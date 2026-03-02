using Shek.ECSAnimation;
using Shek.ECSGameplay;
using Shek.ECSNavigation;
using Unity.Burst;
using Unity.Entities;

namespace Shek.ECSEventSystem
{
    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(AnimationSamplingSystem))]
    [UpdateAfter(typeof(MovementEventSystem))]
    public partial struct MovementAnimationSystem : ISystem
    {
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            foreach (var (controller, startedEnabled, aiState) in
                SystemAPI.Query<RefRW<AnimationController>, EnabledRefRO<StartedMoving>, RefRO<AIState>>())
            {
                if (aiState.ValueRO.State == UnitState.Attacking) continue;
                if (aiState.ValueRO.State == UnitState.Dead) continue;
                AnimationControllerAPI.Play(ref controller.ValueRW, 1);
            }

            foreach (var (controller, stoppedEnabled, aiState) in
                SystemAPI.Query<RefRW<AnimationController>, EnabledRefRO<StoppedMoving>, RefRO<AIState>>())
            {
                if (aiState.ValueRO.State == UnitState.Attacking) continue;
                if (aiState.ValueRO.State == UnitState.Dead) continue;
                AnimationControllerAPI.Play(ref controller.ValueRW, 0);
            }
        }
    }
}