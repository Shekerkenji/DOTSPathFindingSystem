using UnityEngine;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;

// ── ECSNavAgentAuthoring ──────────────────────────────────────────────────────

public class ECSNavAgentAuthoring : MonoBehaviour
{
    public float MoveSpeed = 5f;
    public float StoppingDistance = 0.2f;

    [Tooltip("Assign the GroupAuthoring GameObject if part of a group. Leave null for solo.")]
    public GameObject GroupObject;

    public class Baker : Baker<ECSNavAgentAuthoring>
    {
        public override void Bake(ECSNavAgentAuthoring src)
        {
            var e = GetEntity(TransformUsageFlags.Dynamic);

            AddComponent(e, new NavAgent
            {
                MoveSpeed = src.MoveSpeed,
                StoppingDistance = src.StoppingDistance,
                Status = NavAgentStatus.Idle,
                GroupEntity = src.GroupObject != null
                                   ? GetEntity(src.GroupObject, TransformUsageFlags.None)
                                   : Entity.Null,
            });

            // Bake all enableable components so they always exist on the archetype.
            // Systems toggle enabled/disabled instead of adding/removing, avoiding
            // structural changes entirely.
            AddComponent<PathRequest>(e); SetComponentEnabled<PathRequest>(e, false);
            AddComponent<PathReady>(e); SetComponentEnabled<PathReady>(e, false);
            AddComponent<PathFailed>(e); SetComponentEnabled<PathFailed>(e, false);

            AddBuffer<PathWaypoint>(e);
        }
    }
}