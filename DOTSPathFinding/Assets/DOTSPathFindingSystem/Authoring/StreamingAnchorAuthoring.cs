using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using Unity.Burst;
using Unity.Transforms;

namespace Shek.ECSGrid
{
    /// <summary>
    /// Add to any GameObject that should drive chunk streaming.
    ///
    /// Examples:
    ///   • Player character  ? Priority 1 (default)
    ///   • RTS camera        ? Priority 1
    ///   • Attacking squad   ? Priority 1
    ///   • Long-range scout  ? Priority 2 (loads double the active radius)
    ///
    /// Multiple anchors combine additively — the grid keeps the UNION of all
    /// desired chunk states. An area desired Active by any anchor stays Active.
    ///
    /// Usage: Drop this component on a GameObject. No code changes needed.
    /// GridManagerSystem.StreamingAnchorSystem updates CurrentChunkCoord each frame
    /// from the WorldPosition field, which is synced from LocalTransform by
    /// StreamingAnchorSyncSystem (below).
    /// </summary>
    public class StreamingAnchorAuthoring : MonoBehaviour
    {
        [Tooltip("Priority 1 = normal active radius. Priority 2 = double, etc.")]
        [Min(1)]
        public int priority = 1;
        public class StreamingAnchorBaker : Baker<StreamingAnchorAuthoring>
        {
            public override void Bake(StreamingAnchorAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent(entity, new StreamingAnchor
                {
                    WorldPosition = authoring.transform.position,
                    CurrentChunkCoord = int2.zero,
                    Priority = authoring.priority,
                });
            }
        }
    }

    

    // =========================================================================
    // STREAMING ANCHOR POSITION SYNC
    // Copies LocalTransform.Position ? StreamingAnchor.WorldPosition each frame
    // so the coordinate is always current even if the entity moves.
    // Runs in InitializationSystemGroup BEFORE StreamingAnchorSystem.
    // =========================================================================

    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [UpdateBefore(typeof(StreamingAnchorSystem))]
    [BurstCompile]
    public partial struct StreamingAnchorSyncSystem : ISystem
    {
        public void OnCreate(ref SystemState state) =>
            state.RequireForUpdate<GridConfig>();

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            new SyncJob().ScheduleParallel(state.Dependency).Complete();
        }

        [BurstCompile]
        partial struct SyncJob : IJobEntity
        {
            void Execute(ref StreamingAnchor anchor,
                         in LocalTransform transform)
            {
                anchor.WorldPosition = transform.Position;
            }
        }
    }
}
