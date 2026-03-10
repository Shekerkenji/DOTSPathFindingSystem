
// ─────────────────────────────────────────────────────────────────────────────
//  SelectionFeedbackAuthoring.cs
//
//  Attach to the child highlight / decal GameObject.
//  Bakes SelectionFeedbackActive as disabled.
// ─────────────────────────────────────────────────────────────────────────────
using Unity.Entities;
using Unity.Rendering;
using UnityEngine;

public class SelectionFeedbackAuthoring : MonoBehaviour
{
    public class Baker : Baker<SelectionFeedbackAuthoring>
    {
        public override void Bake(SelectionFeedbackAuthoring src)
        {
            var e = GetEntity(TransformUsageFlags.None);
            AddComponent<SelectionFeedbackActive>(e);
            SetComponentEnabled<SelectionFeedbackActive>(e, false);
            SetComponentEnabled<MaterialMeshInfo>(e, false);
        }
    }
}