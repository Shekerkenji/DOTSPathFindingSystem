using UnityEngine;
using Unity.Entities;
using Unity.Rendering;
using UnityEditor;

// ─────────────────────────────────────────────────────────────────────────────
//  SelectableAuthoring.cs
//
//  Attach to any GameObject that should be selectable by the player.
//  Bakes:
//    • SelectableTag    — kind (Unit / Group BannerHolder / BigGroup BannerHolder)
//    • Selected         — enableable, starts disabled
//    • SelectionFeedbackActive — enableable on the FeedbackEntity, starts disabled
//
//  FeedbackEntity workflow
//  ───────────────────────
//  1. Create a child GameObject with your highlight mesh / decal / circle.
//  2. Give it a SelectionFeedbackAuthoring component.
//  3. Assign it to the FeedbackObject slot below.
//  The baked entity will have SelectionFeedbackActive as an enableable component.
//  Drive your material/visibility from a system that queries
//  .WithAll<SelectionFeedbackActive>() vs .WithDisabled<SelectionFeedbackActive>().
// ─────────────────────────────────────────────────────────────────────────────

public class SelectableAuthoring : MonoBehaviour
{
    [Tooltip("What kind of selectable is this?\n" +
             "Unit            — a single unit.\n" +
             "Group           — a Group's BannerHolder.\n" +
             "BigGroup        — a BigGroup's BannerHolder.")]
    public SelectableKind Kind = SelectableKind.Unit;

    [Tooltip("The child GameObject that carries the selection highlight mesh / decal.\n" +
             "Leave null if you handle feedback differently.")]
    public GameObject FeedbackObject;

    public class Baker : Baker<SelectableAuthoring>
    {
        public override void Bake(SelectableAuthoring src)
        {
            var e = GetEntity(TransformUsageFlags.None);

            // Resolve feedback entity (may be null)
            Entity feedbackEntity = src.FeedbackObject != null
                ? GetEntity(src.FeedbackObject, TransformUsageFlags.None)
                : Entity.Null;

            // Bake SelectableTag
            AddComponent(e, new SelectableTag { Kind = src.Kind });

            // Bake Selected as disabled (not selected at start)
            AddComponent(e, new Selected { FeedbackEntity = feedbackEntity });
            SetComponentEnabled<Selected>(e, false);
            SetComponentEnabled<MaterialMeshInfo>(feedbackEntity, false);
        }
    }
}

