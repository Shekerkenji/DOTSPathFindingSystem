using Shek.ECSAnimation;
using Shek.ECSGamePlay;
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
            foreach (var (controller, startedEnabled) in
                SystemAPI.Query<RefRW<AnimationController>, EnabledRefRO<StartedMoving>>())
            {

                AnimationControllerAPI.Play(ref controller.ValueRW, 1);
            }

            foreach (var (controller, stoppedEnabled ) in
                SystemAPI.Query<RefRW<AnimationController>, EnabledRefRO<StoppedMoving>>())
            {
  
                AnimationControllerAPI.Play(ref controller.ValueRW, 0);
            }
        }
    }
}