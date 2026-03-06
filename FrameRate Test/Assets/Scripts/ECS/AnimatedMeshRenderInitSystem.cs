using Unity.Entities;
using Unity.Rendering;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// RENDER INIT SYSTEM
//
// Runs once per entity (tag AnimatedMeshNeedsRenderSetup is removed on exit).
// RenderMeshUtility.AddComponents is a structural change and cannot be avoided
// for the initial render setup, but it only fires once per entity lifetime.
//
// After AddComponents the archetype is rebuilt by DOTS, which zeros blittable
// component data. Every component that must survive is explicitly re-applied
// below — no further structural changes occur after this point.
// ─────────────────────────────────────────────────────────────────────────────

[UpdateInGroup(typeof(InitializationSystemGroup))]
public partial class AnimatedMeshRenderInitSystem : SystemBase
{
    protected override void OnUpdate()
    {
        var query = SystemAPI.QueryBuilder()
            .WithAll<AnimatedMeshNeedsRenderSetup, AnimatedMeshRenderSetupData, AnimatedMeshData>()
            .Build();

        if (query.IsEmpty) return;

        var entities = query.ToEntityArray(Unity.Collections.Allocator.Temp);

        foreach (var entity in entities)
        {
            var setupData = EntityManager.GetComponentObject<AnimatedMeshRenderSetupData>(entity);
            var animData = EntityManager.GetComponentObject<AnimatedMeshData>(entity);

            // Build the clip-name hash cache once so the command system never
            // calls string.GetHashCode() per entity per frame.
            animData.BuildHashCache();

            // ── Build flat mesh list ──────────────────────────────────────────
            var allMeshes = new System.Collections.Generic.List<Mesh>();
            foreach (var clip in animData.SO.Clips)
                if (clip.Frames != null)
                    foreach (var frame in clip.Frames)
                        if (frame != null)
                            allMeshes.Add(frame);

            if (allMeshes.Count == 0)
            {
                Debug.LogError("[AnimatedMesh] No valid frames found — cannot set up rendering.");
                EntityManager.RemoveComponent<AnimatedMeshNeedsRenderSetup>(entity);
                EntityManager.RemoveComponent<AnimatedMeshRenderSetupData>(entity);
                continue;
            }

            if (setupData.Materials == null || setupData.Materials.Length == 0)
            {
                Debug.LogError("[AnimatedMesh] No materials on MeshRenderer.");
                EntityManager.RemoveComponent<AnimatedMeshNeedsRenderSetup>(entity);
                EntityManager.RemoveComponent<AnimatedMeshRenderSetupData>(entity);
                continue;
            }

            // ── Snapshot all blittable state BEFORE structural change ─────────
            // RenderMeshUtility.AddComponents rebuilds the archetype, zeroing
            // every blittable component. We capture current values here and
            // restore them below so nothing is silently lost.
            bool hadState = EntityManager.HasComponent<AnimatedMeshState>(entity);
            bool hadCommand = EntityManager.HasComponent<AnimatedMeshCommand>(entity);
            bool hadTag = EntityManager.HasComponent<AnimatedMeshTag>(entity);

            var snapshotState = hadState
                ? EntityManager.GetComponentData<AnimatedMeshState>(entity)
                : default;
            var snapshotCmd = hadCommand
                ? EntityManager.GetComponentData<AnimatedMeshCommand>(entity)
                : default;

            // Snapshot clip offsets (DynamicBuffer — also lost after archetype change)
            var offsetSnapshot = new System.Collections.Generic.List<AnimatedMeshClipOffset>();
            if (EntityManager.HasBuffer<AnimatedMeshClipOffset>(entity))
            {
                var buf = EntityManager.GetBuffer<AnimatedMeshClipOffset>(entity);
                for (int i = 0; i < buf.Length; i++) offsetSnapshot.Add(buf[i]);
            }

            // ── One-time structural change (unavoidable for render setup) ─────
            var renderMeshArray = new RenderMeshArray(setupData.Materials, allMeshes.ToArray());
            var renderMeshDesc = new RenderMeshDescription(
                shadowCastingMode: setupData.ShadowMode,
                receiveShadows: setupData.ReceiveShadows);

            RenderMeshUtility.AddComponents(
                entity,
                EntityManager,
                renderMeshDesc,
                renderMeshArray,
                MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));

            // ── Restore / initialise AnimatedMeshState ────────────────────────
            var so = animData.SO;
            int startClip = AnimMath.Clamp(setupData.StartClipIndex, 0, so.Clips.Count - 1);

            // If a valid state was snapshotted use it; otherwise build from SO defaults.
            var newState = hadState && snapshotState.FrameDuration > 0f
                ? snapshotState
                : new AnimatedMeshState
                {
                    ClipIndex = startClip,
                    FrameIndex = 0,
                    FrameAccumulator = 0f,
                    FrameDuration = so.AnimationFPS > 0 ? 1f / so.AnimationFPS : 1f / 30f,
                    IsPlaying = setupData.PlayOnStart,
                    Loop = setupData.Loop,
                };

            if (EntityManager.HasComponent<AnimatedMeshState>(entity))
                EntityManager.SetComponentData(entity, newState);
            else
                EntityManager.AddComponentData(entity, newState);

            // ── Restore AnimatedMeshCommand ───────────────────────────────────
            var restoredCmd = hadCommand
                ? snapshotCmd
                : new AnimatedMeshCommand { Type = AnimatedMeshCommandType.None, ClipIndex = -1 };

            if (EntityManager.HasComponent<AnimatedMeshCommand>(entity))
                EntityManager.SetComponentData(entity, restoredCmd);
            else
                EntityManager.AddComponentData(entity, restoredCmd);

            // ── Restore AnimatedMeshTag ───────────────────────────────────────
            if (!EntityManager.HasComponent<AnimatedMeshTag>(entity))
                EntityManager.AddComponent<AnimatedMeshTag>(entity);

            // ── Restore clip offset buffer ────────────────────────────────────
            if (!EntityManager.HasBuffer<AnimatedMeshClipOffset>(entity))
            {
                var buf = EntityManager.AddBuffer<AnimatedMeshClipOffset>(entity);

                if (offsetSnapshot.Count > 0)
                {
                    // Re-use the snapshotted data (already correct from baker)
                    foreach (var entry in offsetSnapshot)
                        buf.Add(entry);
                }
                else
                {
                    // Rebuild from SO if snapshot was missing (e.g. first-time bake)
                    int cursor = 0;
                    foreach (var clip in so.Clips)
                    {
                        int count = clip.Frames?.Count ?? 0;
                        buf.Add(new AnimatedMeshClipOffset { FrameStart = cursor, FrameCount = count });
                        cursor += count;
                    }
                }
            }

            // ── Remove setup tags — no more structural changes after this ─────
            EntityManager.RemoveComponent<AnimatedMeshNeedsRenderSetup>(entity);
            EntityManager.RemoveComponent<AnimatedMeshRenderSetupData>(entity);
        }

        entities.Dispose();
    }
}