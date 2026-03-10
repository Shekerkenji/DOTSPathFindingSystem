using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using UnityEngine;

// =============================================================================
// AnimatedMeshSystem.cs
//
// Contains only AnimatedMeshCommandSystem.
//
// FIX: AdvanceAndSwapJob removed — it was never scheduled by any system and
// was dead code. Advance and swap work is done by AdvanceJob + MeshSwapJob
// (non-LOD) in AnimatedMeshVisibility.cs, and AdvanceLODJob + MeshSwapLODJob
// (LOD) in AnimatedMeshLODSystem.cs.
//
// COMMAND SYSTEM
//
// Only iterates entities whose AnimatedMeshCommand chunk has actually changed
// this frame via WithChangeFilter. On idle frames the query returns zero
// chunks — zero work regardless of entity count.
// =============================================================================

[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateBefore(typeof(AnimatedMeshAdvanceSystem))]
public partial struct AnimatedMeshCommandSystem : ISystem
{
    private EntityQuery _query;

    public void OnCreate(ref SystemState state)
    {
        _query = SystemAPI.QueryBuilder()
            .WithAll<AnimatedMeshCommand, AnimatedMeshState, AnimatedMeshTag>()
            .WithAll<AnimatedMeshClipOffset>()
            .Build();

        _query.AddChangedVersionFilter(ComponentType.ReadOnly<AnimatedMeshCommand>());
        state.RequireForUpdate(_query);
    }

    public void OnUpdate(ref SystemState state)
    {
        foreach (var (cmd, animState, offsets, data) in
            SystemAPI.Query<
                RefRW<AnimatedMeshCommand>,
                RefRW<AnimatedMeshState>,
                DynamicBuffer<AnimatedMeshClipOffset>,
                AnimatedMeshData>()
            .WithAll<AnimatedMeshTag>()
            .WithChangeFilter<AnimatedMeshCommand>())
        {
            if (cmd.ValueRO.Type == AnimatedMeshCommandType.None) continue;

            switch (cmd.ValueRO.Type)
            {
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

                case AnimatedMeshCommandType.ByIndex:
                    {
                        int idx = math.clamp(cmd.ValueRO.ClipIndex, 0, offsets.Length - 1);
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

            cmd.ValueRW.Type = AnimatedMeshCommandType.None;
        }
    }
}