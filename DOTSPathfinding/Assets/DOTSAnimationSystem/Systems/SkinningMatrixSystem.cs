using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Deformations;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Rendering;

namespace Shek.ECSAnimation
{
    // =========================================================================
    // BoneIndexCachingSystem
    //
    // Resolves mesh-bone -> anim-bone index mappings by name and writes them
    // into BoneIndexCache on each SMR entity.
    //
    // Key changes vs original:
    //   • Fully [BurstCompile] — possible because bone names are now
    //     FixedString64Bytes (not BlobString), so no managed ToString() needed.
    //   • Scheduled as a PARALLEL job (IJobParallelForDefer over unresolved
    //     entities collected into a NativeList each frame).
    //   • No state.Enabled = false — RequireForUpdate gates the system when no
    //     SMR entities exist; new spawns are picked up automatically.
    // =========================================================================
    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(SkinningMatrixSystem))]
    public partial struct BoneIndexCachingSystem : ISystem
    {
        // Entity + component arrays collected on the main thread, then handed
        // to the parallel job. Persistent to avoid per-frame alloc.
        private NativeList<Entity> _unresolvedEntities;
        private NativeList<SkinnedMeshBones> _unresolvedBones;
        private NativeList<AnimationSource> _unresolvedSources;

        // Per-entity output written by the parallel job and applied on the main thread.
        private NativeArray<BlobAssetReference<BoneIndexCacheBlob>> _resolvedBlobs;

        // Shared lookup: sourceEntity -> blob reference (library or clip).
        // Built once per frame on the main thread before launching the job.
        private NativeParallelHashMap<Entity, BlobAssetReference<AnimationLibraryBlob>> _libMap;
        private NativeParallelHashMap<Entity, BlobAssetReference<AnimationClipBlob>> _clipMap;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _unresolvedEntities = new NativeList<Entity>(64, Allocator.Persistent);
            _unresolvedBones = new NativeList<SkinnedMeshBones>(64, Allocator.Persistent);
            _unresolvedSources = new NativeList<AnimationSource>(64, Allocator.Persistent);
            _libMap = new NativeParallelHashMap<Entity, BlobAssetReference<AnimationLibraryBlob>>(64, Allocator.Persistent);
            _clipMap = new NativeParallelHashMap<Entity, BlobAssetReference<AnimationClipBlob>>(64, Allocator.Persistent);

            state.RequireForUpdate<SkinnedMeshBones>();
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            if (_unresolvedEntities.IsCreated) _unresolvedEntities.Dispose();
            if (_unresolvedBones.IsCreated) _unresolvedBones.Dispose();
            if (_unresolvedSources.IsCreated) _unresolvedSources.Dispose();
            if (_libMap.IsCreated) _libMap.Dispose();
            if (_clipMap.IsCreated) _clipMap.Dispose();
            if (_resolvedBlobs.IsCreated) _resolvedBlobs.Dispose();
        }

        // OnUpdate runs on the main thread to collect unresolved entities and
        // build the source lookup, then schedules the Burst parallel job.
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var em = state.EntityManager;

            _unresolvedEntities.Clear();
            _unresolvedBones.Clear();
            _unresolvedSources.Clear();
            _libMap.Clear();
            _clipMap.Clear();

            // -- Collect unresolved SMR entities ---------------------------------
            foreach (var (bonesRef, animSource, cache, entity) in
                SystemAPI.Query<
                    RefRO<SkinnedMeshBones>,
                    RefRO<AnimationSource>,
                    RefRO<BoneIndexCache>>()
                    .WithEntityAccess())
            {
                if (cache.ValueRO.IsResolved) continue;
                if (!bonesRef.ValueRO.Value.IsCreated) continue;

                _unresolvedEntities.Add(entity);
                _unresolvedBones.Add(bonesRef.ValueRO);
                _unresolvedSources.Add(animSource.ValueRO);

                // Build source lookup so the parallel job can read it without EM access.
                var src = animSource.ValueRO.Value;
                if (!_libMap.ContainsKey(src) && !_clipMap.ContainsKey(src))
                {
                    if (em.HasComponent<AnimationLibraryReference>(src))
                    {
                        var libRef = em.GetComponentData<AnimationLibraryReference>(src);
                        if (libRef.Value.IsCreated)
                            _libMap.TryAdd(src, libRef.Value);
                    }
                    else if (em.HasComponent<AnimationClipReference>(src))
                    {
                        var clipRef = em.GetComponentData<AnimationClipReference>(src);
                        if (clipRef.Value.IsCreated)
                            _clipMap.TryAdd(src, clipRef.Value);
                    }
                }
            }

            int count = _unresolvedEntities.Length;
            if (count == 0) return;

            // Allocate output array (resize only when count grows).
            if (!_resolvedBlobs.IsCreated || _resolvedBlobs.Length < count)
            {
                if (_resolvedBlobs.IsCreated) _resolvedBlobs.Dispose();
                _resolvedBlobs = new NativeArray<BlobAssetReference<BoneIndexCacheBlob>>(
                    count, Allocator.TempJob);
            }

            // -- Schedule parallel Burst job -------------------------------------
            var job = new ResolveBoneIndicesJob
            {
                Bones = _unresolvedBones.AsArray(),
                Sources = _unresolvedSources.AsArray(),
                LibMap = _libMap,
                ClipMap = _clipMap,
                Output = _resolvedBlobs
            };
            // Complete synchronously — resolution only happens for new spawns (rare),
            // so the cost is amortised. The job is parallel over all unresolved entities.
            state.Dependency = job.Schedule(count, 4, state.Dependency);
            state.Dependency.Complete();

            // -- Apply results back to components (main thread, no structural change)
            for (int i = 0; i < count; i++)
            {
                if (!_resolvedBlobs[i].IsCreated) continue;
                var entity = _unresolvedEntities[i];
                var cache = em.GetComponentData<BoneIndexCache>(entity);
                cache.Value = _resolvedBlobs[i];
                cache.IsResolved = true;
                em.SetComponentData(entity, cache);
            }
        }
    }

    /// <summary>
    /// Burst-compiled parallel job: for each unresolved SMR entity, builds a
    /// BoneIndexCacheBlob by matching mesh bone names to anim bone names.
    /// All string comparisons use FixedString64Bytes — zero managed allocations.
    /// </summary>
    [BurstCompile]
    struct ResolveBoneIndicesJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<SkinnedMeshBones> Bones;
        [ReadOnly] public NativeArray<AnimationSource> Sources;
        [ReadOnly] public NativeParallelHashMap<Entity, BlobAssetReference<AnimationLibraryBlob>> LibMap;
        [ReadOnly] public NativeParallelHashMap<Entity, BlobAssetReference<AnimationClipBlob>> ClipMap;

        [NativeDisableParallelForRestriction]
        public NativeArray<BlobAssetReference<BoneIndexCacheBlob>> Output;

        public void Execute(int index)
        {
            var src = Sources[index].Value;
            ref var bonesBlob = ref Bones[index].Value.Value;
            int meshBoneCount = bonesBlob.BoneNames.Length;

            // Build name -> index map for anim bones, then look up each mesh bone.
            if (LibMap.TryGetValue(src, out var libRef))
            {
                ref var lib = ref libRef.Value;
                using var map = new NativeHashMap<FixedString64Bytes, int>(lib.BoneCount, Allocator.Temp);
                for (int ab = 0; ab < lib.BoneCount; ab++)
                    map.TryAdd(lib.BoneNames[ab], ab);
                Output[index] = BuildBlob(ref bonesBlob.BoneNames, meshBoneCount, map);
            }
            else if (ClipMap.TryGetValue(src, out var clipRef))
            {
                ref var clip = ref clipRef.Value;
                using var map = new NativeHashMap<FixedString64Bytes, int>(clip.BoneCount, Allocator.Temp);
                for (int ab = 0; ab < clip.BoneCount; ab++)
                {
                    // BoneNames is now FixedString64Bytes — no managed ToString() needed, fully Burst-safe.
                    map.TryAdd(clip.BoneNames[ab], ab);
                }
                Output[index] = BuildBlob(ref bonesBlob.BoneNames, meshBoneCount, map);
            }
            // else: source not ready — Output[index] stays default (IsCreated = false).
        }

        static BlobAssetReference<BoneIndexCacheBlob> BuildBlob(
            ref BlobArray<FixedString64Bytes> meshBoneNames,
            int meshBoneCount,
            NativeHashMap<FixedString64Bytes, int> animBoneMap)
        {
            var builder = new BlobBuilder(Allocator.Temp);
            ref var root = ref builder.ConstructRoot<BoneIndexCacheBlob>();
            var idxArr = builder.Allocate(ref root.MeshToAnimBoneIndex, meshBoneCount);

            for (int mb = 0; mb < meshBoneCount; mb++)
                idxArr[mb] = animBoneMap.TryGetValue(meshBoneNames[mb], out int ai) ? ai : -1;

            var result = builder.CreateBlobAssetReference<BoneIndexCacheBlob>(Allocator.Persistent);
            builder.Dispose();
            return result;
        }
    }

    // =========================================================================
    // SkinningMatrixSystem
    //
    // Computes GPU skinning matrices every frame.
    // skinMatrix[meshBone] = boneMatrix(animBone) * bindPose[meshBone]
    //
    // BoneTransformBuffer now stores pre-built float4x4 matrices (written by
    // AnimationSamplingSystem), so this job only needs one math.mul per bone.
    // =========================================================================
    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(AnimationSamplingSystem))]
    public partial struct SkinningMatrixSystem : ISystem
    {
        private BufferLookup<BoneTransformBuffer> _boneTransformLookup;
        private EntityQuery _skinningQuery;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _boneTransformLookup = state.GetBufferLookup<BoneTransformBuffer>(true);

            _skinningQuery = SystemAPI.QueryBuilder()
                .WithAll<RequiresSkinning, SkinnedMeshBones, AnimationSource, BoneIndexCache>()
                .WithAll<AnimationActive>()
                .Build();

            state.RequireForUpdate(_skinningQuery);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            _boneTransformLookup.Update(ref state);

            var job = new ComputeSkinMatricesJob
            {
                BonesType = SystemAPI.GetComponentTypeHandle<SkinnedMeshBones>(true),
                AnimSourceType = SystemAPI.GetComponentTypeHandle<AnimationSource>(true),
                BoneIndexCacheType = SystemAPI.GetComponentTypeHandle<BoneIndexCache>(true),
                SkinMatrixType = SystemAPI.GetBufferTypeHandle<SkinMatrix>(false),
                BoneTransformLookup = _boneTransformLookup,
                AnimActiveType = SystemAPI.GetComponentTypeHandle<AnimationActive>(true),
                CulledType = SystemAPI.GetComponentTypeHandle<AnimationCulled>(true),
            };
            state.Dependency = job.ScheduleParallel(_skinningQuery, state.Dependency);
        }
    }

    [BurstCompile]
    struct ComputeSkinMatricesJob : IJobChunk
    {
        [ReadOnly] public ComponentTypeHandle<SkinnedMeshBones> BonesType;
        [ReadOnly] public ComponentTypeHandle<AnimationSource> AnimSourceType;
        [ReadOnly] public ComponentTypeHandle<BoneIndexCache> BoneIndexCacheType;
        public BufferTypeHandle<SkinMatrix> SkinMatrixType;
        [ReadOnly] public ComponentTypeHandle<AnimationActive> AnimActiveType;
        [ReadOnly] public ComponentTypeHandle<AnimationCulled> CulledType;
        [ReadOnly] public BufferLookup<BoneTransformBuffer> BoneTransformLookup;

        static readonly float3x4 k_Identity = new float3x4(
            new float3(1, 0, 0),
            new float3(0, 1, 0),
            new float3(0, 0, 1),
            new float3(0, 0, 0));

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex,
            bool useEnabledMask, in v128 chunkEnabledMask)
        {
            var bonesArr = chunk.GetNativeArray(ref BonesType);
            var animSrcArr = chunk.GetNativeArray(ref AnimSourceType);
            var cacheArr = chunk.GetNativeArray(ref BoneIndexCacheType);
            var skinMatrixAcc = chunk.GetBufferAccessor(ref SkinMatrixType);
            bool hasCulled = chunk.Has(ref CulledType);

            for (int i = 0; i < chunk.Count; i++)
            {
                if (!chunk.IsComponentEnabled(ref AnimActiveType, i)) continue;
                // If the animation source is culled, skip skinning too.
                if (hasCulled && chunk.IsComponentEnabled(ref CulledType, i)) continue;

                var cacheRef = cacheArr[i];
                if (!cacheRef.IsResolved) continue;
                if (!cacheRef.Value.IsCreated) continue;

                var bonesRef = bonesArr[i];
                if (!bonesRef.Value.IsCreated) continue;

                var sourceEnt = animSrcArr[i].Value;
                if (!BoneTransformLookup.TryGetBuffer(sourceEnt, out var boneTransforms)) continue;
                if (boneTransforms.Length == 0) continue;

                var skinMatrices = skinMatrixAcc[i];
                ref var bonesBlob = ref bonesRef.Value.Value;
                ref var cache = ref cacheRef.Value.Value;
                int meshBoneCount = bonesBlob.BindPoses.Length;

                for (int mb = 0; mb < meshBoneCount; mb++)
                {
                    int animIdx = cache.MeshToAnimBoneIndex[mb];

                    if (animIdx < 0 || animIdx >= boneTransforms.Length)
                    {
                        skinMatrices[mb] = new SkinMatrix { Value = k_Identity };
                        continue;
                    }

                    // One math.mul — BoneTransformBuffer is already a float4x4.
                    float4x4 skinMat = math.mul(boneTransforms[animIdx].Matrix, bonesBlob.BindPoses[mb]);

                    skinMatrices[mb] = new SkinMatrix
                    {
                        Value = new float3x4(
                            skinMat.c0.xyz,
                            skinMat.c1.xyz,
                            skinMat.c2.xyz,
                            skinMat.c3.xyz)
                    };
                }
            }
        }
    }
}