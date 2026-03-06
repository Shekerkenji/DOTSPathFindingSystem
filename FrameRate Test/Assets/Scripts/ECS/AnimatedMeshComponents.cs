using Unity.Entities;
using Unity.Rendering;
using UnityEngine;

// ── Managed component: SO reference + pre-hashed clip name lookup ─────────────
//
// ClipNameHashes is populated once by AnimatedMeshRenderInitSystem so that
// ByName commands never call string.GetHashCode() inside the per-entity loop.

public class AnimatedMeshData : IComponentData
{
    public AnimatedMeshScriptableObjectECS SO;

    /// <summary>
    /// Parallel array to SO.Clips. Each entry is clip.Name.GetHashCode(),
    /// computed once at init time so the command system does zero string work.
    /// </summary>
    public int[] ClipNameHashes;

    /// <summary>
    /// Initialise (or re-initialise) the hash cache from the current SO.
    /// Call this once after assigning SO.
    /// </summary>
    public void BuildHashCache()
    {
        if (SO == null) { ClipNameHashes = System.Array.Empty<int>(); return; }
        var clips = SO.Clips;
        ClipNameHashes = new int[clips.Count];
        for (int i = 0; i < clips.Count; i++)
            ClipNameHashes[i] = clips[i].Name != null ? clips[i].Name.GetHashCode() : 0;
    }
}

// ── Blittable: per-entity playback state (lives in chunk) ────────────────────

public struct AnimatedMeshState : IComponentData
{
    public int ClipIndex;
    public int FrameIndex;
    public float FrameAccumulator;
    public float FrameDuration;   // 1 / AnimationFPS
    public bool IsPlaying;
    public bool Loop;
}

// ── Blittable: frame offset into the RenderMeshArray for each clip ───────────

/// <summary>
/// Stores where each clip's frames start inside the single RenderMeshArray
/// baked onto the entity. The system adds FrameIndex to FrameStart to get
/// the final mesh index passed to MaterialMeshInfo.
/// </summary>
public struct AnimatedMeshClipOffset : IBufferElementData
{
    /// <summary>Index of this clip's first frame inside RenderMeshArray.</summary>
    public int FrameStart;
    /// <summary>Number of frames in this clip.</summary>
    public int FrameCount;
}

// ── Tag ───────────────────────────────────────────────────────────────────────

public struct AnimatedMeshTag : IComponentData { }

// ── Command ───────────────────────────────────────────────────────────────────

public enum AnimatedMeshCommandType : byte
{
    None = 0,
    ByIndex = 1,
    ByName = 2,
    Pause = 3,
    Resume = 4,
    Stop = 5,
}

public struct AnimatedMeshCommand : IComponentData
{
    public AnimatedMeshCommandType Type;
    public int ClipIndex;
    public int ClipNameHash;
    public bool ForceRestart;
    public bool Loop;
    public bool OverrideLoop;
}

// ── One-frame events ──────────────────────────────────────────────────────────

public struct AnimationCompletedEvent : IComponentData { public int ClipIndex; }
public struct AnimationLoopedEvent : IComponentData { public int ClipIndex; }

// ── Internal math helper ──────────────────────────────────────────────────────

internal static class AnimMath
{
    public static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
}

// ── Render setup helpers ──────────────────────────────────────────────────────

/// <summary>
/// Holds only asset references (Material[], shadow settings) — no scene objects.
/// Safe for subscene serialization. Consumed once by AnimatedMeshRenderInitSystem.
/// </summary>
public class AnimatedMeshRenderSetupData : IComponentData
{
    public UnityEngine.Material[] Materials;
    public UnityEngine.Rendering.ShadowCastingMode ShadowMode;
    public bool ReceiveShadows;
    public int StartClipIndex;
    public bool PlayOnStart;
    public bool Loop;
}

/// <summary>
/// Tag that tells AnimatedMeshRenderInitSystem this entity still needs
/// RenderMeshArray added via EntityManager. Removed after init — the only
/// structural changes in the entire system happen inside that one-time path.
/// </summary>
public struct AnimatedMeshNeedsRenderSetup : IComponentData { }