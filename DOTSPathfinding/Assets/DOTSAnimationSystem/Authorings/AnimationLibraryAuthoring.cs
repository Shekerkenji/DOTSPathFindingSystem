using System.Collections.Generic;
using Unity.Entities;
using UnityEngine;

namespace Shek.ECSAnimation
{
    /// <summary>
    /// Authoring component for a character that uses a multi-clip animation library.
    ///
    /// Attach this to your character root (same place AnimationClipAuthoring used to go).
    /// Assign as many AnimationClips as needed � they are baked into a single blob asset
    /// and addressed at runtime by <see cref="ushort"/> index via <see cref="AnimationController"/>.
    ///
    /// Typical hierarchy:
    ///   Character_Root   ? AnimationLibraryAuthoring here
    ///     Body_Mesh      ? SkinnedMeshAuthoring (unchanged)
    ///     Hips           ? skeleton root
    ///       ...
    ///
    /// The existing SkinnedMeshAuthoring / SkinnedMeshBaker components are unchanged
    /// and work with the new system transparently � they still look up AnimationSource
    /// to find the entity with BoneTransformBuffer.
    /// </summary>
    [DisallowMultipleComponent]
    public class AnimationLibraryAuthoring : MonoBehaviour
    {
        [Header("Animation Library")]
        [Tooltip("All clips for this character. Index 0 is the default. Order matters � it becomes the runtime ushort ClipIndex.")]
        public List<AnimationClip> clips = new();

        [Header("Roots (auto-detected if left null)")]
        [Tooltip("The GameObject passed to SampleAnimation. Defaults to this GameObject.")]
        public GameObject animationRoot;

        [Tooltip("Root of the skeleton (e.g. Hips). Auto-detected as first non-SMR child if null.")]
        public GameObject boneRoot;

        [Header("Baking Settings")]
        [Range(30f, 120f)]
        [Tooltip("Frames sampled per second. Higher = smoother animation, more memory.")]
        public float sampleRate = 60f;

        [Header("Animation LOD")]
        [Range(0f, 1f)]
        [Tooltip("Fraction of base sample rate. 1 = full (60fps), 0.25 = quarter (15fps). Set per-character based on expected screen distance.")]
        public float animationLODMultiplier = 1f;

        [Tooltip("Adds AnimationCulled component so a culling system can skip this character when off-screen.")]
        public bool supportCulling = true;

        [Header("Default Playback")]
        [Tooltip("Index of the clip to play on start. Must be a valid index into the clips list.")]
        public ushort defaultClipIndex = 0;

        [Tooltip("If true, starts playing the default clip immediately on scene load.")]
        public bool playOnStart = true;

        [Range(0.1f, 5f)]
        public float playbackSpeed = 1f;

        [Header("Blending (Optional)")]
        [Tooltip("If true, adds AnimationBlendLayer buffer for manual multi-layer blending.")]
        public bool enableBlending = false;

        private void OnValidate() => AutoDetect();
        private void Reset() => AutoDetect();

        private void AutoDetect()
        {
            if (animationRoot == null) animationRoot = gameObject;

            if (boneRoot == null)
            {
                for (int i = 0; i < transform.childCount; i++)
                {
                    var child = transform.GetChild(i);
                    if (child.GetComponent<SkinnedMeshRenderer>() == null)
                    {
                        boneRoot = child.gameObject;
                        break;
                    }
                }
            }

            // Clamp default index to valid range
            if (clips != null && clips.Count > 0)
                defaultClipIndex = (ushort)Mathf.Clamp(defaultClipIndex, 0, clips.Count - 1);
        }
    }

    /// <summary>
    /// Baker for <see cref="AnimationLibraryAuthoring"/>.
    /// Creates an entity that owns the animation library blob, an AnimationController,
    /// and a pre-sized BoneTransformBuffer.
    /// </summary>
    public class AnimationLibraryBakerComponent : Baker<AnimationLibraryAuthoring>
    {
        public override void Bake(AnimationLibraryAuthoring authoring)
        {
            if (authoring.clips == null || authoring.clips.Count == 0)
            {
                Debug.LogWarning(
                    $"[AnimationLibraryBaker] No clips assigned on '{authoring.gameObject.name}'.",
                    authoring);
                return;
            }

            var animRoot = authoring.animationRoot != null
                ? authoring.animationRoot
                : authoring.gameObject;

            var boneRootGO = authoring.boneRoot;
            if (boneRootGO == null)
            {
                var t = animRoot.transform;
                for (int i = 0; i < t.childCount; i++)
                {
                    var child = t.GetChild(i);
                    if (child.GetComponent<SkinnedMeshRenderer>() == null)
                    {
                        boneRootGO = child.gameObject;
                        break;
                    }
                }
            }

            if (boneRootGO == null)
            {
                Debug.LogError(
                    $"[AnimationLibraryBaker] Could not find boneRoot on '{authoring.gameObject.name}'. " +
                    "Assign it manually or add a non-SMR child (e.g. Hips).");
                return;
            }

            // Bake all clips into one flat blob.
            var blobRef = AnimationLibraryBaker.BakeLibrary(
                authoring.clips, animRoot, boneRootGO, authoring.sampleRate);

            if (!blobRef.IsCreated) return;

            var entity = GetEntity(TransformUsageFlags.Dynamic);
            AddBlobAsset(ref blobRef, out _);
            AddComponent(entity, new AnimationLibraryReference { Value = blobRef });

            // Initial controller state.
            ushort startClip = (ushort)Mathf.Clamp(
                authoring.defaultClipIndex, 0, authoring.clips.Count - 1);

            AddComponent(entity, new AnimationController
            {
                ClipIndex = startClip,
                Time = 0f,
                Speed = authoring.playbackSpeed,
                IsPlaying = authoring.playOnStart,
                HasLooped = false,
                HasFinished = false,
                NextClipIndex = AnimationController.NoTransition,
                NextClipTime = 0f,
                NextClipSpeed = 1f,
                TransitionDuration = 0f,
                TransitionTime = 0f
            });

            // Enableable tag for zero-cost pause.
            AddComponent<AnimationActive>(entity);

            // Animation LOD — reduces sample rate for this character based on distance.
            if (authoring.animationLODMultiplier < 1f)
            {
                AddComponent(entity, new AnimationLOD
                {
                    SampleRateMultiplier = authoring.animationLODMultiplier,
                    AccumulatedDelta = 0f
                });
            }

            // Culling support — toggle AnimationCulled to skip sampling when off-screen.
            if (authoring.supportCulling)
            {
                AddComponent<AnimationCulled>(entity);
                SetComponentEnabled<AnimationCulled>(entity, false);
            }

            // Pre-size the bone buffer � avoids a runtime resize on frame 1.
            int boneCount = blobRef.Value.BoneCount;
            var boneBuffer = AddBuffer<BoneTransformBuffer>(entity);
            boneBuffer.ResizeUninitialized(boneCount);

            if (authoring.enableBlending)
            {
                AddComponent<UseAnimationBlending>(entity);
                AddBuffer<AnimationBlendLayer>(entity);
            }
        }
    }
}