using System.Collections.Generic;
using Unity.Entities;
using Unity.Rendering;
using UnityEngine;

// =============================================================================
// AnimatedMeshRenderInitSystem.cs
//
// One-time init system.  Runs only for entities that have
// AnimatedMeshNeedsRenderSetup, which is removed at the end of each entity's
// setup pass — so this system is completely dormant at steady state.
//
// LOD CHANGE vs original:
//   Previously the init system only baked LOD0 meshes into the RenderMeshArray,
//   and AnimatedMeshLODRebuildSystem called RenderMeshUtility.AddComponents again
//   at every LOD transition — a full archetype rebuild per transition.
//
//   Now ALL three LODs' meshes are packed into ONE RenderMeshArray here:
//
//     [ LOD0 clip0 frames | LOD0 clip1 frames | … | LOD1 … | LOD2 … ]
//
//   AnimatedMeshLODState.LodBufferBase{0,1,2} record where each LOD starts in
//   the ClipOffset buffer.  Switching LODs at runtime is then a plain data write
//   (three ints + one float) — zero structural change, zero archetype rebuild,
//   safe from a Burst parallel job.
//
// BATCHING FIX:
//   EntitiesGraphics only GPU-instances entities that share the EXACT SAME
//   RenderMeshArray object reference. Previously every entity called
//   new RenderMeshArray(...) independently, producing up to 1600 unique
//   instances and therefore 1600 separate draw calls (seen as 1.05M batches
//   in the profiler). The _renderMeshArrayCache ensures all entities that
//   use the same SO + materials combination share one RenderMeshArray instance,
//   collapsing them into a single instanced draw call.
// =============================================================================

[UpdateInGroup(typeof(InitializationSystemGroup))]
public partial class AnimatedMeshRenderInitSystem : SystemBase
{
    // Key: (LOD0 SO, LOD1 SO, LOD2 SO, materials array reference).
    // Value: the shared RenderMeshArray instance for that combination.
    // Allocated once, lives for the lifetime of the system.
    private readonly Dictionary<RenderMeshCacheKey, RenderMeshArray> _renderMeshArrayCache = new();

    private readonly struct RenderMeshCacheKey : System.IEquatable<RenderMeshCacheKey>
    {
        readonly AnimatedMeshScriptableObjectECS SO0, SO1, SO2;
        readonly Material[] Materials;  // compared by reference — same array = same key

        public RenderMeshCacheKey(
            AnimatedMeshScriptableObjectECS so0,
            AnimatedMeshScriptableObjectECS so1,
            AnimatedMeshScriptableObjectECS so2,
            Material[] materials)
        { SO0 = so0; SO1 = so1; SO2 = so2; Materials = materials; }

        public bool Equals(RenderMeshCacheKey o) =>
            SO0 == o.SO0 && SO1 == o.SO1 && SO2 == o.SO2 &&
            ReferenceEquals(Materials, o.Materials);

        public override bool Equals(object obj) =>
            obj is RenderMeshCacheKey k && Equals(k);

        public override int GetHashCode() =>
            System.HashCode.Combine(SO0, SO1, SO2, Materials);
    }

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

            animData.BuildHashCache();

            // ── Determine whether this is a LOD entity ────────────────────────
            bool isLOD = EntityManager.HasComponent<AnimatedMeshLODSetupData>(entity);
            AnimatedMeshLODSetupData lodSetup = isLOD
                ? EntityManager.GetComponentObject<AnimatedMeshLODSetupData>(entity)
                : null;

            // ── Build the combined mesh list ──────────────────────────────────
            // Non-LOD: just LOD0.
            // LOD:     LOD0 meshes, then LOD1 meshes, then LOD2 meshes — all in
            //          one flat array so RenderMeshUtility.AddComponents is called
            //          exactly once, ever.

            var allMeshes = new List<Mesh>();

            // LOD0 (= the main SO, shared with non-LOD path)
            int lod0MeshStart = 0;
            AppendMeshes(animData.SO, allMeshes);
            int lod0MeshCount = allMeshes.Count;

            // LOD1 / LOD2 (only for LOD entities)
            int lod1MeshStart = allMeshes.Count;
            int lod1MeshCount = 0;
            int lod2MeshStart = 0;
            int lod2MeshCount = 0;

            if (isLOD)
            {
                if (lodSetup.SO1 != null)
                {
                    AppendMeshes(lodSetup.SO1, allMeshes);
                    lod1MeshCount = allMeshes.Count - lod1MeshStart;
                }

                lod2MeshStart = allMeshes.Count;
                if (lodSetup.SO2 != null)
                {
                    AppendMeshes(lodSetup.SO2, allMeshes);
                    lod2MeshCount = allMeshes.Count - lod2MeshStart;
                }
            }

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

            // ── Snapshot blittable state BEFORE structural change ─────────────
            // RenderMeshUtility.AddComponents rebuilds the archetype and zeroes
            // all blittable components that aren't re-added by the utility itself.
            bool hadState = EntityManager.HasComponent<AnimatedMeshState>(entity);
            bool hadCmd = EntityManager.HasComponent<AnimatedMeshCommand>(entity);
            bool hadLODSt = EntityManager.HasComponent<AnimatedMeshLODState>(entity);
            bool hadLODReq = EntityManager.HasComponent<AnimatedMeshLODRebuildRequest>(entity);

            var snapState = hadState ? EntityManager.GetComponentData<AnimatedMeshState>(entity) : default;
            var snapCmd = hadCmd ? EntityManager.GetComponentData<AnimatedMeshCommand>(entity) : default;
            var snapLODSt = hadLODSt ? EntityManager.GetComponentData<AnimatedMeshLODState>(entity) : default;
            var snapLODReq = hadLODReq ? EntityManager.GetComponentData<AnimatedMeshLODRebuildRequest>(entity) : default;

            var offsetSnapshot = new List<AnimatedMeshClipOffset>();
            if (EntityManager.HasBuffer<AnimatedMeshClipOffset>(entity))
            {
                var buf = EntityManager.GetBuffer<AnimatedMeshClipOffset>(entity);
                for (int i = 0; i < buf.Length; i++) offsetSnapshot.Add(buf[i]);
            }

            // ── ONE-TIME structural change ────────────────────────────────────
            // Materials: LOD entities use LOD0 materials for the shared batch.
            // Shadow settings come from LOD setup if present, else from main setup.
            var shadowMode = isLOD ? lodSetup.ShadowMode : setupData.ShadowMode;
            var receiveShadows = isLOD ? lodSetup.ReceiveShadows : setupData.ReceiveShadows;

            // BATCHING FIX: reuse a cached RenderMeshArray for entities sharing
            // the same SO + materials. EntitiesGraphics only GPU-instances entities
            // that hold the exact same RenderMeshArray object reference — a unique
            // instance per entity means a unique draw call per entity.
            var cacheKey = new RenderMeshCacheKey(
                animData.SO,
                isLOD ? lodSetup.SO1 : null,
                isLOD ? lodSetup.SO2 : null,
                setupData.Materials);

            if (!_renderMeshArrayCache.TryGetValue(cacheKey, out var renderMeshArray))
            {
                renderMeshArray = new RenderMeshArray(setupData.Materials, allMeshes.ToArray());
                _renderMeshArrayCache[cacheKey] = renderMeshArray;
            }

            var renderMeshDesc = new RenderMeshDescription(
                shadowCastingMode: shadowMode,
                receiveShadows: receiveShadows);

            RenderMeshUtility.AddComponents(
                entity,
                EntityManager,
                renderMeshDesc,
                renderMeshArray,
                MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));

            // ── Restore AnimatedMeshState ─────────────────────────────────────
            var so = animData.SO;
            int startClip = AnimMath.Clamp(setupData.StartClipIndex, 0, so.Clips.Count - 1);
            var newState = hadState && snapState.FrameDuration > 0f
                ? snapState
                : new AnimatedMeshState
                {
                    ClipIndex = startClip,
                    FrameIndex = 0,
                    FrameAccumulator = 0f,
                    FrameDuration = so.AnimationFPS > 0 ? 1f / so.AnimationFPS : 1f / 30f,
                    IsPlaying = setupData.PlayOnStart,
                    Loop = setupData.Loop,
                };

            SetOrAdd(entity, newState);

            // ── Restore AnimatedMeshCommand ───────────────────────────────────
            var restoredCmd = hadCmd
                ? snapCmd
                : new AnimatedMeshCommand { Type = AnimatedMeshCommandType.None, ClipIndex = -1 };
            SetOrAdd(entity, restoredCmd);

            // ── Restore AnimatedMeshTag ───────────────────────────────────────
            if (!EntityManager.HasComponent<AnimatedMeshTag>(entity))
                EntityManager.AddComponent<AnimatedMeshTag>(entity);

            // ── Restore / build ClipOffset buffer ────────────────────────────
            // For LOD entities we always rebuild from scratch so the buffer
            // contains all three LODs' offsets in the correct packed layout.
            // For non-LOD entities we restore the snapshot if one existed.
            if (!EntityManager.HasBuffer<AnimatedMeshClipOffset>(entity))
                EntityManager.AddBuffer<AnimatedMeshClipOffset>(entity);

            var offsetBuf = EntityManager.GetBuffer<AnimatedMeshClipOffset>(entity);
            offsetBuf.Clear();

            if (isLOD)
            {
                // Pack all three LODs' clip offsets sequentially.
                // The mesh indices inside each LOD's section are relative to the
                // start of that LOD's mesh block in the combined RenderMeshArray.
                AppendClipOffsets(animData.SO, lod0MeshStart, offsetBuf);
                if (lodSetup.SO1 != null) AppendClipOffsets(lodSetup.SO1, lod1MeshStart, offsetBuf);
                if (lodSetup.SO2 != null) AppendClipOffsets(lodSetup.SO2, lod2MeshStart, offsetBuf);
            }
            else if (offsetSnapshot.Count > 0)
            {
                foreach (var entry in offsetSnapshot) offsetBuf.Add(entry);
            }
            else
            {
                AppendClipOffsets(so, 0, offsetBuf);
            }

            // ── Restore visibility tag ────────────────────────────────────────
            if (!EntityManager.HasComponent<AnimatedMeshVisible>(entity))
                EntityManager.AddComponent<AnimatedMeshVisible>(entity);
            EntityManager.SetComponentEnabled<AnimatedMeshVisible>(entity, true);

            // ── LOD EXTENSION ─────────────────────────────────────────────────
            if (isLOD)
            {
                // Populate AnimatedMeshLODData (managed — safe after structural change).
                AnimatedMeshLODData lodData;
                if (EntityManager.HasComponent<AnimatedMeshLODData>(entity))
                    lodData = EntityManager.GetComponentObject<AnimatedMeshLODData>(entity);
                else
                {
                    lodData = new AnimatedMeshLODData();
                    EntityManager.AddComponentObject(entity, lodData);
                }

                lodData.SO0 = animData.SO;
                lodData.Materials0 = setupData.Materials;
                lodData.SO1 = lodSetup.SO1;
                lodData.SO2 = lodSetup.SO2;
                lodData.Materials1 = lodSetup.Materials1 ?? setupData.Materials;
                lodData.Materials2 = lodSetup.Materials2 ?? lodSetup.Materials1 ?? setupData.Materials;
                lodData.ShadowMode = lodSetup.ShadowMode;
                lodData.ReceiveShadows = lodSetup.ReceiveShadows;
                lodData.BuildHashCaches();

                // Compute clip counts per LOD — needed by AnimatedMeshLODState.
                int clipCount0 = animData.SO?.Clips?.Count ?? 0;
                int clipCount1 = lodSetup.SO1?.Clips?.Count ?? 0;
                int clipCount2 = lodSetup.SO2?.Clips?.Count ?? 0;

                float fps0 = animData.SO?.AnimationFPS ?? 30;
                float fps1 = lodSetup.SO1?.AnimationFPS ?? fps0;
                float fps2 = lodSetup.SO2?.AnimationFPS ?? fps1;

                // Build the LOD state, preserving distance thresholds from the
                // snapshot if they were already configured by the baker.
                var lodState = hadLODSt ? snapLODSt : default;
                // Buffer bases are always recalculated from the actual clip counts.
                lodState.LodBufferBase0 = 0;
                lodState.LodBufferBase1 = clipCount0;
                lodState.LodBufferBase2 = clipCount0 + clipCount1;
                lodState.LodClipCount0 = clipCount0;
                lodState.LodClipCount1 = clipCount1;
                lodState.LodClipCount2 = clipCount2;
                lodState.LodFrameDuration0 = fps0 > 0 ? 1f / fps0 : 1f / 30f;
                lodState.LodFrameDuration1 = fps1 > 0 ? 1f / fps1 : 1f / 30f;
                lodState.LodFrameDuration2 = fps2 > 0 ? 1f / fps2 : 1f / 30f;
                // ActiveLOD defaults to 0 (correct for a freshly spawned entity).
                lodState.ActiveLOD = 0;

                SetOrAdd(entity, lodState);

                if (hadLODReq) SetOrAdd(entity, snapLODReq);

                // Ensure the enableable rebuild tag is present and DISABLED.
                // It will be enabled (bit flip only) by CheckLODJob when needed.
                if (!EntityManager.HasComponent<AnimatedMeshLODNeedsRebuild>(entity))
                    EntityManager.AddComponent<AnimatedMeshLODNeedsRebuild>(entity);
                EntityManager.SetComponentEnabled<AnimatedMeshLODNeedsRebuild>(entity, false);

                EntityManager.RemoveComponent<AnimatedMeshLODSetupData>(entity);
            }

            // ── Remove setup tags ─────────────────────────────────────────────
            EntityManager.RemoveComponent<AnimatedMeshNeedsRenderSetup>(entity);
            EntityManager.RemoveComponent<AnimatedMeshRenderSetupData>(entity);
        }

        entities.Dispose();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Append all non-null frames from every clip in <paramref name="so"/>.</summary>
    private static void AppendMeshes(AnimatedMeshScriptableObjectECS so, List<Mesh> list)
    {
        if (so == null) return;
        foreach (var clip in so.Clips)
            if (clip.Frames != null)
                foreach (var frame in clip.Frames)
                    if (frame != null) list.Add(frame);
    }

    /// <summary>
    /// Append one <see cref="AnimatedMeshClipOffset"/> per clip in <paramref name="so"/>.
    /// <paramref name="meshArrayBase"/> is the index in the combined RenderMeshArray where
    /// this SO's meshes begin, so FrameStart values are absolute indices into that array.
    /// </summary>
    private static void AppendClipOffsets(
        AnimatedMeshScriptableObjectECS so,
        int meshArrayBase,
        DynamicBuffer<AnimatedMeshClipOffset> buf)
    {
        if (so == null) return;
        int cursor = meshArrayBase;
        foreach (var clip in so.Clips)
        {
            int count = clip.Frames?.Count ?? 0;
            buf.Add(new AnimatedMeshClipOffset { FrameStart = cursor, FrameCount = count });
            cursor += count;
        }
    }

    /// <summary>SetComponentData if present, AddComponentData if not.</summary>
    private void SetOrAdd<T>(Entity e, T value) where T : unmanaged, IComponentData
    {
        if (EntityManager.HasComponent<T>(e))
            EntityManager.SetComponentData(e, value);
        else
            EntityManager.AddComponentData(e, value);
    }
}