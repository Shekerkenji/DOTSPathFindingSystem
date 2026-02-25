using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Shek.ECSNavigation;

namespace Shek.ECSGameplay
{
    /// <summary>
    /// Processes DamageReceivedEvent components:
    ///   1. Applies damage to HealthComponent.
    ///   2. Resets out-of-combat timer.
    ///   3. Enables DeadTag if HP drops to 0.
    ///   4. Issues NavigationStopCommand and disables movement on death.
    ///
    /// Also handles out-of-combat health regeneration each frame.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(AIDecisionSystem))]
    public partial struct DamageSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<HealthComponent>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            float time = (float)SystemAPI.Time.ElapsedTime;
            float dt = SystemAPI.Time.DeltaTime;

            var ecb = new EntityCommandBuffer(Allocator.TempJob);

            // ?? Apply incoming damage ??????????????????????????????????????

            foreach (var (health, dmgEvent, dmgEnabled, aiState, entity) in
                SystemAPI.Query<
                    RefRW<HealthComponent>,
                    RefRO<DamageReceivedEvent>,
                    EnabledRefRO<DamageReceivedEvent>,
                    RefRW<AIState>>()
                    .WithEntityAccess())
            {
                if (!dmgEnabled.ValueRO) continue;

                int incoming = dmgEvent.ValueRO.Damage;
                health.ValueRW.Current = math.max(0, health.ValueRO.Current - incoming);
                health.ValueRW.TimeSinceLastDamage = 0f;

                // Signal hit animation (non-dead)
                if (health.ValueRO.Current > 0 && aiState.ValueRO.State != UnitState.Dead)
                {
                    aiState.ValueRW.State = UnitState.Hit;
                    aiState.ValueRW.StateTimer = 0f;
                }

                // Death
                if (health.ValueRO.Current <= 0 && aiState.ValueRO.State != UnitState.Dead)
                {
                    aiState.ValueRW.State = UnitState.Dead;
                    ecb.SetComponentEnabled<DeadTag>(entity, true);
                    ecb.SetComponentEnabled<NavigationStopCommand>(entity, true);
                    // Release melee slot
                    ecb.SetComponentEnabled<MeleeSlotAssignment>(entity, false);
                }

                // Consume event
                ecb.SetComponentEnabled<DamageReceivedEvent>(entity, false);
            }

            // ?? Regeneration ???????????????????????????????????????????????

            foreach (var (health, aiState) in
                SystemAPI.Query<RefRW<HealthComponent>, RefRO<AIState>>()
                    .WithDisabled<DeadTag>())
            {
                if (aiState.ValueRO.State == UnitState.Dead) continue;

                health.ValueRW.TimeSinceLastDamage += dt;

                if (health.ValueRO.TimeSinceLastDamage >= health.ValueRO.OutOfCombatDelay &&
                    health.ValueRO.Current < health.ValueRO.Max)
                {
                    health.ValueRW.Current = math.min(
                        health.ValueRO.Max,
                        health.ValueRO.Current + (int)math.round(health.ValueRO.RegenRate * dt));
                }
            }

            // ?? AttackHitEvent cleanup ?????????????????????????????????????
            // (events consumed in same frame by anything listening;
            //  clean them up here so they don't persist)
            foreach (var (_, hitEnabled, entity) in
                SystemAPI.Query<RefRO<AttackHitEvent>, EnabledRefRO<AttackHitEvent>>()
                    .WithEntityAccess())
            {
                if (!hitEnabled.ValueRO) continue;
                ecb.SetComponentEnabled<AttackHitEvent>(entity, false);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }

    /// <summary>
    /// Clears Hit state after the animation duration expires.
    /// Hit (animation clip 3) is expected to be ~0.4 s long.
    /// After that, the unit returns to Idle or Attacking depending on whether
    /// it still has a target.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(DamageSystem))]
    public partial struct HitRecoverySystem : ISystem
    {
        private const float HitAnimDuration = 0.4f;

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            float dt = SystemAPI.Time.DeltaTime;

            foreach (var (aiState, currentTarget) in
                SystemAPI.Query<RefRW<AIState>, RefRO<CurrentTarget>>()
                    .WithDisabled<DeadTag>())
            {
                if (aiState.ValueRO.State != UnitState.Hit) continue;

                aiState.ValueRW.StateTimer += dt;
                if (aiState.ValueRO.StateTimer < HitAnimDuration) continue;

                // Recover
                aiState.ValueRW.StateTimer = 0f;
                aiState.ValueRW.State = currentTarget.ValueRO.HasTarget == 1
                    ? UnitState.Attacking
                    : UnitState.Idle;
            }
        }
    }
}