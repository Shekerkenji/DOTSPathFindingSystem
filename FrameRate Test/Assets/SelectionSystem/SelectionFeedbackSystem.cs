using Unity.Burst;
using Unity.Entities;
using Unity.Rendering;

[WorldSystemFilter(WorldSystemFilterFlags.Default)]
[UpdateInGroup(typeof(PresentationSystemGroup))]
[BurstCompile]
public partial struct SelectionFeedbackSystem : ISystem
{
    [BurstCompile] public void OnCreate(ref SystemState state) { }
    [BurstCompile] public void OnDestroy(ref SystemState state) { }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        // Show: SelectionFeedbackActive enabled → enable MaterialMeshInfo
        foreach (var (meshInfo, entity) in
            SystemAPI.Query<EnabledRefRW<MaterialMeshInfo>>()
                     .WithAll<SelectionFeedbackActive>()
                     .WithDisabled<MaterialMeshInfo>()
                     .WithEntityAccess())
        {
            meshInfo.ValueRW = true;
        }

        // Hide: SelectionFeedbackActive disabled → disable MaterialMeshInfo
        foreach (var (meshInfo, entity) in
            SystemAPI.Query<EnabledRefRW<MaterialMeshInfo>>()
                     .WithDisabled<SelectionFeedbackActive>()
                     .WithAll<MaterialMeshInfo>()
                     .WithEntityAccess())
        {
            meshInfo.ValueRW = false;
        }
    }
}