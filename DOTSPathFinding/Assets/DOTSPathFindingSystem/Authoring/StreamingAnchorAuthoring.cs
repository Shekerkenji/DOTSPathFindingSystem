using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Navigation.ECS
{
    /// <summary>
    /// Add to ANY entity that should anchor chunk streaming around it.
    /// Multiple anchors are supported — squads, cameras, cinematic targets, AI directors.
    /// ChunkManagerSystem unions all anchor positions and loads chunks around all of them.
    /// </summary>
    [AddComponentMenu("Navigation/Streaming Anchor")]
    public class StreamingAnchorAuthoring : MonoBehaviour
    {
        [Tooltip("Priority weight — higher priority anchors load a larger active ring. " +
                 "Use 1 for normal anchors, 2+ for critical anchors like the main player.")]
        public int priority = 1;
    }

    public class StreamingAnchorBaker : Baker<StreamingAnchorAuthoring>
    {
        public override void Bake(StreamingAnchorAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            AddComponent(entity, new StreamingAnchor
            {
                WorldPosition = authoring.transform.position,
                CurrentChunkCoord = int2.zero,
                Priority = authoring.priority
            });
        }
    }
}