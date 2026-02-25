using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Shek.ECSGameplay
{
    /// <summary>
    /// When a unit acquires a new target it pings allies within PingRadius.
    /// Allies without a current target that are within range receive the
    /// same target assignment — simulating squad-level awareness.
    ///
    /// To avoid the ping triggering every frame, it fires only when
    /// CurrentTarget.HasTarget transitions from 0→1, tracked via a local
    /// "just acquired" flag we reuse the StateTimer for (StateTimer == 0
    /// on the same frame the target was first assigned).
    ///
    /// Runs after ThreatScanSystem (which does the initial target assignment)
    /// and before AIDecisionSystem (which acts on targets).
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ThreatScanSystem))]
    [UpdateBefore(typeof(AIDecisionSystem))]
    public partial class AllyPingSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<DetectionComponent>();
        }

        protected override void OnUpdate()
        {
            // Collect units that just got a fresh target this frame (StateTimer ≈ 0)
            var freshTargets = new NativeList<PingEntry>(32, Allocator.Temp);

            foreach (var (currentTarget, aiState, transform, unitData, detection) in
                SystemAPI.Query<
                    RefRO<CurrentTarget>,
                    RefRO<AIState>,
                    RefRO<LocalTransform>,
                    RefRO<UnitData>,
                    RefRO<DetectionComponent>>()
                    .WithDisabled<DeadTag>())
            {
                if (currentTarget.ValueRO.HasTarget == 0) continue;
                // StateTimer is reset to 0 by AIDecisionSystem when state changes.
                // A timer < one frame means target was just acquired.
                if (aiState.ValueRO.StateTimer > SystemAPI.Time.DeltaTime * 1.5f) continue;

                freshTargets.Add(new PingEntry
                {
                    PingerPos = transform.ValueRO.Position,
                    PingRadius = detection.ValueRO.PingRadius,
                    FactionId = unitData.ValueRO.FactionId,
                    TargetEntity = currentTarget.ValueRO.TargetEntity,
                    TargetPosition = currentTarget.ValueRO.LastKnownPosition
                });
            }

            if (freshTargets.Length == 0) { freshTargets.Dispose(); return; }

            // Apply pings to idle allies
            var ecb = new EntityCommandBuffer(Allocator.TempJob);
            var pingArray = freshTargets.AsArray();

            var job = new ApplyPingJob
            {
                Pings = pingArray,
                ECBWriter = ecb.AsParallelWriter()
            };
            Dependency = job.ScheduleParallel(Dependency);
            Dependency.Complete();

            ecb.Playback(EntityManager);
            ecb.Dispose();
            freshTargets.Dispose();
        }

        [BurstCompile]
        [WithDisabled(typeof(DeadTag))]
        partial struct ApplyPingJob : IJobEntity
        {
            [ReadOnly] public NativeArray<PingEntry> Pings;
            public EntityCommandBuffer.ParallelWriter ECBWriter;

            void Execute(
                [ChunkIndexInQuery] int sortKey,
                Entity entity,
                ref CurrentTarget currentTarget,
                in LocalTransform transform,
                in UnitData unitData)
            {
                // Only propagate to allies without a current target
                if (currentTarget.HasTarget == 1) return;

                for (int i = 0; i < Pings.Length; i++)
                {
                    var ping = Pings[i];
                    if (ping.FactionId != unitData.FactionId) continue;
                    if (ping.TargetEntity == entity) continue;

                    float dist = math.distance(transform.Position, ping.PingerPos);
                    if (dist > ping.PingRadius) continue;

                    currentTarget.HasTarget = 1;
                    currentTarget.TargetEntity = ping.TargetEntity;
                    currentTarget.LastKnownPosition = ping.TargetPosition;
                    ECBWriter.SetComponent(sortKey, entity, currentTarget);
                    break; // One ping is enough
                }
            }
        }

        private struct PingEntry
        {
            public float3 PingerPos;
            public float PingRadius;
            public int FactionId;
            public Entity TargetEntity;
            public float3 TargetPosition;
        }
    }
}