using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using Shek.ECSNavigation;

namespace Shek.ECSGameplay
{
    /// <summary>
    /// Central AI brain. Runs after ThreatScan and MeleeSlot systems so decisions
    /// are based on fresh target data.
    ///
    /// Per-unit logic each frame:
    ///  1. If Dead → skip everything.
    ///  2. If no target → Idle, stop moving.
    ///  3. If target exists:
    ///     a. Check if in attack range → transition to Attacking.
    ///     b. Melee: navigate to orbit position around target.
    ///     c. Ranged: navigate within weapon range, then stop and shoot.
    ///  4. Issue NavigationMoveCommand / NavigationStopCommand via ECB.
    ///  5. Fire AttackHitEvent when cooldown expires and in range.
    ///
    /// Navigation commands are issued through the ECS command interface defined
    /// in NavigationCommandSystem — pure data, no MonoBehaviour calls.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(MeleeSlotSystem))]
    public partial class AIDecisionSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<AIState>();
        }

        protected override void OnUpdate()
        {
            float time = (float)SystemAPI.Time.ElapsedTime;
            float dt = SystemAPI.Time.DeltaTime;

            // Snapshot target positions for look-up inside Burst job
            // (we can't access component data from arbitrary entities in a Burst parallel job
            //  without ComponentLookup)
            var transformLookup = SystemAPI.GetComponentLookup<LocalTransform>(true);
            var unitDataLookup = SystemAPI.GetComponentLookup<UnitData>(true);
            var meleeSlotLookup = SystemAPI.GetComponentLookup<MeleeSlotComponent>(true);
            var healthLookup = SystemAPI.GetComponentLookup<HealthComponent>(true);
            var deadLookup = SystemAPI.GetComponentLookup<DeadTag>(true);

            var ecb = new EntityCommandBuffer(Allocator.TempJob);

            var job = new AIDecisionJob
            {
                Time = time,
                DeltaTime = dt,
                TransformLookup = transformLookup,
                UnitDataLookup = unitDataLookup,
                MeleeSlotLookup = meleeSlotLookup,
                HealthLookup = healthLookup,
                DeadLookup = deadLookup,
                ECBWriter = ecb.AsParallelWriter()
            };

            Dependency = job.ScheduleParallel(Dependency);
            Dependency.Complete();

            ecb.Playback(EntityManager);
            ecb.Dispose();
        }

        [BurstCompile]
        [WithDisabled(typeof(DeadTag))]
        partial struct AIDecisionJob : IJobEntity
        {
            public float Time;
            public float DeltaTime;

            [ReadOnly] public ComponentLookup<LocalTransform> TransformLookup;
            [ReadOnly] public ComponentLookup<UnitData> UnitDataLookup;
            [ReadOnly] public ComponentLookup<MeleeSlotComponent> MeleeSlotLookup;
            [ReadOnly] public ComponentLookup<HealthComponent> HealthLookup;
            [ReadOnly] public ComponentLookup<DeadTag> DeadLookup;

            public EntityCommandBuffer.ParallelWriter ECBWriter;

            void Execute(
                [ChunkIndexInQuery] int sortKey,
                Entity entity,
                ref AIState aiState,
                ref AttackComponent attack,
                ref CurrentTarget currentTarget,
                in LocalTransform transform,
                in UnitData unitData,
                in Weapon weapon,
                in MeleeSlotAssignment slotAssignment,
                EnabledRefRO<MeleeSlotAssignment> slotEnabled,
                in DetectionComponent detection)
            {
                aiState.StateTimer += DeltaTime;

                // ── Dead guard ─────────────────────────────────────────────
                if (aiState.State == UnitState.Dead) return;

                // ── No target → Idle ───────────────────────────────────────
                if (currentTarget.HasTarget == 0)
                {
                    if (aiState.State != UnitState.Idle)
                    {
                        aiState.State = UnitState.Idle;
                        aiState.StateTimer = 0f;
                        ECBWriter.SetComponentEnabled<NavigationStopCommand>(sortKey, entity, true);
                    }
                    return;
                }

                // ── Validate target still alive ────────────────────────────
                Entity targetEnt = currentTarget.TargetEntity;
                if (!TransformLookup.HasComponent(targetEnt) ||
                    (DeadLookup.HasComponent(targetEnt) && DeadLookup.IsComponentEnabled(targetEnt)))
                {
                    currentTarget.HasTarget = 0;
                    currentTarget.TargetEntity = Entity.Null;
                    aiState.State = UnitState.Idle;
                    aiState.StateTimer = 0f;
                    ECBWriter.SetComponentEnabled<NavigationStopCommand>(sortKey, entity, true);
                    return;
                }

                float3 myPos = transform.Position;
                float3 targetPos = TransformLookup[targetEnt].Position;
                currentTarget.LastKnownPosition = targetPos;

                float targetRadius = UnitDataLookup.HasComponent(targetEnt)
                    ? UnitDataLookup[targetEnt].Radius : 0.5f;

                float distToTarget = math.distance(myPos, targetPos);
                float effectiveRange = weapon.Range + unitData.Radius + targetRadius;

                bool isRanged = weapon.Type == WeaponType.Ranged || weapon.Type == WeaponType.RangedAOE;

                // ── Determine desired position ─────────────────────────────

                float3 desiredPos;
                bool inAttackRange;

                if (isRanged)
                {
                    // Ranged: stop at weapon.Range distance from target edge
                    float stopDist = weapon.Range + unitData.Radius + targetRadius - 0.2f;
                    inAttackRange = distToTarget <= effectiveRange;

                    float3 toTarget = targetPos - myPos;
                    float3 toTargetFlat = new float3(toTarget.x, 0, toTarget.z);
                    float flatDist = math.length(toTargetFlat);

                    if (flatDist > 0.001f)
                    {
                        float3 dir = toTargetFlat / flatDist;
                        desiredPos = targetPos - dir * stopDist;
                        desiredPos.y = myPos.y;
                    }
                    else
                    {
                        desiredPos = myPos;
                    }
                }
                else
                {
                    // Melee: orbit to assigned slot position
                    float orbitRadius = unitData.Radius + targetRadius + weapon.Range * 0.5f;
                    float angle = slotEnabled.ValueRO
                        ? (float)slotAssignment.SlotIndex / math.max(1, slotAssignment.TotalSlots) * math.PI * 2f
                        : 0f;

                    float3 orbitOffset = new float3(math.cos(angle), 0f, math.sin(angle)) * orbitRadius;
                    desiredPos = targetPos + orbitOffset;
                    desiredPos.y = myPos.y;

                    inAttackRange = distToTarget <= effectiveRange + 0.5f;
                }

                // ── State machine ──────────────────────────────────────────

                float distToDesired = math.distance(myPos, desiredPos);
                const float arrivalEpsilon = 0.5f;

                if (inAttackRange)
                {
                    // In range — stop and attack
                    if (aiState.State == UnitState.Moving)
                    {
                        aiState.State = UnitState.Attacking;
                        aiState.StateTimer = 0f;
                        ECBWriter.SetComponentEnabled<NavigationStopCommand>(sortKey, entity, true);
                    }
                    else if (aiState.State != UnitState.Attacking)
                    {
                        aiState.State = UnitState.Attacking;
                        aiState.StateTimer = 0f;
                    }

                    // Fire attack if cooldown elapsed
                    if (Time >= attack.LastAttackTime + attack.AttackCooldown)
                    {
                        attack.LastAttackTime = Time;

                        // Compute damage
                        int dmg = (int)math.round(attack.BaseDamage * weapon.DamageMult);

                        // Fire AttackHitEvent on self
                        ECBWriter.SetComponent(sortKey, entity, new AttackHitEvent
                        {
                            HitTarget = targetEnt,
                            Damage = dmg
                        });
                        ECBWriter.SetComponentEnabled<AttackHitEvent>(sortKey, entity, true);

                        // Fire DamageReceivedEvent on target
                        ECBWriter.SetComponent(sortKey, targetEnt, new DamageReceivedEvent
                        {
                            Attacker = entity,
                            Damage = dmg
                        });
                        ECBWriter.SetComponentEnabled<DamageReceivedEvent>(sortKey, targetEnt, true);
                    }
                }
                else
                {
                    // Out of range — navigate toward desired position
                    if (aiState.State != UnitState.Moving || distToDesired > arrivalEpsilon)
                    {
                        aiState.State = UnitState.Moving;
                        aiState.StateTimer = 0f;

                        ECBWriter.SetComponent(sortKey, entity, new NavigationMoveCommand
                        {
                            Destination = desiredPos,
                            Priority = 1
                        });
                        ECBWriter.SetComponentEnabled<NavigationMoveCommand>(sortKey, entity, true);
                    }
                }
            }
        }
    }
}