using Unity.Entities;
using UnityEngine;

namespace Shek.ECSAnimation
{
    /// <summary>
    /// MonoBehaviour authoring component for a skinned mesh entity.
    ///
    /// Attach this to each GameObject that has a SkinnedMeshRenderer (e.g. body, armour, hair).
    /// The baker creates an ECS entity that holds the mesh bone data and references the
    /// animation source entity, which provides the shared bone transforms each frame.
    ///
    /// The animation source is located automatically by walking up the hierarchy until an
    /// AnimationLibraryAuthoring component is found. For a character with a single skinned mesh,
    /// both components can live on the same GameObject.
    ///
    /// Expected hierarchy example:
    ///   Character_Root   &lt;-- AnimationLibraryAuthoring goes here
    ///     Body_Mesh      &lt;-- SkinnedMeshAuthoring
    ///     Armour_Mesh    &lt;-- SkinnedMeshAuthoring
    ///     Hips           &lt;-- skeleton root
    ///       ...
    /// </summary>
    [RequireComponent(typeof(SkinnedMeshRenderer))]
    [DisallowMultipleComponent]
    public class SkinnedMeshAuthoring : MonoBehaviour
    {
        [HideInInspector]
        public SkinnedMeshRenderer skinnedMeshRenderer;

        [Header("Animation LOD")]
        [Tooltip("Fraction of base sample rate to use for this mesh. 1 = full rate, 0.25 = quarter rate (good for distant characters), 0 = frozen.")]
        [Range(0f, 1f)]
        public float animationLODMultiplier = 1f;

        [Tooltip("If true, adds AnimationCulled component so a culling system can toggle it to skip sampling and skinning when off-screen.")]
        public bool supportCulling = true;

        private void OnValidate() => skinnedMeshRenderer = GetComponent<SkinnedMeshRenderer>();
        private void Reset() => skinnedMeshRenderer = GetComponent<SkinnedMeshRenderer>();
    }

    /// <summary>
    /// Baker for <see cref="SkinnedMeshAuthoring"/>.
    /// Creates an ECS entity that represents the skinned mesh and wires it to its animation source.
    /// </summary>
    public class SkinnedMeshBaker : Baker<SkinnedMeshAuthoring>
    {
        public override void Bake(SkinnedMeshAuthoring authoring)
        {
            var smr = authoring.skinnedMeshRenderer != null
                ? authoring.skinnedMeshRenderer
                : authoring.GetComponent<SkinnedMeshRenderer>();

            if (smr == null)
            {
                Debug.LogError(
                    $"[SkinnedMeshBaker] No SkinnedMeshRenderer on '{authoring.gameObject.name}'!");
                return;
            }

            var entity = GetEntity(TransformUsageFlags.Dynamic);

            // Bake the mesh bone names and bind poses into a blob asset.
            var blobRef = AnimationBaker.BakeSkinnedMeshBones(smr);
            if (blobRef.IsCreated)
            {
                AddBlobAsset(ref blobRef, out _);
                AddComponent(entity, new SkinnedMeshBones { Value = blobRef });
            }

            // Mark this entity as requiring GPU skinning matrix computation.
            AddComponent<RequiresSkinning>(entity);
            AddComponent(entity, Unity.Transforms.LocalTransform.Identity);

            // Add the bone index cache in an unresolved state.
            // BoneIndexCachingSystem resolves it on the first frame by name-matching.
            // Using an IsResolved flag avoids adding/removing a tag component at runtime.
            AddComponent(entity, new BoneIndexCache
            {
                Value = default,
                IsResolved = false
            });

            // Note: SkinMatrix buffer is NOT added here.
            // Unity's SkinnedMeshRendererBaker already adds and sizes it based on smr.bones.Length.
            // Adding it again would cause a duplicate component baking error.

            // Find the animation source by searching up the hierarchy for AnimationLibraryAuthoring.
            var animSource = FindAnimationSourceInHierarchy(authoring.transform);
            if (animSource == null)
            {
                Debug.LogError(
                    $"[SkinnedMeshBaker] No AnimationLibraryAuthoring found in the hierarchy above " +
                    $"'{authoring.gameObject.name}'. Add AnimationLibraryAuthoring to this GameObject or a parent.");
                return;
            }

            // Store a reference to the animation entity so the skinning system can
            // read its BoneTransformBuffer every frame.
            var animEntity = GetEntity(animSource, TransformUsageFlags.Dynamic);
            AddComponent(entity, new AnimationSource { Value = animEntity });

            // Enableable tag — allows skinning to be paused for this mesh independently
            // of the animation source entity, with zero structural cost.
            AddComponent<AnimationActive>(entity);

            // Animation LOD — reduces sample rate for distant characters.
            // SampleRateMultiplier = 1 means full rate (no LOD applied).
            if (authoring.animationLODMultiplier < 1f)
            {
                AddComponent(entity, new AnimationLOD
                {
                    SampleRateMultiplier = authoring.animationLODMultiplier,
                    AccumulatedDelta = 0f
                });
            }

            // Culling support — a culling system can toggle AnimationCulled to skip
            // both sampling and skinning for off-screen characters at zero structural cost.
            if (authoring.supportCulling)
            {
                AddComponent<AnimationCulled>(entity);
                // Start un-culled.
                SetComponentEnabled<AnimationCulled>(entity, false);
            }
        }

        /// <summary>
        /// Walks up the transform hierarchy starting at <paramref name="t"/> and returns
        /// the first <see cref="AnimationLibraryAuthoring"/> found, or null if none exists.
        /// </summary>
        static AnimationLibraryAuthoring FindAnimationSourceInHierarchy(Transform t)
        {
            var current = t;
            while (current != null)
            {
                var found = current.GetComponent<AnimationLibraryAuthoring>();
                if (found != null) return found;
                current = current.parent;
            }
            return null;
        }
    }
}