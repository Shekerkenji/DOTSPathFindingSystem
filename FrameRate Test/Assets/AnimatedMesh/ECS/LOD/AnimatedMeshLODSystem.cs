using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;

// =============================================================================
// AnimatedMeshLODSystem.cs
//
// THE KEY FIX:
//   The original AnimatedMeshLODRebuildSystem called RenderMeshUtility.AddComponents
//   on every LOD transition — a full archetype rebuild. With 1600 entities crossing
//   distance thresholds simultaneously (load, fast camera move) this caused massive
//   sync-point spikes on the main thread.
//
//   The new approach: ALL three LODs' meshes are packed into ONE RenderMeshArray
//   at init time (see AnimatedMeshRenderInitSystem). Switching LODs is now just
//   writing a new buffer base index into AnimatedMeshLODState — a blittable data
//   write that can happen entirely inside a Burst parallel job.
//
//   Zero structural changes at runtime. Zero archetype rebuilds. Zero sync points.
//
// SYSTEMS:
//
//   AnimatedMeshLODCheckSystem  — Burst, ScheduleParallel, every frame.
//       Computes squared camera distance, resolves desired LOD, and if it differs
//       from ActiveLOD writes the new level directly into AnimatedMeshLODState
//       plus updates AnimatedMeshState.FrameDuration and resets ClipIndex/Frame.
//       No structural change of any kind.
//
//   AnimatedMeshLODRebuildSystem — REMOVED.
//       The rebuild step no longer exists because there is nothing to rebuild.
//       AnimatedMeshLODNeedsRebuild and AnimatedMeshLODRebuildRequest are kept
//       as components so the baker and existing serialized data still compile,
//       but they are no longer written or read at runtime.
// =============================================================================


// =============================================================================
// 1.  CAMERA POSITION SINGLETON
//     Extracted on the main thread each frame, passed by value into the job.
//     Avoids Camera.main inside the Burst job (managed API, not Burst-compatible).
// =============================================================================

[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateBefore(typeof(AnimatedMeshAdvanceSystem))]
[UpdateAfter(typeof(AnimatedMeshCommandSystem))]
public partial class AnimatedMeshLODCheckSystem : SystemBase
{
    private Camera _camera;

    protected override void OnCreate()
    {
        RequireForUpdate<AnimatedMeshTag>();
        RequireForUpdate<AnimatedMeshLODState>();
    }

    protected override void OnUpdate()
    {
        if (_camera == null) _camera = Camera.main;
        if (_camera == null) return;

        float3 camPos = _camera.transform.position;

        Dependency = new CheckLODJob { CameraPosition = camPos }
            .ScheduleParallel(Dependency);
    }
}


// =============================================================================
// 2.  CHECK + SWITCH JOB  (Burst, parallel)
//
// When the desired LOD differs from the active one this job:
//   a) Updates AnimatedMeshLODState.ActiveLOD — changes the buffer base that
//      AdvanceJob and MeshSwapJob use to index into the ClipOffset buffer.
//   b) Updates AnimatedMeshState.FrameDuration to the new LOD's FPS.
//   c) Tries to preserve the clip index by clamping to the new LOD's clip count.
//      If the clip index is out of range for the new LOD it resets to 0.
//   d) Resets FrameIndex and FrameAccumulator so the new LOD starts cleanly.
//
// All of this is plain blittable data. No structural changes. No ECB.
// Safe for ScheduleParallel — each entity owns its own components.
// =============================================================================

[BurstCompile]
[WithAll(typeof(AnimatedMeshTag))]
[WithOptions(EntityQueryOptions.IgnoreComponentEnabledState)]
public partial struct CheckLODJob : IJobEntity
{
    [ReadOnly] public float3 CameraPosition;

    [BurstCompile]
    void Execute(
        in LocalToWorld ltw,
        ref AnimatedMeshLODState lodState,
        ref AnimatedMeshState animState)
    {
        float distSq = math.distancesq(ltw.Position, CameraPosition);

        // Resolve desired LOD level from distance thresholds.
        // A threshold of 0 means that level is not configured — skip it.
        int desired;
        if (lodState.LOD2DistanceSq > 0f && distSq >= lodState.LOD2DistanceSq)
            desired = 2;
        else if (lodState.LOD1DistanceSq > 0f && distSq >= lodState.LOD1DistanceSq)
            desired = 1;
        else
            desired = 0;

        if (desired == lodState.ActiveLOD) return;  // nothing to do — common case

        // ── Switch LOD ────────────────────────────────────────────────────────
        // Update the active level. AdvanceJob and MeshSwapJob read
        // lodState.ActiveLodBufferBase to offset into the ClipOffset buffer,
        // so this single int write is the entire "LOD swap" at runtime.
        lodState.ActiveLOD = desired;

        // Update frame duration to match the new LOD's animation FPS.
        animState.FrameDuration = lodState.ActiveLodFrameDuration;

        // Clamp clip index to the new LOD's clip count so we never read out of
        // bounds. If the clip exists in the new LOD keep it; otherwise reset.
        int newClipCount = lodState.ActiveLodClipCount;
        if (animState.ClipIndex >= newClipCount)
            animState.ClipIndex = 0;

        // Reset frame position for a clean start on the new mesh set.
        animState.FrameIndex = 0;
        animState.FrameAccumulator = 0f;
    }
}


// =============================================================================
// 3.  ADVANCE + SWAP JOBS  (updated to respect LOD buffer base)
//
// AdvanceJob and MeshSwapJob in AnimatedMeshVisibility.cs index into the
// ClipOffset buffer using animState.ClipIndex directly. For LOD entities the
// buffer contains all three LODs' offsets packed together, so we must add the
// active LOD's buffer base before indexing.
//
// We override AdvanceJob and MeshSwapJob with LOD-aware variants that use
// lodState.ActiveLodBufferBase as the offset. Entities without AnimatedMeshLODState
// are handled by the original jobs in AnimatedMeshVisibility.cs — both sets of
// jobs coexist because they target disjoint entity sets via WithAll/WithNone.
// =============================================================================

// ── LOD-aware Advance: all LOD entities, on-screen or off ────────────────────

[BurstCompile]
[WithAll(typeof(AnimatedMeshTag), typeof(AnimatedMeshLODState))]
[WithOptions(EntityQueryOptions.IgnoreComponentEnabledState)]
public partial struct AdvanceLODJob : IJobEntity
{
    public float DeltaTime;

    [BurstCompile]
    void Execute(
        ref AnimatedMeshState animState,
        in AnimatedMeshLODState lodState,
        in DynamicBuffer<AnimatedMeshClipOffset> offsets)
    {
        if (!animState.IsPlaying) return;

        // Buffer index = LOD base + clip index within that LOD.
        int bufferIndex = lodState.ActiveLodBufferBase + animState.ClipIndex;
        if ((uint)bufferIndex >= (uint)offsets.Length) return;

        var clipOffset = offsets[bufferIndex];
        int frameCount = clipOffset.FrameCount;
        if (frameCount == 0) return;

        float accumulator = animState.FrameAccumulator + DeltaTime;
        float duration = animState.FrameDuration;

        if (accumulator < duration)
        {
            animState.FrameAccumulator = accumulator;
            return;
        }

        int steps = (int)math.floor(accumulator / duration);
        int newFrame = animState.FrameIndex + steps;
        float newAccum = accumulator - steps * duration;

        if (newFrame >= frameCount)
        {
            if (animState.Loop)
            {
                newFrame = newFrame % frameCount;
                newAccum = 0f;
            }
            else
            {
                newFrame = frameCount - 1;
                newAccum = 0f;
                animState.IsPlaying = false;
            }
        }

        animState.FrameIndex = newFrame;
        animState.FrameAccumulator = newAccum;
    }
}

// ── LOD-aware Swap: visible LOD entities only ─────────────────────────────────

[BurstCompile]
[WithAll(typeof(AnimatedMeshTag), typeof(AnimatedMeshVisible), typeof(AnimatedMeshLODState))]
public partial struct MeshSwapLODJob : IJobEntity
{
    [BurstCompile]
    void Execute(
        in AnimatedMeshState animState,
        in AnimatedMeshLODState lodState,
        ref MaterialMeshInfo meshInfo,
        in DynamicBuffer<AnimatedMeshClipOffset> offsets)
    {
        int bufferIndex = lodState.ActiveLodBufferBase + animState.ClipIndex;
        if ((uint)bufferIndex >= (uint)offsets.Length) return;

        var offset = offsets[bufferIndex];
        int safeFrame = math.clamp(animState.FrameIndex, 0, offset.FrameCount - 1);
        int meshIndex = offset.FrameStart + safeFrame;

        var desired = MaterialMeshInfo.FromRenderMeshArrayIndices(0, meshIndex);
        if (meshInfo.Mesh != desired.Mesh)
            meshInfo = desired;
    }
}


// =============================================================================
// 4.  LOD-AWARE ADVANCE SYSTEM
//     Schedules the LOD variants AFTER the standard jobs so all entities are
//     covered: non-LOD entities by AdvanceJob/MeshSwapJob (WithNone<LODState>),
//     LOD entities by AdvanceLODJob/MeshSwapLODJob (WithAll<LODState>).
// =============================================================================

[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(AnimatedMeshFrustumCullSystem))]
[UpdateAfter(typeof(AnimatedMeshCommandSystem))]
[UpdateAfter(typeof(AnimatedMeshLODCheckSystem))]
public partial struct AnimatedMeshLODAdvanceSystem : ISystem
{
    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        state.Dependency = new AdvanceLODJob { DeltaTime = SystemAPI.Time.DeltaTime }
            .ScheduleParallel(state.Dependency);

        state.Dependency = new MeshSwapLODJob()
            .ScheduleParallel(state.Dependency);
    }
}