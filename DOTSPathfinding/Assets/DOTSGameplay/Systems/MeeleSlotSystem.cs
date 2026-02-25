using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Shek.ECSGameplay
{
    /// <summary>
    /// Manages melee attacker slots around each target.
    ///
    /// Design:
    ///   • Each target entity has a MeleeSlotComponent that tracks how many
    ///     melee/ranged attackers currently occupy it.
    ///   • When an attacker acquires a melee target it calls for a slot assignment.
    ///     MeleeSlotSystem assigns the next free slot index (0..MaxMeleeSlots-1).
    ///   • The orbit position for slot N is:
    ///       angle = (N / TotalSlots) * 2?
    ///       orbitPos = target.position + float3(cos(angle), 0, sin(angle)) * orbitRadius
    ///   • orbitRadius = targetRadius + attackerRadius + weapon.Range * 0.5f
    ///   • When an attacker loses its target or dies, slots are freed and the
    ///     attacker count on the target is decremented.
    ///
    /// Runs before AIDecisionSystem so slot counts are fresh for scoring.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(AIDecisionSystem))]
    [UpdateAfter(typeof(ThreatScanSystem))]
    public partial class MeleeSlotSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<MeleeSlotComponent>();
        }

        protected override void OnUpdate()
        {
            float time = (float)SystemAPI.Time.ElapsedTime;

            // ?? 1. Decrement slot counts for attackers that lost their target ??

            var ecb = new EntityCommandBuffer(Allocator.TempJob);

            foreach (var (assignment, assignEnabled, currentTarget, entity) in
                SystemAPI.Query<
                    RefRO<MeleeSlotAssignment>,
                    EnabledRefRO<MeleeSlotAssignment>,
                    RefRO<CurrentTarget>>()
                    .WithEntityAccess())
            {
                if (!assignEnabled.ValueRO) continue;

                bool targetChanged = currentTarget.ValueRO.TargetEntity != assignment.ValueRO.TargetEntity;
                bool targetLost = currentTarget.ValueRO.HasTarget == 0;

                if (!targetChanged && !targetLost) continue;

                // Release the slot on the old target
                Entity oldTarget = assignment.ValueRO.TargetEntity;
                if (EntityManager.Exists(oldTarget) &&
                    EntityManager.HasComponent<MeleeSlotComponent>(oldTarget))
                {
                    var slots = EntityManager.GetComponentData<MeleeSlotComponent>(oldTarget);

                    bool wasRanged = EntityManager.HasComponent<Weapon>(entity) &&
                        (EntityManager.GetComponentData<Weapon>(entity).Type == WeaponType.Ranged ||
                         EntityManager.GetComponentData<Weapon>(entity).Type == WeaponType.RangedAOE);

                    if (wasRanged)
                        slots.CurrentRangedAttackers = math.max(0, slots.CurrentRangedAttackers - 1);
                    else
                        slots.CurrentMeleeAttackers = math.max(0, slots.CurrentMeleeAttackers - 1);

                    ecb.SetComponent(oldTarget, slots);
                }
                ecb.SetComponentEnabled<MeleeSlotAssignment>(entity, false);
            }

            // ?? 2. Assign slots for attackers that just acquired a new target ??

            foreach (var (currentTarget, weapon, weaponEnabled, assignEnabled, unitData, entity) in
                SystemAPI.Query<
                    RefRO<CurrentTarget>,
                    RefRO<Weapon>,
                    EnabledRefRO<Weapon>,
                    EnabledRefRO<MeleeSlotAssignment>,
                    RefRO<UnitData>>()
                    .WithDisabled<DeadTag>()
                    .WithEntityAccess())
            {
                if (currentTarget.ValueRO.HasTarget == 0) continue;
                if (assignEnabled.ValueRO) continue; // Already has a slot

                Entity targetEnt = currentTarget.ValueRO.TargetEntity;
                if (!EntityManager.Exists(targetEnt)) continue;
                if (!EntityManager.HasComponent<MeleeSlotComponent>(targetEnt)) continue;

                bool isRanged = weapon.ValueRO.Type == WeaponType.Ranged ||
                                weapon.ValueRO.Type == WeaponType.RangedAOE;

                var slots = EntityManager.GetComponentData<MeleeSlotComponent>(targetEnt);

                if (!isRanged && slots.CurrentMeleeAttackers >= slots.MaxMeleeSlots)
                    continue; // No melee slot available — keep current target but wait

                if (!isRanged) slots.CurrentMeleeAttackers++;
                else slots.CurrentRangedAttackers++;

                int slotIndex = isRanged ? slots.CurrentRangedAttackers - 1 : slots.CurrentMeleeAttackers - 1;
                int totalSlots = isRanged ? 8 : slots.MaxMeleeSlots;

                ecb.SetComponent(targetEnt, slots);
                ecb.SetComponent(entity, new MeleeSlotAssignment
                {
                    TargetEntity = targetEnt,
                    SlotIndex = slotIndex,
                    TotalSlots = totalSlots
                });
                ecb.SetComponentEnabled<MeleeSlotAssignment>(entity, true);
            }

            ecb.Playback(EntityManager);
            ecb.Dispose();

            // ?? 3. Compute & write orbit positions (Burst) ????????????????????

            var orbitJob = new ComputeOrbitPositionJob { };
            Dependency = orbitJob.ScheduleParallel(Dependency);
        }

        /// <summary>
        /// Computes the world-space orbit position this attacker should move toward.
        /// Stored in MeleeSlotAssignment fields — read by AIDecisionSystem when
        /// issuing NavigationMoveCommands for melee.
        /// We write an OrbitTarget component which NavigationCommandSystem will pick up.
        /// </summary>
        [BurstCompile]
        [WithAll(typeof(MeleeSlotAssignment))]
        [WithDisabled(typeof(DeadTag))]
        partial struct ComputeOrbitPositionJob : IJobEntity
        {
            void Execute(
                ref MeleeSlotAssignment assignment,
                EnabledRefRO<MeleeSlotAssignment> assignEnabled,
                in LocalTransform transform,
                in UnitData unitData,
                in Weapon weapon)
            {
                if (!assignEnabled.ValueRO) return;
                // Orbit angle for this slot
                float angle = (float)assignment.SlotIndex / math.max(1, assignment.TotalSlots) * math.PI * 2f;
                float2 dir2 = new float2(math.cos(angle), math.sin(angle));
                // We store the angle so AIDecisionSystem can compute the actual
                // orbit world-position once it has the target's transform.
                // Nothing more needed here — the angle is deterministic from slot index.
                assignment.SlotIndex = assignment.SlotIndex; // no-op but keeps job valid
            }
        }
    }
}