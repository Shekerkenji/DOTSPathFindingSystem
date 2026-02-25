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
    /// Scoring formula (lower = better target):
    ///   score = dist * 1.0
    ///         - meleePressure * 30        (prefer targets with fewer attackers)
    ///         - lowHealthBonus * 20       (finish off weakened enemies)
    ///         + los_penalty               (large penalty if LoS fails for ranged)
    ///
    /// LoS is checked via Physics.Raycast on the main thread before scheduling
    /// the Burst job. The Burst job receives a pre-computed NativeHashSet of
    /// visible entity pairs so it stays fully Burst-compiled.
    ///
    /// Runs in InitializationSystemGroup so results are ready before AI decisions.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(AIDecisionSystem))]
    public partial class ThreatScanSystem : SystemBase
    {
        // Main-thread LoS results written here, read by inner Burst job.
        private NativeHashSet<LoSPair> _visiblePairs;
        private NativeArray<TargetCandidate> _candidates;
        private NativeArray<ScannerData> _scanners;

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

            // ── 1. Collect all live unit positions & faction data ──────────────

            int unitCount = 0;
            foreach (var _ in SystemAPI.Query<RefRO<CurrentTarget>>()) unitCount++;

            if (unitCount == 0) return;

            var allUnits = new NativeList<UnitSnapshot>(unitCount, Allocator.Temp);
            foreach (var (transform, unitData, health, slots, meleeAssign, meleeEnabled, entity) in
                SystemAPI.Query<
                    RefRO<LocalTransform>,
                    RefRO<UnitData>,
                    RefRO<HealthComponent>,
                    RefRO<MeleeSlotComponent>,
                    RefRO<MeleeSlotAssignment>,
                    EnabledRefRO<MeleeSlotAssignment>>()
                    .WithDisabled<DeadTag>()
                    .WithEntityAccess())
            {
                allUnits.Add(new UnitSnapshot
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

            // ── 2. Main-thread LoS raycasts (managed Physics API) ─────────────

            _visiblePairs.Clear();

            foreach (var (transform, detection, target, weapon, entity) in
                SystemAPI.Query<
                    RefRO<LocalTransform>,
                    RefRO<DetectionComponent>,
                    RefRO<CurrentTarget>,
                    RefRO<Weapon>>()
                    .WithDisabled<DeadTag>()
                    .WithEntityAccess())
            {
                if (time < detection.ValueRO.NextScanTime) continue;
                bool isRanged = weapon.ValueRO.Type == WeaponType.Ranged ||
                                weapon.ValueRO.Type == WeaponType.RangedAOE;
                if (!isRanged) continue;  // Melee doesn't need LoS

                float3 fromPos = transform.ValueRO.Position + new float3(0, 1f, 0);
                int layerMask = detection.ValueRO.ObstacleLayers;
                float radius = detection.ValueRO.DetectionRadius;

                for (int i = 0; i < allUnits.Length; i++)
                {
                    var candidate = allUnits[i];
                    if (candidate.Entity == entity) continue;
                    float dist = math.distance(fromPos, candidate.Position);
                    if (dist > radius) continue;

                    float3 toPos = candidate.Position + new float3(0, 1f, 0);
                    float3 dir3 = toPos - fromPos;
                    var origin3 = new Vector3(fromPos.x, fromPos.y, fromPos.z);
                    var dir3u = new Vector3(dir3.x, dir3.y, dir3.z);

                    bool clear = !Physics.Raycast(origin3, dir3u.normalized, dist, layerMask);
                    if (clear)
                        _visiblePairs.Add(new LoSPair { Scanner = entity, Target = candidate.Entity });
                }
            }

            // ── 3. Burst job: score & assign ──────────────────────────────────

            var ecb = new EntityCommandBuffer(Allocator.TempJob);

            var job = new ScoreAndAssignJob
            {
                AllUnits = allUnits.AsArray(),
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

        // ─────────────────────────────────────────────────────────────────────
        // INNER JOB
        // ─────────────────────────────────────────────────────────────────────

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
                    if (candidate.FactionId == unitData.FactionId) continue; // Same faction

                    float dist = math.distance(transform.Position, candidate.Position);
                    if (dist > scanRadius) continue;

                    // LoS check for ranged — melee ignores LoS since they navigate to target
                    if (isRanged)
                    {
                        var pair = new LoSPair { Scanner = entity, Target = candidate.Entity };
                        if (!VisiblePairs.Contains(pair)) continue;
                    }

                    // Scoring: lower is better
                    float meleePressure = candidate.MaxMeleeSlots > 0
                        ? (float)candidate.MeleeSlots / candidate.MaxMeleeSlots
                        : 0f;

                    float score = dist
                                - meleePressure * 30f        // Prefer targets with open melee slots
                                - (1f - candidate.HealthFrac) * 20f; // Prefer wounded targets

                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestEntity = candidate.Entity;
                        bestPos = candidate.Position;
                    }
                }

                // Check if current target is still alive and in range
                if (currentTarget.HasTarget == 1)
                {
                    bool currentStillValid = false;
                    for (int i = 0; i < AllUnits.Length; i++)
                    {
                        if (AllUnits[i].Entity != currentTarget.TargetEntity) continue;
                        float dist = math.distance(transform.Position, AllUnits[i].Position);
                        if (dist <= detection.ChaseRange) currentStillValid = true;
                        break;
                    }
                    if (!currentStillValid)
                    {
                        currentTarget.HasTarget = 0;
                        currentTarget.TargetEntity = Entity.Null;
                    }
                }

                // Only switch targets if a significantly better one is found
                // (hysteresis: new target must score 15+ better than current)
                if (bestEntity != Entity.Null)
                {
                    bool shouldSwitch = currentTarget.HasTarget == 0;
                    if (!shouldSwitch && bestEntity != currentTarget.TargetEntity)
                    {
                        // Compute current target's score for hysteresis comparison
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

        // ─────────────────────────────────────────────────────────────────────
        // HELPER TYPES
        // ─────────────────────────────────────────────────────────────────────

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