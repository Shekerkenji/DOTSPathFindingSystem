using Unity.Burst;
using Unity.Entities;
using Navigation.ECS;
using DOTSAnimation;

namespace DOTSEventSystem
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
            foreach (var (controller, startedEnabled, entity) in
                SystemAPI.Query<RefRW<AnimationController>, EnabledRefRO<StartedMoving>>()
                    .WithEntityAccess())
            {
                AnimationControllerAPI.Play(ref controller.ValueRW, 1);
            }

            foreach (var (controller, stoppedEnabled, entity) in
                SystemAPI.Query<RefRW<AnimationController>, EnabledRefRO<StoppedMoving>>()
                    .WithEntityAccess())
            {
                AnimationControllerAPI.Play(ref controller.ValueRW, 0);
            }
        }
    }
}