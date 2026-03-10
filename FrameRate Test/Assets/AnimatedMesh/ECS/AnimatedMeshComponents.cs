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
///
/// For LOD entities the buffer contains ALL clips for ALL levels:
///   [ LOD0 clips... | LOD1 clips... | LOD2 clips... ]
/// AnimatedMeshLODState.ActiveLodBufferBase is added to ClipIndex before
/// indexing this buffer, so the correct LOD region is always addressed
/// without any structural change.
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

// ── LOD blittable state ───────────────────────────────────────────────────────

/// <summary>
/// Per-entity LOD state. All fields are blittable — lives in chunk memory,
/// readable from Burst jobs.
///
/// KEY DESIGN: instead of swapping the RenderMeshArray at LOD transition time
/// (which requires RenderMeshUtility.AddComponents → full archetype rebuild),
/// ALL three LODs' meshes are packed into ONE RenderMeshArray at init time:
///
///   [ LOD0 clips… | LOD1 clips… | LOD2 clips… ]
///
/// ActiveLodBufferBase stores the ClipOffset buffer index where the active
/// LOD's clips start. Switching LODs is then just writing three ints — zero
/// structural change, safe from a Burst parallel job.
///
/// ActiveLodFrameDuration caches 1/FPS for the active LOD so AdvanceJob never
/// needs to touch managed SO data at runtime.
/// </summary>
public struct AnimatedMeshLODState : IComponentData
{
    // Distance thresholds (squared, world units).  0 = level not configured.
    public float LOD1DistanceSq;
    public float LOD2DistanceSq;

    // Which LOD is currently displayed.
    public int ActiveLOD;

    // Buffer offset: index of the first AnimatedMeshClipOffset entry for each LOD.
    // Set once by AnimatedMeshRenderInitSystem, never changed at runtime.
    public int LodBufferBase0;   // always 0
    public int LodBufferBase1;   // = clip count of LOD0
    public int LodBufferBase2;   // = clip count of LOD0 + LOD1

    // Clip counts per LOD — used by the LOD check job to clamp ClipIndex.
    public int LodClipCount0;
    public int LodClipCount1;
    public int LodClipCount2;

    // Frame duration (1/FPS) per LOD — written at init, read by AdvanceJob.
    public float LodFrameDuration0;
    public float LodFrameDuration1;
    public float LodFrameDuration2;

    // Convenience: the buffer base for the currently active LOD.
    public readonly int ActiveLodBufferBase => ActiveLOD switch
    {
        1 => LodBufferBase1,
        2 => LodBufferBase2,
        _ => LodBufferBase0,
    };

    // Convenience: frame duration for the currently active LOD.
    public readonly float ActiveLodFrameDuration => ActiveLOD switch
    {
        1 => LodFrameDuration1,
        2 => LodFrameDuration2,
        _ => LodFrameDuration0,
    };

    // Convenience: clip count for the currently active LOD.
    public readonly int ActiveLodClipCount => ActiveLOD switch
    {
        1 => LodClipCount1,
        2 => LodClipCount2,
        _ => LodClipCount0,
    };
}

/// <summary>
/// Enableable tag — set by CheckLODJob when a transition is needed.
/// Using IEnableableComponent means flipping this is a bitmask write only,
/// no structural change, safe from ScheduleParallel.
/// </summary>
public struct AnimatedMeshLODNeedsRebuild : IComponentData, IEnableableComponent { }

/// <summary>
/// Blittable payload written by CheckLODJob alongside the enabled bit.
/// Tells AnimatedMeshLODSwitchSystem which level to transition to.
/// </summary>
public struct AnimatedMeshLODRebuildRequest : IComponentData
{
    public int TargetLOD;
}