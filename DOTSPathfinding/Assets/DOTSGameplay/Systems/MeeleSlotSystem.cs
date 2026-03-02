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
    ///   Each target entity has a MeleeSlotComponent tracking how many
    ///   melee/ranged attackers currently occupy it.
    ///
    ///   When an attacker acquires a melee target, this system assigns the next
    ///   free slot index (0..MaxMeleeSlots-1) and writes MeleeSlotAssignment.
    ///   The orbit angle for slot N is:
    ///       angle = (N / TotalSlots) * 2 * PI
    ///   AIDecisionSystem reads SlotIndex + TotalSlots to compute the actual
    ///   world-space orbit position using the live target transform.
    ///
    ///   When an attacker loses or switches targets, slots are freed and
    ///   attacker counts decremented.
    ///
    /// FIX: Removed ComputeOrbitPositionJob entirely. It was a no-op (only
    /// re-assigned SlotIndex to itself) but it declared MeleeSlotAssignment
    /// via both EnabledRefRO (read) and [WithAll] (which generates a RW handle),
    /// causing the "aliasing" InvalidOperationException. Orbit math is now
    /// handled inline in AIDecisionSystem which has the target transform anyway.
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
            var ecb = new EntityCommandBuffer(Allocator.TempJob);

            // 1. Release slots for attackers that lost or changed their target
            foreach (var (assignment, assignEnabled, currentTarget, weapon, weaponEnabled, entity) in
                SystemAPI.Query<
                    RefRO<MeleeSlotAssignment>,
                    EnabledRefRO<MeleeSlotAssignment>,
                    RefRO<CurrentTarget>,
                    RefRO<Weapon>,
                    EnabledRefRO<Weapon>>()
                    .WithEntityAccess())
            {
                if (!assignEnabled.ValueRO) continue;

                bool targetChanged = currentTarget.ValueRO.HasTarget == 0 ||
                                     currentTarget.ValueRO.TargetEntity != assignment.ValueRO.TargetEntity;
                if (!targetChanged) continue;

                Entity oldTarget = assignment.ValueRO.TargetEntity;
                if (EntityManager.Exists(oldTarget) &&
                    EntityManager.HasComponent<MeleeSlotComponent>(oldTarget))
                {
                    var slots = EntityManager.GetComponentData<MeleeSlotComponent>(oldTarget);
                    bool wasRanged = weaponEnabled.ValueRO &&
                        (weapon.ValueRO.Type == WeaponType.Ranged ||
                         weapon.ValueRO.Type == WeaponType.RangedAOE);

                    if (wasRanged)
                        slots.CurrentRangedAttackers = math.max(0, slots.CurrentRangedAttackers - 1);
                    else
                        slots.CurrentMeleeAttackers = math.max(0, slots.CurrentMeleeAttackers - 1);

                    ecb.SetComponent(oldTarget, slots);
                }

                ecb.SetComponentEnabled<MeleeSlotAssignment>(entity, false);
            }

            // 2. Assign slots for attackers that just acquired a new target
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

                bool isRanged = weaponEnabled.ValueRO &&
                    (weapon.ValueRO.Type == WeaponType.Ranged ||
                     weapon.ValueRO.Type == WeaponType.RangedAOE);

                var slots = EntityManager.GetComponentData<MeleeSlotComponent>(targetEnt);

                if (!isRanged && slots.CurrentMeleeAttackers >= slots.MaxMeleeSlots)
                    continue; // No melee slot free — keep target but don't assign slot yet

                int slotIndex;
                int totalSlots;
                if (isRanged)
                {
                    slotIndex = slots.CurrentRangedAttackers;
                    totalSlots = 8;
                    slots.CurrentRangedAttackers++;
                }
                else
                {
                    slotIndex = slots.CurrentMeleeAttackers;
                    totalSlots = slots.MaxMeleeSlots;
                    slots.CurrentMeleeAttackers++;
                }

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
        }
    }
}