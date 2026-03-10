using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Attach to a GameObject that has a MeshRenderer.
/// </summary>
public class AnimatedMeshAuthoring : MonoBehaviour
{
    [Tooltip("The baked animation data asset.")]
    public AnimatedMeshScriptableObjectECS AnimationData;

    [Tooltip("Index of the clip to play on start.")]
    public int StartClipIndex = 0;

    [Tooltip("Whether to start playing immediately.")]
    public bool PlayOnStart = true;
}

public class AnimatedMeshBaker : Baker<AnimatedMeshAuthoring>
{
    public override void Bake(AnimatedMeshAuthoring authoring)
    {
        if (authoring.AnimationData == null) return;

        var so = authoring.AnimationData;
        var renderer = authoring.GetComponent<MeshRenderer>();
        if (renderer == null)
        {
            Debug.LogError("[AnimatedMesh] Requires a MeshRenderer on the same GameObject.");
            return;
        }

        Entity e = GetEntity(TransformUsageFlags.Renderable);

        // ── Clip offset buffer ────────────────────────────────────────────────
        var offsetBuffer = AddBuffer<AnimatedMeshClipOffset>(e);
        int cursor = 0;
        foreach (var clip in so.Clips)
        {
            int count = clip.Frames?.Count ?? 0;
            offsetBuffer.Add(new AnimatedMeshClipOffset { FrameStart = cursor, FrameCount = count });
            cursor += count;
        }

        // ── SO + render setup ─────────────────────────────────────────────────
        AddComponentObject(e, new AnimatedMeshData { SO = so });

        AddComponentObject(e, new AnimatedMeshRenderSetupData
        {
            Materials = renderer.sharedMaterials,
            ShadowMode = renderer.shadowCastingMode,
            ReceiveShadows = renderer.receiveShadows,
            StartClipIndex = authoring.StartClipIndex,
            PlayOnStart = authoring.PlayOnStart,
            Loop = true,
        });

        AddComponent(e, new AnimatedMeshNeedsRenderSetup());

        // ── Render bounds ─────────────────────────────────────────────────────
        // NOTE: RenderBounds, WorldRenderBounds, PerInstanceCullingTag, and
        // MaterialMeshInfo are all added by MeshRendererBaker automatically
        // because this GameObject has a MeshRenderer component.
        // Adding them again here causes a duplicate-component baking error.
        // We only override RenderBounds with the first animation frame's bounds
        // if we actually have one — use SetComponent, not AddComponent.
        Mesh firstMesh = null;
        foreach (var clip in so.Clips)
            if (clip.Frames != null && clip.Frames.Count > 0) { firstMesh = clip.Frames[0]; break; }

        // ── Playback state ────────────────────────────────────────────────────
        int startClip = AnimMath.Clamp(authoring.StartClipIndex, 0, so.Clips.Count - 1);
        AddComponent(e, new AnimatedMeshState
        {
            ClipIndex = startClip,
            FrameIndex = 0,
            FrameAccumulator = 0f,
            FrameDuration = so.AnimationFPS > 0 ? 1f / so.AnimationFPS : 1f / 30f,
            IsPlaying = authoring.PlayOnStart,
            Loop = true,
        });

        AddComponent(e, new AnimatedMeshCommand
        {
            Type = AnimatedMeshCommandType.None,
            ClipIndex = -1,
            ClipNameHash = 0,
        });

        AddComponent(e, new AnimatedMeshTag());

        // ── Visibility (IEnableableComponent — no structural changes at runtime)
        AddComponent(e, new AnimatedMeshVisible());
        SetComponentEnabled<AnimatedMeshVisible>(e, true);
    }
}