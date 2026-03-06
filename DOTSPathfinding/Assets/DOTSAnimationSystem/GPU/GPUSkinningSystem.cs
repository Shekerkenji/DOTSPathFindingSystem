using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;

namespace Shek.ECSAnimation
{
    /// <summary>
    /// GPU Skinning Render System — Compute Shader Architecture.
    ///
    /// Each frame:
    ///   1. Gather animation frame data + world matrices from ECS (Burst parallel job)
    ///   2. Upload to GPU via ComputeBuffers (SetData)
    ///   3. Dispatch compute shader: skins ALL instances in one compute dispatch
    ///      (threads = vertexCount × instanceCount, runs entirely on GPU)
    ///   4. DrawMeshInstancedProcedural reads pre-skinned vertices — zero vertex
    ///      shader texture sampling, cost equivalent to drawing a static mesh.
    ///
    /// GPU work per frame:
    ///   Compute:  instances × verts × 16 tex reads  (once, not per-pass)
    ///   Render:   instances × verts × 0 tex reads   (per pass, very cheap)
    /// </summary>
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    public partial class GPUSkinningRenderSystem : SystemBase
    {
        const int k_AnimStride = sizeof(float) * 4;   // FrameA,FrameB,Blend,Pad
        const int k_MatrixStride = sizeof(float) * 16;  // float4x4
        // SkinnedVertex: float3 pos + float pad + float3 norm + float pad = 32 bytes
        const int k_SkinnedVertexStride = sizeof(float) * 8;

        struct RendererEntry
        {
            public Mesh Mesh;
            public Material Material;
            public ComputeShader SkinCompute;
            public int KernelIndex;
            public int VertexCount;
            public int Capacity;       // current instance capacity
            public ShadowCastingMode ShadowCasting;
            public bool ReceiveShadows;
            public int Layer;

            // GPU buffers
            public ComputeBuffer AnimBuffer;        // _InstanceAnimData
            public ComputeBuffer MatrixBuffer;      // _InstanceMatrices
            public GraphicsBuffer SkinnedVerts;     // _SkinnedVertices (output of compute, input of shader)

            // Source mesh GPU buffers (static, uploaded once)
            public ComputeBuffer SrcPositions;
            public ComputeBuffer SrcNormals;
            public ComputeBuffer BoneWeights;
            public ComputeBuffer BoneIndices;
        }

        System.Collections.Generic.List<RendererEntry> _renderers;
        NativeList<float> _frameA;
        NativeList<float> _frameB;
        NativeList<float> _blends;
        NativeList<int> _rendererIndices;
        NativeList<float4x4> _matrices;

        float[] _animUpload;
        Matrix4x4[] _matUpload;

        ComputeShader _computeShader;

        protected override void OnCreate()
        {
            _renderers = new System.Collections.Generic.List<RendererEntry>();
            _frameA = new NativeList<float>(256, Allocator.Persistent);
            _frameB = new NativeList<float>(256, Allocator.Persistent);
            _blends = new NativeList<float>(256, Allocator.Persistent);
            _rendererIndices = new NativeList<int>(256, Allocator.Persistent);
            _matrices = new NativeList<float4x4>(256, Allocator.Persistent);
            _animUpload = new float[256 * 4];
            _matUpload = new Matrix4x4[256];

            _computeShader = Resources.Load<ComputeShader>("GPUSkinning");
            if (_computeShader == null)
                Debug.LogError("[GPUSkinningRenderSystem] Could not load GPUSkinning.compute from Resources folder. " +
                               "Place GPUSkinning.compute inside a Resources folder.");

            RequireForUpdate<GPUSkinningTag>();
        }

        protected override void OnDestroy()
        {
            if (_frameA.IsCreated) _frameA.Dispose();
            if (_frameB.IsCreated) _frameB.Dispose();
            if (_blends.IsCreated) _blends.Dispose();
            if (_rendererIndices.IsCreated) _rendererIndices.Dispose();
            if (_matrices.IsCreated) _matrices.Dispose();

            foreach (var r in _renderers) ReleaseEntry(r);
        }

        static void ReleaseEntry(RendererEntry r)
        {
            r.AnimBuffer?.Release();
            r.MatrixBuffer?.Release();
            r.SkinnedVerts?.Release();
            r.SrcPositions?.Release();
            r.SrcNormals?.Release();
            r.BoneWeights?.Release();
            r.BoneIndices?.Release();
        }

        protected override void OnUpdate()
        {
            if (_computeShader == null) return;

            // ── 1. Register new renderer types ────────────────────────────────
            foreach (var (gpuData, managed, libRef) in
                SystemAPI.Query<RefRW<GPUSkinningData>, GPUSkinningManagedData, RefRO<GPUSkinningLibraryReference>>())
            {
                if (gpuData.ValueRO.RendererIndex >= 0) continue;

                int idx = -1;
                for (int i = 0; i < _renderers.Count; i++)
                    if (_renderers[i].Material == managed.Material) { idx = i; break; }

                if (idx < 0)
                {
                    idx = RegisterNewRenderer(managed, libRef.ValueRO);
                }
                gpuData.ValueRW.RendererIndex = idx;
            }

            // ── 2. Count entities ─────────────────────────────────────────────
            int total = SystemAPI.QueryBuilder()
                .WithAll<GPUSkinningTag, AnimationController, GPUSkinningLibraryReference, LocalToWorld>()
                .Build().CalculateEntityCount();
            if (total == 0) return;

            _frameA.Resize(total, NativeArrayOptions.UninitializedMemory);
            _frameB.Resize(total, NativeArrayOptions.UninitializedMemory);
            _blends.Resize(total, NativeArrayOptions.UninitializedMemory);
            _rendererIndices.Resize(total, NativeArrayOptions.UninitializedMemory);
            _matrices.Resize(total, NativeArrayOptions.UninitializedMemory);

            // ── 3. Burst gather ───────────────────────────────────────────────
            Dependency = new GatherJob
            {
                FrameA = _frameA.AsArray(),
                FrameB = _frameB.AsArray(),
                Blends = _blends.AsArray(),
                RendererIndices = _rendererIndices.AsArray(),
                Matrices = _matrices.AsArray()
            }.ScheduleParallel(Dependency);
            Dependency.Complete();

            // ── 4. Per-renderer: upload → dispatch compute → draw ─────────────
            for (int r = 0; r < _renderers.Count; r++)
            {
                var entry = _renderers[r];

                // Count instances for this renderer
                int count = 0;
                for (int i = 0; i < total; i++)
                    if (_rendererIndices[i] == r) count++;
                if (count == 0) continue;

                // Grow buffers if needed
                if (count > entry.Capacity)
                {
                    entry.AnimBuffer?.Release();
                    entry.MatrixBuffer?.Release();
                    entry.SkinnedVerts?.Release();

                    entry.Capacity = Mathf.NextPowerOfTwo(count);
                    entry.AnimBuffer = new ComputeBuffer(entry.Capacity, k_AnimStride, ComputeBufferType.Structured);
                    entry.MatrixBuffer = new ComputeBuffer(entry.Capacity, k_MatrixStride, ComputeBufferType.Structured);
                    entry.SkinnedVerts = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                        entry.Capacity * entry.VertexCount, k_SkinnedVertexStride);

                    // Re-bind to material
                    entry.Material.SetBuffer("_SkinnedVertices", entry.SkinnedVerts);
                    entry.Material.SetInt("_VertexCount", entry.VertexCount);
                    _renderers[r] = entry;
                }

                // Fill upload arrays
                if (_animUpload.Length < count * 4) _animUpload = new float[Mathf.NextPowerOfTwo(count) * 4];
                if (_matUpload.Length < count) _matUpload = new Matrix4x4[Mathf.NextPowerOfTwo(count)];

                int w = 0;
                for (int i = 0; i < total; i++)
                {
                    if (_rendererIndices[i] != r) continue;
                    int f = w * 4;
                    _animUpload[f] = _frameA[i];
                    _animUpload[f + 1] = _frameB[i];
                    _animUpload[f + 2] = _blends[i];
                    _animUpload[f + 3] = 0f;
                    _matUpload[w] = _matrices[i];
                    w++;
                }

                entry.AnimBuffer.SetData(_animUpload, 0, 0, count * 4);
                entry.MatrixBuffer.SetData(_matUpload, 0, 0, count);

                // ── Dispatch compute shader ───────────────────────────────────
                int kernel = entry.KernelIndex;
                _computeShader.SetTexture(kernel, "_AnimTex", entry.Material.GetTexture("_AnimTex") as Texture2D);
                _computeShader.SetBuffer(kernel, "_InstanceAnimData", entry.AnimBuffer);
                _computeShader.SetBuffer(kernel, "_InstanceMatrices", entry.MatrixBuffer);
                _computeShader.SetBuffer(kernel, "_SrcPositions", entry.SrcPositions);
                _computeShader.SetBuffer(kernel, "_SrcNormals", entry.SrcNormals);
                _computeShader.SetBuffer(kernel, "_BoneWeights", entry.BoneWeights);
                _computeShader.SetBuffer(kernel, "_BoneIndices", entry.BoneIndices);
                _computeShader.SetBuffer(kernel, "_SkinnedVertices", entry.SkinnedVerts);
                _computeShader.SetInt("_VertexCount", entry.VertexCount);

                // Texel size: float4(1/w, 1/h, w, h)
                var animTex = entry.Material.GetTexture("_AnimTex") as Texture2D;
                _computeShader.SetVector("_AnimTex_TexelSize",
                    new Vector4(1f / animTex.width, 1f / animTex.height, animTex.width, animTex.height));

                // groups: ceil(verts/64) × instanceCount × 1
                int groupsX = Mathf.CeilToInt(entry.VertexCount / 64f);
                _computeShader.Dispatch(kernel, groupsX, count, 1);

                // ── Draw — vertex shader just reads from _SkinnedVertices ─────
                Graphics.DrawMeshInstancedProcedural(
                    entry.Mesh, 0, entry.Material,
                    new Bounds(Vector3.zero, Vector3.one * 10000f),
                    count, null,
                    entry.ShadowCasting, entry.ReceiveShadows, entry.Layer);
            }
        }

        int RegisterNewRenderer(GPUSkinningManagedData managed, GPUSkinningLibraryReference libRef)
        {
            ref var lib = ref libRef.Value.Value;
            int kernel = _computeShader.FindKernel("SkinMeshes");
            int initCap = 64;
            int verts = managed.BakedMesh.vertexCount;

            // Build source mesh GPU buffers (uploaded once, never change)
            var srcVerts = managed.BakedMesh.vertices;
            var srcNormals = managed.BakedMesh.normals;
            var uvBoneIdx01 = managed.BakedMesh.uv2; // bone indices 0,1
            var uvBoneIdx23 = managed.BakedMesh.uv4; // bone indices 2,3
            var uvBoneWgt01 = managed.BakedMesh.uv3; // bone weights 0,1
            var uvBoneWgt23 = managed.BakedMesh.uv5; // bone weights 2,3

            var posBuf = new ComputeBuffer(verts, sizeof(float) * 3);
            var normBuf = new ComputeBuffer(verts, sizeof(float) * 3);
            var wgtBuf = new ComputeBuffer(verts, sizeof(float) * 4);
            var idxBuf = new ComputeBuffer(verts, sizeof(uint) * 4);

            posBuf.SetData(srcVerts);
            normBuf.SetData(srcNormals);

            var wgts = new Vector4[verts];
            var idxs = new int[] { };
            var idxArr = new uint[verts * 4];
            var wgtArr = new float[verts * 4];

            for (int v = 0; v < verts; v++)
            {
                wgtArr[v * 4 + 0] = uvBoneWgt01[v].x;
                wgtArr[v * 4 + 1] = uvBoneWgt01[v].y;
                wgtArr[v * 4 + 2] = uvBoneWgt23[v].x;
                wgtArr[v * 4 + 3] = uvBoneWgt23[v].y;

                idxArr[v * 4 + 0] = (uint)Mathf.RoundToInt(uvBoneIdx01[v].x);
                idxArr[v * 4 + 1] = (uint)Mathf.RoundToInt(uvBoneIdx01[v].y);
                idxArr[v * 4 + 2] = (uint)Mathf.RoundToInt(uvBoneIdx23[v].x);
                idxArr[v * 4 + 3] = (uint)Mathf.RoundToInt(uvBoneIdx23[v].y);
            }

            wgtBuf.SetData(wgtArr);
            idxBuf.SetData(idxArr);

            var animBuf = new ComputeBuffer(initCap, k_AnimStride, ComputeBufferType.Structured);
            var matBuf = new ComputeBuffer(initCap, k_MatrixStride, ComputeBufferType.Structured);
            var skinBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                initCap * verts, k_SkinnedVertexStride);

            // Configure material — only needs the skinned vertex buffer now, no AnimTex in shader
            var animTex = managed.AnimationTexture;
            managed.Material.SetBuffer("_SkinnedVertices", skinBuf);
            managed.Material.SetInt("_VertexCount", verts);

            var entry = new RendererEntry
            {
                Mesh = managed.BakedMesh,
                Material = managed.Material,
                SkinCompute = _computeShader,
                KernelIndex = kernel,
                VertexCount = verts,
                Capacity = initCap,
                ShadowCasting = managed.ShadowCasting,
                ReceiveShadows = managed.ReceiveShadows,
                Layer = managed.Layer,
                AnimBuffer = animBuf,
                MatrixBuffer = matBuf,
                SkinnedVerts = skinBuf,
                SrcPositions = posBuf,
                SrcNormals = normBuf,
                BoneWeights = wgtBuf,
                BoneIndices = idxBuf,
            };

            // Store animTex reference on material so we can retrieve it in OnUpdate
            managed.Material.SetTexture("_AnimTexInternal", animTex);

            int idx = _renderers.Count;
            _renderers.Add(entry);
            return idx;
        }

        [BurstCompile]
        partial struct GatherJob : IJobEntity
        {
            [NativeDisableParallelForRestriction] public NativeArray<float> FrameA;
            [NativeDisableParallelForRestriction] public NativeArray<float> FrameB;
            [NativeDisableParallelForRestriction] public NativeArray<float> Blends;
            [NativeDisableParallelForRestriction] public NativeArray<int> RendererIndices;
            [NativeDisableParallelForRestriction] public NativeArray<float4x4> Matrices;

            void Execute([EntityIndexInQuery] int index,
                in AnimationController ctrl, in GPUSkinningLibraryReference libRef,
                in GPUSkinningData gpuData, in LocalToWorld ltw)
            {
                if (!libRef.Value.IsCreated || gpuData.RendererIndex < 0)
                {
                    FrameA[index] = 0; FrameB[index] = 0; Blends[index] = 0;
                    RendererIndices[index] = -1;
                    Matrices[index] = float4x4.identity;
                    return;
                }

                ref var lib = ref libRef.Value.Value;
                if (ctrl.ClipIndex >= lib.Clips.Length)
                {
                    FrameA[index] = 0; FrameB[index] = 0; Blends[index] = 0;
                    RendererIndices[index] = gpuData.RendererIndex;
                    Matrices[index] = ltw.Value;
                    return;
                }

                ref var clip = ref lib.Clips[ctrl.ClipIndex];
                float frameF = ctrl.Time * clip.FrameRate;
                float frameFA = math.floor(frameF);
                float frameFB = math.min(frameFA + 1f, clip.FrameCount - 1);
                float blend = frameF - frameFA;

                float rowA = clip.TextureRowOffset + math.clamp(frameFA, 0, clip.FrameCount - 1);
                float rowB = clip.TextureRowOffset + frameFB;

                if (ctrl.IsTransitioning && ctrl.NextClipIndex < lib.Clips.Length)
                {
                    ref var nc = ref lib.Clips[ctrl.NextClipIndex];
                    float nRow = nc.TextureRowOffset + math.clamp(math.floor(ctrl.NextClipTime * nc.FrameRate), 0, nc.FrameCount - 1);
                    float alpha = math.saturate(ctrl.TransitionTime / ctrl.TransitionDuration);
                    rowA = math.lerp(rowA, nRow, alpha);
                    blend = math.lerp(blend, 0f, alpha);
                }

                FrameA[index] = rowA;
                FrameB[index] = rowB;
                Blends[index] = blend;
                RendererIndices[index] = gpuData.RendererIndex;
                Matrices[index] = ltw.Value;
            }
        }
    }
}