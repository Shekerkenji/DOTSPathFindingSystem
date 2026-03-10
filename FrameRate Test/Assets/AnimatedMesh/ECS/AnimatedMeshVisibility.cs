// =============================================================================
// AnimatedMeshVisibility.cs
//
// Visibility gating + advance/swap for NON-LOD entities.
//
// LOD entities (those with AnimatedMeshLODState) are handled by
// AdvanceLODJob / MeshSwapLODJob in AnimatedMeshLODSystem.cs.
// The jobs here use WithNone<AnimatedMeshLODState> so both sets coexist
// on disjoint entity sets with no overlap.
//
// FIXES vs original:
//   • Camera.main cached — was calling FindObjectOfType every frame.
//   • GeometryUtility.CalculateFrustumPlanes uses the Plane[] overload with a
//     reused buffer — original allocated a new Plane[6] every frame.
//   • FrustumPlanes now takes Plane[] instead of Camera.
//   • cam.rect restore is inside try/finally — guaranteed even on exception.
//   • AdvanceJob / MeshSwapJob gain WithNone<AnimatedMeshLODState> so they
//     only process non-LOD entities.
// =============================================================================

using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using UnityEngine;

// =============================================================================
// COMPONENT
// =============================================================================

/// <summary>
/// Enableable tag. ENABLED = inside camera frustum. DISABLED = culled.
/// Never structurally added or removed after bake — only the enabled bit moves.
/// </summary>
public struct AnimatedMeshVisible : IComponentData, IEnableableComponent { }


// =============================================================================
// FRUSTUM HELPER
// =============================================================================

public struct FrustumPlanes
{
    float4 P0, P1, P2, P3, P4, P5;

    /// <summary>Build from a pre-filled Plane[6] — no allocation, no Camera API.</summary>
    public FrustumPlanes(Plane[] p)
    {
        P0 = Pack(p[0]); P1 = Pack(p[1]); P2 = Pack(p[2]);
        P3 = Pack(p[3]); P4 = Pack(p[4]); P5 = Pack(p[5]);
    }

    static float4 Pack(Plane p) => new float4(p.normal.x, p.normal.y, p.normal.z, p.distance);

    static bool Outside(float4 plane, float3 c, float3 e)
    {
        float r = math.dot(math.abs(plane.xyz), e);
        float d = math.dot(plane.xyz, c) + plane.w;
        return d < -r;
    }

    public bool Intersects(float3 center, float3 extents) =>
        !Outside(P0, center, extents) && !Outside(P1, center, extents) &&
        !Outside(P2, center, extents) && !Outside(P3, center, extents) &&
        !Outside(P4, center, extents) && !Outside(P5, center, extents);
}


// =============================================================================
// CULL SYSTEM
// =============================================================================

// ISystem structs cannot hold managed fields (Camera, Plane[]).
// Converted to SystemBase which runs on the main thread anyway
// (Camera API requires main thread regardless).
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateBefore(typeof(AnimatedMeshAdvanceSystem))]
[UpdateAfter(typeof(AnimatedMeshCommandSystem))]
public partial class AnimatedMeshFrustumCullSystem : SystemBase
{
    private Camera _camera;
    private Plane[] _planeBuffer;

    protected override void OnCreate()
    {
        _planeBuffer = new Plane[6];
        RequireForUpdate<AnimatedMeshTag>();
    }

    protected override void OnUpdate()
    {
        if (_camera == null) _camera = Camera.main;
        if (_camera == null) return;

        ExtractFrustumPlanes(_camera, _planeBuffer, shrinkPixels: 60f);
        var planes = new FrustumPlanes(_planeBuffer);

        Dependency = new CullJob { Planes = planes }
            .ScheduleParallel(Dependency);
    }

    private static void ExtractFrustumPlanes(Camera cam, Plane[] buffer, float shrinkPixels)
    {
        Rect original = cam.rect;
        try
        {
            if (shrinkPixels > 0f)
            {
                float shrink = shrinkPixels / Screen.width;
                cam.rect = new Rect(shrink, 0f, 1f - shrink * 2f, 1f);
            }
            GeometryUtility.CalculateFrustumPlanes(cam, buffer);
        }
        finally
        {
            cam.rect = original;
        }
    }
}

[BurstCompile]
[WithAll(typeof(AnimatedMeshTag))]
[WithOptions(EntityQueryOptions.IgnoreComponentEnabledState)]
public partial struct CullJob : IJobEntity
{
    [ReadOnly] public FrustumPlanes Planes;

    [BurstCompile]
    void Execute(in WorldRenderBounds bounds,
                 EnabledRefRW<AnimatedMeshVisible> visible)
    {
        bool shouldBeVisible = Planes.Intersects(bounds.Value.Center, bounds.Value.Extents);
        if (visible.ValueRO != shouldBeVisible)
            visible.ValueRW = shouldBeVisible;
    }
}


// =============================================================================
// ADVANCE SYSTEM  (non-LOD entities only)
// =============================================================================

[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(AnimatedMeshFrustumCullSystem))]
[UpdateAfter(typeof(AnimatedMeshCommandSystem))]
public partial struct AnimatedMeshAdvanceSystem : ISystem
{
    public void OnUpdate(ref SystemState state)  // no [BurstCompile] here
    {
        state.Dependency = new AdvanceJob { DeltaTime = SystemAPI.Time.DeltaTime }
            .ScheduleParallel(state.Dependency);
        state.Dependency = new MeshSwapJob()
            .ScheduleParallel(state.Dependency);
    }
}

// ── Advance: non-LOD entities, on-screen or off ───────────────────────────────

[BurstCompile]
[WithAll(typeof(AnimatedMeshTag))]
[WithNone(typeof(AnimatedMeshLODState))]          // LOD entities handled in AnimatedMeshLODSystem
[WithOptions(EntityQueryOptions.IgnoreComponentEnabledState)]
public partial struct AdvanceJob : IJobEntity
{
    public float DeltaTime;

    [BurstCompile]
    void Execute(ref AnimatedMeshState animState,
                 in DynamicBuffer<AnimatedMeshClipOffset> offsets)
    {
        if (!animState.IsPlaying) return;

        var clipOffset = offsets[animState.ClipIndex];
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

// ── Swap: visible non-LOD entities only ──────────────────────────────────────

[BurstCompile]
[WithAll(typeof(AnimatedMeshTag), typeof(AnimatedMeshVisible))]
[WithNone(typeof(AnimatedMeshLODState))]          // LOD entities handled in AnimatedMeshLODSystem
public partial struct MeshSwapJob : IJobEntity
{
    [BurstCompile]
    void Execute(in AnimatedMeshState animState,
                 ref MaterialMeshInfo meshInfo,
                 in DynamicBuffer<AnimatedMeshClipOffset> offsets)
    {
        var offset = offsets[animState.ClipIndex];
        int safeFrame = math.clamp(animState.FrameIndex, 0, offset.FrameCount - 1);
        int meshIndex = offset.FrameStart + safeFrame;

        var desired = MaterialMeshInfo.FromRenderMeshArrayIndices(0, meshIndex);
        if (meshInfo.Mesh != desired.Mesh)
            meshInfo = desired;
    }
}