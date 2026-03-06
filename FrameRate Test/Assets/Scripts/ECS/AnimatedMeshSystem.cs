using Unity.Burst;
using Unity.Entities;
using Unity.Rendering;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// 1. COMMAND SYSTEM
//    Single query pass. Early-out on None so we never touch SO on idle frames.
//    Cannot be Burst-compiled because ByName touches managed AnimatedMeshData.
// ─────────────────────────────────────────────────────────────────────────────

[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateBefore(typeof(AnimatedMeshAdvanceSystem))]
public partial struct AnimatedMeshCommandSystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        foreach (var (cmd, animState, offsets, data) in
            SystemAPI.Query<
                RefRW<AnimatedMeshCommand>,
                RefRW<AnimatedMeshState>,
                DynamicBuffer<AnimatedMeshClipOffset>,
                AnimatedMeshData>()
            .WithAll<AnimatedMeshTag>())
        {
            // ── Early-out — the common case every frame ───────────────────────
            if (cmd.ValueRO.Type == AnimatedMeshCommandType.None) continue;

            switch (cmd.ValueRO.Type)
            {
                // ── Simple playback control ───────────────────────────────────
                case AnimatedMeshCommandType.Pause:
                    animState.ValueRW.IsPlaying = false;
                    break;

                case AnimatedMeshCommandType.Resume:
                    animState.ValueRW.IsPlaying = true;
                    break;

                case AnimatedMeshCommandType.Stop:
                    animState.ValueRW.IsPlaying = false;
                    animState.ValueRW.FrameIndex = 0;
                    animState.ValueRW.FrameAccumulator = 0f;
                    break;

                // ── Clip switch by index (buffer only, no SO) ─────────────────
                case AnimatedMeshCommandType.ByIndex:
                    {
                        int idx = AnimMath.Clamp(cmd.ValueRO.ClipIndex, 0, offsets.Length - 1);
                        if (idx != animState.ValueRO.ClipIndex || cmd.ValueRO.ForceRestart)
                        {
                            animState.ValueRW.ClipIndex = idx;
                            animState.ValueRW.FrameIndex = 0;
                            animState.ValueRW.FrameAccumulator = 0f;
                            animState.ValueRW.IsPlaying = true;
                            if (cmd.ValueRO.OverrideLoop) animState.ValueRW.Loop = cmd.ValueRO.Loop;
                        }
                        break;
                    }

                // ── Clip switch by name (uses pre-hashed cache, no per-frame string alloc) ─
                case AnimatedMeshCommandType.ByName:
                    {
                        int hash = cmd.ValueRO.ClipNameHash;
                        var hashes = data.ClipNameHashes;
                        int idx = -1;
                        if (hashes != null)
                            for (int i = 0; i < hashes.Length; i++)
                                if (hashes[i] == hash) { idx = i; break; }

                        if (idx < 0)
                            Debug.LogWarning($"[AnimatedMesh] No clip for hash {hash}");
                        else if (idx != animState.ValueRO.ClipIndex || cmd.ValueRO.ForceRestart)
                        {
                            animState.ValueRW.ClipIndex = idx;
                            animState.ValueRW.FrameIndex = 0;
                            animState.ValueRW.FrameAccumulator = 0f;
                            animState.ValueRW.IsPlaying = true;
                            if (cmd.ValueRO.OverrideLoop) animState.ValueRW.Loop = cmd.ValueRO.Loop;
                        }
                        break;
                    }
            }

            // Clear command — single write point regardless of branch taken
            cmd.ValueRW.Type = AnimatedMeshCommandType.None;
        }
    }
}


// ─────────────────────────────────────────────────────────────────────────────
// 2. ADVANCE SYSTEM
//    Pure blittable data only → fully Burst-compiled, main-thread scheduled.
//    No ECB, no structural changes, no allocations.
// ─────────────────────────────────────────────────────────────────────────────

[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(AnimatedMeshCommandSystem))]
[UpdateBefore(typeof(AnimatedMeshMeshSwapSystem))]
public partial struct AnimatedMeshAdvanceSystem : ISystem
{
    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        float dt = SystemAPI.Time.DeltaTime;

        foreach (var (animState, offsets) in
            SystemAPI.Query<RefRW<AnimatedMeshState>, DynamicBuffer<AnimatedMeshClipOffset>>()
            .WithAll<AnimatedMeshTag>())
        {
            if (!animState.ValueRO.IsPlaying) continue;

            int frameCount = offsets[animState.ValueRO.ClipIndex].FrameCount;
            if (frameCount == 0) continue;

            // ── Work on a local copy so we only write back when something changed.
            // This avoids dirtying the AnimatedMeshState chunk on sub-frame ticks,
            // which would otherwise cause EntitiesGraphics to re-upload batch data.
            float accumulator = animState.ValueRO.FrameAccumulator + dt;
            float duration = animState.ValueRO.FrameDuration;

            if (accumulator < duration)
            {
                // Sub-frame tick — only accumulator changed, write just that field.
                animState.ValueRW.FrameAccumulator = accumulator;
                continue;
            }

            int frameIndex = animState.ValueRO.FrameIndex;
            bool isPlaying = true;

            while (accumulator >= duration)
            {
                accumulator -= duration;
                frameIndex++;

                if (frameIndex >= frameCount)
                {
                    if (animState.ValueRO.Loop)
                    {
                        frameIndex = 0;
                        accumulator = 0f;
                    }
                    else
                    {
                        frameIndex = frameCount - 1;
                        accumulator = 0f;
                        isPlaying = false;
                    }
                    break;
                }
            }

            // Single write — only reaches here when FrameIndex actually changed.
            animState.ValueRW.FrameIndex = frameIndex;
            animState.ValueRW.FrameAccumulator = accumulator;
            animState.ValueRW.IsPlaying = isPlaying;
        }
    }
}


// ─────────────────────────────────────────────────────────────────────────────
// 3. MESH SWAP SYSTEM
//    Guarded write: MaterialMeshInfo is only written when the mesh index
//    actually changes. An unconditional write every frame marks the DOTS
//    batch dirty regardless of value, forcing EntitiesGraphicsSystem to call
//    UpdateAllBatches and re-upload GPU data — the root cause of the
//    Semaphore.WaitForSignal stall visible in the profiler.
// ─────────────────────────────────────────────────────────────────────────────

[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(AnimatedMeshAdvanceSystem))]
public partial struct AnimatedMeshMeshSwapSystem : ISystem
{
    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        foreach (var (animState, offsets, meshInfo) in
            SystemAPI.Query<
                RefRO<AnimatedMeshState>,
                DynamicBuffer<AnimatedMeshClipOffset>,
                RefRW<MaterialMeshInfo>>()
            .WithAll<AnimatedMeshTag>())
        {
            var offset = offsets[animState.ValueRO.ClipIndex];
            int safeFrame = AnimMath.Clamp(animState.ValueRO.FrameIndex, 0, offset.FrameCount - 1);
            int meshIndex = offset.FrameStart + safeFrame;

            // Only write — and therefore only dirty the batch — when the
            // mesh index has actually changed since last frame.
            var desired = MaterialMeshInfo.FromRenderMeshArrayIndices(0, meshIndex);
            if (meshInfo.ValueRO.Mesh != desired.Mesh)
                meshInfo.ValueRW = desired;
        }
    }
}


// ─────────────────────────────────────────────────────────────────────────────
// NOTE: AnimationCompletedEvent system is intentionally omitted.
// Poll animState.IsPlaying == false to detect non-looping clip completion.
// ─────────────────────────────────────────────────────────────────────────────