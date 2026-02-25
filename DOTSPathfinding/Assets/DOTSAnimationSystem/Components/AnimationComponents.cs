using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;

namespace DOTSAnimation
{
    // ─────────────────────────────────────────────────────────────────────────
    // Core bone / transform types
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Core animation clip data stored as a blob asset.
    /// Bone frames are stored in animationRoot-local (object) space.
    /// </summary>
    public struct AnimationClipBlob
    {
        public BlobArray<BoneTransform> Frames;
        public BlobArray<float> FrameTimes;
        /// <summary>
        /// Bone names as FixedString64Bytes — Burst-safe, no managed ToString() needed.
        /// </summary>
        public BlobArray<FixedString64Bytes> BoneNames;
        public BlobArray<int> ParentIndices;
        public int BoneCount;
        public float Duration;
        public float FrameRate;
        public bool IsLooping;
        /// <summary>Hash of the clip name — (uint)new FixedString64Bytes(clipName).GetHashCode()</summary>
        public uint NameHash;
    }

    public struct BoneTransform
    {
        public float3 Position;
        public quaternion Rotation;
        public float3 Scale;

        /// <summary>
        /// Full slerp lerp — used for intra-clip frame interpolation where quality matters most.
        /// </summary>
        public static BoneTransform Lerp(BoneTransform a, BoneTransform b, float t) =>
            new BoneTransform
            {
                Position = math.lerp(a.Position, b.Position, t),
                Rotation = math.slerp(a.Rotation, b.Rotation, t),
                Scale = math.lerp(a.Scale, b.Scale, t)
            };

        /// <summary>
        /// Faster nlerp variant — used during crossfades where two poses are already being
        /// blended and the result will be normalized again. Avoids the trig cost of slerp.
        /// </summary>
        public static BoneTransform NlerpFast(BoneTransform a, BoneTransform b, float t) =>
            new BoneTransform
            {
                Position = math.lerp(a.Position, b.Position, t),
                Rotation = math.normalizesafe(math.lerp(a.Rotation.value, b.Rotation.value, t)),
                Scale = math.lerp(a.Scale, b.Scale, t)
            };

        /// <summary>Converts this bone transform to a 4x4 TRS matrix.</summary>
        public float4x4 ToMatrix() => float4x4.TRS(Position, Rotation, Scale);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Animation state — single-clip playback
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Tracks playback state for a single animation clip.
    /// Written by AnimationSamplingSystem every frame.
    /// </summary>
    public struct AnimationState : IComponentData
    {
        public float Time;
        public float Speed;
        public bool IsPlaying;
        public bool HasLooped;
    }

    /// <summary>
    /// Reference to the baked animation clip blob.
    /// Paired with AnimationState on the animation source entity.
    /// </summary>
    public struct AnimationClipReference : IComponentData
    {
        public BlobAssetReference<AnimationClipBlob> Value;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Mesh / skinning components
    // ─────────────────────────────────────────────────────────────────────────

    public struct SkinnedMeshBones : IComponentData
    {
        public BlobAssetReference<SkinnedMeshBonesBlob> Value;
    }

    public struct SkinnedMeshBonesBlob
    {
        public BlobArray<int> BoneIndices;
        public BlobArray<float4x4> BindPoses;
        /// <summary>
        /// Bone names stored as FixedString64Bytes (not BlobString) so BoneIndexCachingSystem
        /// can be Burst-compiled and scheduled as a parallel job — BlobString.ToString() is managed.
        /// </summary>
        public BlobArray<FixedString64Bytes> BoneNames;
        public int RootBoneIndex;
    }

    /// <summary>
    /// Per-bone output buffer written by AnimationSamplingSystem,
    /// read by SkinningMatrixSystem. Pre-sized at bake time.
    ///
    /// Stores the bone transform pre-built as a float4x4 so SkinningMatrixSystem
    /// only needs a single math.mul per bone instead of TRS + mul.
    /// The quaternion-to-matrix conversion happens once here (in the sampling job)
    /// rather than once per mesh that shares this animation source.
    /// </summary>
    [InternalBufferCapacity(64)]
    public struct BoneTransformBuffer : IBufferElementData
    {
        /// <summary>
        /// Bone transform in object (animation) space, already converted to a 4x4 matrix.
        /// Written by AnimationSamplingSystem; consumed by SkinningMatrixSystem.
        /// </summary>
        public float4x4 Matrix;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Bone index cache — resolved once at startup
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Blob storing pre-resolved mesh-bone → animation-bone index mappings.
    /// Built once by BoneIndexCachingSystem; consumed every frame by SkinningMatrixSystem.
    /// </summary>
    public struct BoneIndexCacheBlob
    {
        /// <summary>MeshToAnimBoneIndex[meshBoneIdx] = animBoneIdx, or -1 if not found.</summary>
        public BlobArray<int> MeshToAnimBoneIndex;
    }

    /// <summary>
    /// Component holding the resolved bone-index cache blob asset.
    /// Baked with IsResolved = false; BoneIndexCachingSystem writes the blob in-place.
    /// No add/remove structural changes are ever needed.
    /// </summary>
    public struct BoneIndexCache : IComponentData
    {
        public BlobAssetReference<BoneIndexCacheBlob> Value;

        /// <summary>False until BoneIndexCachingSystem has written the resolved blob.</summary>
        public bool IsResolved;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Blending
    // ─────────────────────────────────────────────────────────────────────────

    public struct AnimationBlendLayer : IBufferElementData
    {
        public BlobAssetReference<AnimationClipBlob> Clip;
        public float Weight;
        public float Time;
        public float Speed;
        public bool IsAdditive;
    }

    public struct UseAnimationBlending : IComponentData { }

    // ─────────────────────────────────────────────────────────────────────────
    // Skinning routing + enable / disable
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Marks an entity as requiring GPU skinning matrix computation each frame.</summary>
    public struct RequiresSkinning : IComponentData { }

    /// <summary>
    /// Points a skinned-mesh entity at the entity that owns the animation state
    /// and BoneTransformBuffer. Allows multiple SMR meshes to share one pose.
    /// </summary>
    public struct AnimationSource : IComponentData
    {
        public Entity Value;
    }

    /// <summary>
    /// Enableable tag. Toggle with em.SetComponentEnabled&lt;AnimationActive&gt;(entity, false)
    /// to pause sampling + skinning with zero structural cost.
    /// </summary>
    public struct AnimationActive : IComponentData, IEnableableComponent { }

    // ─────────────────────────────────────────────────────────────────────────
    // Animation LOD — reduces sampling rate for distant characters
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Controls animation update rate for LOD. Set SampleRateMultiplier to a value
    /// between 0..1 to reduce how often the animation is sampled (e.g. 0.25 = 15fps
    /// updates for a 60fps base rate). AnimationSamplingSystem accumulates delta time
    /// and only samples when the threshold is reached, so the pose stays frozen between
    /// samples — imperceptible at distance.
    ///
    /// Set SampleRateMultiplier = 1 for full rate (no LOD).
    /// SampleRateMultiplier = 0 freezes the animation entirely.
    /// </summary>
    public struct AnimationLOD : IComponentData
    {
        /// <summary>Fraction of the clip's FrameRate at which to sample. Range [0, 1].</summary>
        public float SampleRateMultiplier;

        /// <summary>Accumulated delta time since last sample. Written by AnimationSamplingSystem.</summary>
        public float AccumulatedDelta;
    }

    /// <summary>
    /// Enableable tag set by a culling system when the character is off-screen.
    /// AnimationSamplingSystem skips entities with this enabled.
    /// Toggling this costs zero structural changes.
    /// </summary>
    public struct AnimationCulled : IComponentData, IEnableableComponent { }

    /// <summary>
    /// Optional marker: this animation source entity has exactly one SMR mesh entity
    /// referencing it. AnimationSamplingSystem uses a merged sample+skin path for these,
    /// skipping the intermediate BoneTransformBuffer write-read round trip.
    ///
    /// Added by SkinnedMeshBaker when it detects a 1:1 source-to-mesh relationship.
    /// If a second mesh entity is added at runtime, remove this component from the source.
    /// </summary>
    public struct SingleMeshSource : IComponentData
    {
        /// <summary>The one SMR entity that reads from this source.</summary>
        public Entity MeshEntity;
    }

    // ?????????????????????????????????????????????????????????????????????????
    // Blob: flat array of all clips for a character, addressed by ushort index.
    //
    // Layout in memory:
    //   AnimationLibraryBlob
    //     ?? Clips[clipCount]          — per-clip metadata (offsets, lengths, etc.)
    //     ?? Frames[totalFrames]       — bone transforms, ALL clips concatenated
    //     ?? FrameTimes[totalTimes]    — frame timestamps, ALL clips concatenated
    //     ?? BoneNames[boneCount]      — shared across all clips (same skeleton)
    //     ?? NameHashes[clipCount]     — uint hash of each clip name for runtime lookup
    //
    // Clips share the skeleton — BoneCount and BoneNames are the same for every clip.
    // ?????????????????????????????????????????????????????????????????????????

    /// <summary>
    /// Per-clip metadata stored inside the library blob.
    /// Frames and FrameTimes are addressed as slice [FrameOffset .. FrameOffset+FrameCount].
    /// </summary>
    public struct AnimationClipInfo
    {
        /// <summary>Start index into AnimationLibraryBlob.Frames (flat: frameIdx * BoneCount + boneIdx).</summary>
        public int FrameOffset;

        /// <summary>Number of frames sampled for this clip.</summary>
        public int FrameCount;

        /// <summary>Start index into AnimationLibraryBlob.FrameTimes.</summary>
        public int TimeOffset;

        public float Duration;
        public float FrameRate;
        public bool IsLooping;

        /// <summary>uint hash of the clip name — use AnimationLibraryBlob.FindClipIndex() for lookup.</summary>
        public uint NameHash;
    }

    /// <summary>
    /// Blob asset that holds ALL animation clips for a character in a single allocation.
    /// Shared by all entities that use the same character rig.
    ///
    /// Frame data is stored in both AOS and SOA layout:
    ///   • Frames[]        — AOS BoneTransform — used by the crossfade blending path
    ///   • FramesPos/Rot/Scl — SOA float3/quaternion/float3 — used by the fast single-clip
    ///     path; contiguous per-component arrays let Burst auto-vectorize the lerp loop.
    ///
    /// BoneNames uses FixedString64Bytes (not BlobString) so BoneIndexCachingSystem
    /// can be Burst-compiled and run as a parallel job.
    /// </summary>
    public struct AnimationLibraryBlob
    {
        /// <summary>Per-clip metadata. Index with ushort ClipIndex.</summary>
        public BlobArray<AnimationClipInfo> Clips;

        // ── AOS layout — used by blending path ───────────────────────────────
        /// <summary>All bone frames from all clips, concatenated. Access via clip.FrameOffset.</summary>
        public BlobArray<BoneTransform> Frames;

        // ── SOA layout — used by fast single-clip path ────────────────────────
        /// <summary>SOA positions — same data as Frames[i].Position, contiguous per component.</summary>
        public BlobArray<float3> FramesPos;
        /// <summary>SOA rotations — same data as Frames[i].Rotation, contiguous per component.</summary>
        public BlobArray<quaternion> FramesRot;
        /// <summary>SOA scales — same data as Frames[i].Scale, contiguous per component.</summary>
        public BlobArray<float3> FramesScl;

        /// <summary>All frame timestamps from all clips, concatenated. Access via clip.TimeOffset.</summary>
        public BlobArray<float> FrameTimes;

        /// <summary>
        /// Bone names as FixedString64Bytes — Burst-safe, no managed ToString() needed.
        /// </summary>
        public BlobArray<FixedString64Bytes> BoneNames;

        /// <summary>Bone parent indices — shared across all clips.</summary>
        public BlobArray<int> ParentIndices;

        /// <summary>Number of bones. Clips[i].FrameOffset is in units of BoneCount bones per frame.</summary>
        public int BoneCount;

        /// <summary>
        /// Linear search for a clip by name hash. Returns -1 if not found.
        /// Call once and cache the result — do not call per-frame.
        /// </summary>
        public int FindClipIndex(uint nameHash)
        {
            for (int i = 0; i < Clips.Length; i++)
                if (Clips[i].NameHash == nameHash) return i;
            return -1;
        }

        /// <summary>Helper: hash a clip name the same way the baker does.</summary>
        public static uint HashName(in FixedString64Bytes name) => (uint)name.GetHashCode();
    }

    /// <summary>
    /// IComponentData holding the baked animation library blob.
    /// Replaces AnimationClipReference on the animation source entity.
    /// </summary>
    public struct AnimationLibraryReference : IComponentData
    {
        public BlobAssetReference<AnimationLibraryBlob> Value;
    }

    // ?????????????????????????????????????????????????????????????????????????
    // AnimationController — replaces AnimationState.
    //
    // Supports:
    //   • Instant clip switch  (TransitionDuration == 0)
    //   • Built-in crossfade   (TransitionDuration  > 0)
    //   • Looping / non-looping per clip (read from blob at runtime)
    //   • Per-clip speed override
    //
    // All fields are value types — Burst-safe, no heap allocation.
    // ?????????????????????????????????????????????????????????????????????????

    /// <summary>
    /// Animation playback controller for multi-clip libraries.
    /// Written by AnimationLibrarySamplingSystem every frame.
    /// Set ClipIndex to switch clips; set NextClipIndex + TransitionDuration for crossfade.
    /// </summary>
    public struct AnimationController : IComponentData
    {
        // ?? Current clip ??????????????????????????????????????????????????????
        /// <summary>Index into AnimationLibraryBlob.Clips for the active clip.</summary>
        public ushort ClipIndex;

        /// <summary>Playback time of the current clip, in seconds.</summary>
        public float Time;

        /// <summary>Playback speed multiplier (negative = reverse).</summary>
        public float Speed;

        /// <summary>Whether the current clip is advancing each frame.</summary>
        public bool IsPlaying;

        /// <summary>Set to true by the system when a looping clip wraps around.</summary>
        public bool HasLooped;

        /// <summary>Set to true by the system when a non-looping clip reaches its end.</summary>
        public bool HasFinished;

        // ?? Transition (crossfade) ????????????????????????????????????????????
        /// <summary>Target clip index for an in-progress crossfade. 0xFFFF = no transition.</summary>
        public ushort NextClipIndex;

        /// <summary>Time within NextClip's playback (starts at 0 when crossfade begins).</summary>
        public float NextClipTime;

        /// <summary>Speed for NextClip during and after the transition.</summary>
        public float NextClipSpeed;

        /// <summary>Total crossfade duration in seconds. 0 = instant switch.</summary>
        public float TransitionDuration;

        /// <summary>Elapsed time of the current crossfade.</summary>
        public float TransitionTime;

        // ?? Sentinel ?????????????????????????????????????????????????????????
        public const ushort NoTransition = 0xFFFF;

        /// <summary>True while a crossfade transition is in progress.</summary>
        public bool IsTransitioning => NextClipIndex != NoTransition;
    }

    // ?????????????????????????????????????????????????????????????????????????
    // Utility: AnimationControllerAPI
    // Static helpers to drive AnimationController from systems or MonoBehaviours.
    // All methods are Burst-compatible (no managed types).
    // ?????????????????????????????????????????????????????????????????????????

    public static class AnimationControllerAPI
    {
        // ?? Immediate switch ??????????????????????????????????????????????????

        /// <summary>
        /// Instantly switches to the clip at <paramref name="clipIndex"/>.
        /// Resets time to 0 and cancels any in-progress transition.
        /// </summary>
        public static void Play(ref AnimationController ctrl, ushort clipIndex, float speed = 1f)
        {
            ctrl.ClipIndex = clipIndex;
            ctrl.Time = 0f;
            ctrl.Speed = speed;
            ctrl.IsPlaying = true;
            ctrl.HasLooped = false;
            ctrl.HasFinished = false;
            CancelTransition(ref ctrl);
        }

        /// <summary>
        /// Switches to the clip at <paramref name="clipIndex"/> from time <paramref name="fromTime"/>.
        /// Useful for syncing entry points (e.g. start run-cycle from mid-stride).
        /// </summary>
        public static void PlayFrom(ref AnimationController ctrl, ushort clipIndex, float fromTime, float speed = 1f)
        {
            Play(ref ctrl, clipIndex, speed);
            ctrl.Time = fromTime;
        }

        /// <summary>Pauses playback without resetting time.</summary>
        public static void Pause(ref AnimationController ctrl) => ctrl.IsPlaying = false;

        /// <summary>Resumes playback from current time.</summary>
        public static void Resume(ref AnimationController ctrl) => ctrl.IsPlaying = true;

        /// <summary>Stops and resets to time 0.</summary>
        public static void Stop(ref AnimationController ctrl)
        {
            ctrl.IsPlaying = false;
            ctrl.Time = 0f;
            ctrl.HasFinished = false;
            CancelTransition(ref ctrl);
        }

        /// <summary>Seeks the current clip to a specific time without affecting play state.</summary>
        public static void SetTime(ref AnimationController ctrl, float time) => ctrl.Time = time;

        /// <summary>Changes playback speed of the current clip.</summary>
        public static void SetSpeed(ref AnimationController ctrl, float speed) => ctrl.Speed = speed;

        // ?? Crossfade ?????????????????????????????????????????????????????????

        /// <summary>
        /// Starts a smooth crossfade from the current clip to <paramref name="targetClip"/>
        /// over <paramref name="duration"/> seconds.
        ///
        /// If <paramref name="duration"/> is 0, this is equivalent to calling Play().
        /// </summary>
        public static void CrossfadeTo(
            ref AnimationController ctrl,
            ushort targetClip,
            float duration,
            float speed = 1f)
        {
            if (duration <= 0f)
            {
                Play(ref ctrl, targetClip, speed);
                return;
            }

            // If already transitioning to the same clip, do nothing.
            if (ctrl.IsTransitioning && ctrl.NextClipIndex == targetClip) return;

            ctrl.NextClipIndex = targetClip;
            ctrl.NextClipTime = 0f;
            ctrl.NextClipSpeed = speed;
            ctrl.TransitionDuration = duration;
            ctrl.TransitionTime = 0f;
        }

        /// <summary>Immediately cancels any in-progress crossfade, staying on the current clip.</summary>
        public static void CancelTransition(ref AnimationController ctrl)
        {
            ctrl.NextClipIndex = AnimationController.NoTransition;
            ctrl.NextClipTime = 0f;
            ctrl.NextClipSpeed = 1f;
            ctrl.TransitionDuration = 0f;
            ctrl.TransitionTime = 0f;
        }

        // ?? Queries ???????????????????????????????????????????????????????????

        /// <summary>
        /// Returns the playback position of the current clip as a 0..1 normalised value.
        /// Requires the library blob to look up the clip's duration.
        /// </summary>
        public static float GetNormalizedTime(
            in AnimationController ctrl,
            ref AnimationLibraryBlob lib)
        {
            if (ctrl.ClipIndex >= lib.Clips.Length) return 0f;
            float dur = lib.Clips[ctrl.ClipIndex].Duration;
            return dur > 0f ? ctrl.Time / dur : 0f;
        }

        /// <summary>
        /// Returns true once a non-looping clip has played to the end.
        /// Remains true until Play/CrossfadeTo is called.
        /// </summary>
        public static bool IsFinished(in AnimationController ctrl) => ctrl.HasFinished;

        /// <summary>Crossfade blend weight of the incoming (next) clip — 0 at start, 1 at end.</summary>
        public static float TransitionAlpha(in AnimationController ctrl)
        {
            if (!ctrl.IsTransitioning || ctrl.TransitionDuration <= 0f) return 0f;
            return math.saturate(ctrl.TransitionTime / ctrl.TransitionDuration);
        }
    }
}