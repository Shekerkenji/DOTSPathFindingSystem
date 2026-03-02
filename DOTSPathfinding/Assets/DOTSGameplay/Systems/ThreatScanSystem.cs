using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace Shek.ECSGameplay
{
    /// <summary>
    /// Scans for enemies within detection radius, scores each candidate,
    /// and assigns the best target to CurrentTarget.
    ///
    /// FIX 1: AllUnits is copied into NativeArray(Allocator.TempJob) before
    /// ScheduleParallel so the scheduler owns the buffer lifetime.
    /// The source NativeList (Allocator.Temp) is disposed immediately after copy.
    ///
    /// FIX 2: Removed unused _candidates and _scanners fields.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(AIDecisionSystem))]
    public partial class ThreatScanSystem : SystemBase
    {
        private NativeHashSet<LoSPair> _visiblePairs;

        protected override void OnCreate()
        {
            RequireForUpdate<DetectionComponent>();
            _visiblePairs = new NativeHashSet<LoSPair>(512, Allocator.Persistent);
        }

        protected override void OnDestroy()
        {
            if (_visiblePairs.IsCreated) _visiblePairs.Dispose();
        }

        protected override void OnUpdate()
        {
            float time = (float)SystemAPI.Time.ElapsedTime;

            // 1. Collect all live unit snapshots
            int unitCount = 0;
            foreach (var _ in SystemAPI.Query<RefRO<CurrentTarget>>().WithDisabled<DeadTag>())
                unitCount++;

            if (unitCount == 0) return;

            var allUnitsTemp = new NativeList<UnitSnapshot>(unitCount, Allocator.Temp);

            foreach (var (transform, unitData, health, slots, entity) in
                SystemAPI.Query<
                    RefRO<LocalTransform>,
                    RefRO<UnitData>,
                    RefRO<HealthComponent>,
                    RefRO<MeleeSlotComponent>>()
                    .WithDisabled<DeadTag>()
                    .WithEntityAccess())
            {
                allUnitsTemp.Add(new UnitSnapshot
                {
                    Entity = entity,
                    Position = transform.ValueRO.Position,
                    FactionId = unitData.ValueRO.FactionId,
                    Radius = unitData.ValueRO.Radius,
                    HealthFrac = (float)health.ValueRO.Current / math.max(1, health.ValueRO.Max),
                    MeleeSlots = slots.ValueRO.CurrentMeleeAttackers,
                    MaxMeleeSlots = slots.ValueRO.MaxMeleeSlots,
                });
            }

            // 2. Main-thread LoS raycasts (Physics API is managed, must run here)
            _visiblePairs.Clear();

            foreach (var (transform, detection, weapon, entity) in
                SystemAPI.Query<
                    RefRO<LocalTransform>,
                    RefRO<DetectionComponent>,
                    RefRO<Weapon>>()
                    .WithDisabled<DeadTag>()
                    .WithEntityAccess())
            {
                bool isRanged = weapon.ValueRO.Type == WeaponType.Ranged ||
                                weapon.ValueRO.Type == WeaponType.RangedAOE;
                if (!isRanged) continue;
                if (time < detection.ValueRO.NextScanTime) continue;

                float3 fromPos = transform.ValueRO.Position + new float3(0, 1f, 0);
                int layerMask = detection.ValueRO.ObstacleLayers;
                float radius = detection.ValueRO.DetectionRadius;

                for (int i = 0; i < allUnitsTemp.Length; i++)
                {
                    var candidate = allUnitsTemp[i];
                    if (candidate.Entity == entity) continue;
                    float dist = math.distance(fromPos, candidate.Position);
                    if (dist > radius) continue;

                    float3 toPos = candidate.Position + new float3(0, 1f, 0);
                    float3 dir3 = toPos - fromPos;
                    bool clear = !Physics.Raycast(
                        new Vector3(fromPos.x, fromPos.y, fromPos.z),
                        new Vector3(dir3.x, dir3.y, dir3.z).normalized,
                        dist, layerMask);

                    if (clear)
                        _visiblePairs.Add(new LoSPair { Scanner = entity, Target = candidate.Entity });
                }
            }

            // 3. Copy to TempJob-allocated NativeArray before scheduling.
            //    Allocator.Temp is only valid until the end of this frame's stack scope;
            //    the job may not have started yet when the Temp allocator resets.
            //    Allocator.TempJob persists for 4 frames and is safe across job boundaries.
            var allUnits = new NativeArray<UnitSnapshot>(allUnitsTemp.Length, Allocator.TempJob);
            allUnits.CopyFrom(allUnitsTemp.AsArray());
            allUnitsTemp.Dispose();

            var ecb = new EntityCommandBuffer(Allocator.TempJob);

            var job = new ScoreAndAssignJob
            {
                AllUnits = allUnits,
                VisiblePairs = _visiblePairs,
                Time = time,
                ECBWriter = ecb.AsParallelWriter()
            };
            Dependency = job.ScheduleParallel(Dependency);
            Dependency.Complete();

            ecb.Playback(EntityManager);
            ecb.Dispose();
            allUnits.Dispose();
        }

        // -----------------------------------------------------------------
        // SCORE AND ASSIGN JOB
        // -----------------------------------------------------------------

        [BurstCompile]
        [WithDisabled(typeof(DeadTag))]
        partial struct ScoreAndAssignJob : IJobEntity
        {
            [ReadOnly] public NativeArray<UnitSnapshot> AllUnits;
            [ReadOnly] public NativeHashSet<LoSPair> VisiblePairs;
            public float Time;
            public EntityCommandBuffer.ParallelWriter ECBWriter;

            void Execute(
                [ChunkIndexInQuery] int sortKey,
                Entity entity,
                ref DetectionComponent detection,
                ref CurrentTarget currentTarget,
                in LocalTransform transform,
                in UnitData unitData,
                in Weapon weapon,
                in AIState aiState)
            {
                if (aiState.State == UnitState.Dead) return;
                if (Time < detection.NextScanTime) return;

                detection.NextScanTime = Time + detection.ScanInterval;

                bool isRanged = weapon.Type == WeaponType.Ranged || weapon.Type == WeaponType.RangedAOE;
                float scanRadius = detection.DetectionRadius;

                float bestScore = float.MaxValue;
                Entity bestEntity = Entity.Null;
                float3 bestPos = float3.zero;

                for (int i = 0; i < AllUnits.Length; i++)
                {
                    var candidate = AllUnits[i];
                    if (candidate.Entity == entity) continue;
                    if (candidate.FactionId == unitData.FactionId) continue;

                    float dist = math.distance(transform.Position, candidate.Position);
                    if (dist > scanRadius) continue;

                    if (isRanged)
                    {
                        var pair = new LoSPair { Scanner = entity, Target = candidate.Entity };
                        if (!VisiblePairs.Contains(pair)) continue;
                    }

                    float meleePressure = candidate.MaxMeleeSlots > 0
                        ? (float)candidate.MeleeSlots / candidate.MaxMeleeSlots : 0f;

                    float score = dist
                                - meleePressure * 30f
                                - (1f - candidate.HealthFrac) * 20f;

                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestEntity = candidate.Entity;
                        bestPos = candidate.Position;
                    }
                }

                // Validate existing target still in chase range
                if (currentTarget.HasTarget == 1)
                {
                    bool valid = false;
                    for (int i = 0; i < AllUnits.Length; i++)
                    {
                        if (AllUnits[i].Entity != currentTarget.TargetEntity) continue;
                        if (math.distance(transform.Position, AllUnits[i].Position) <= detection.ChaseRange)
                            valid = true;
                        break;
                    }
                    if (!valid)
                    {
                        currentTarget.HasTarget = 0;
                        currentTarget.TargetEntity = Entity.Null;
                    }
                }

                // Hysteresis: only switch if new target scores 15+ better
                if (bestEntity != Entity.Null)
                {
                    bool shouldSwitch = currentTarget.HasTarget == 0;

                    if (!shouldSwitch && bestEntity != currentTarget.TargetEntity)
                    {
                        float currentScore = float.MaxValue;
                        for (int i = 0; i < AllUnits.Length; i++)
                        {
                            if (AllUnits[i].Entity != currentTarget.TargetEntity) continue;
                            float dist = math.distance(transform.Position, AllUnits[i].Position);
                            float mp = AllUnits[i].MaxMeleeSlots > 0
                                ? (float)AllUnits[i].MeleeSlots / AllUnits[i].MaxMeleeSlots : 0f;
                            currentScore = dist - mp * 30f - (1f - AllUnits[i].HealthFrac) * 20f;
                            break;
                        }
                        shouldSwitch = (currentScore - bestScore) > 15f;
                    }

                    if (shouldSwitch)
                    {
                        currentTarget.TargetEntity = bestEntity;
                        currentTarget.LastKnownPosition = bestPos;
                        currentTarget.HasTarget = 1;
                    }
                }

                ECBWriter.SetComponent(sortKey, entity, currentTarget);
            }
        }

        // -----------------------------------------------------------------
        // HELPER TYPES
        // -----------------------------------------------------------------

        public struct UnitSnapshot
        {
            public Entity Entity;
            public float3 Position;
            public int FactionId;
            public float Radius;
            public float HealthFrac;
            public int MeleeSlots;
            public int MaxMeleeSlots;
        }

        public struct LoSPair : System.IEquatable<LoSPair>
        {
            public Entity Scanner;
            public Entity Target;
            public bool Equals(LoSPair other) => Scanner == other.Scanner && Target == other.Target;
            public override int GetHashCode() => Scanner.GetHashCode() * 397 ^ Target.GetHashCode();
        }
    }
}