using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Shek.ECSAnimation
{
    // =========================================================================
    // GPU Skinning Components
    //
    // These components replace SkinnedMeshRenderer-based skinning entirely.
    // The rendering pipeline becomes:
    //   AnimationController (existing) 
    //     -> GPUSkinningSystem reads Time+ClipIndex
    //     -> Maps to texture row(s)
    //     -> Graphics.DrawMeshInstanced with per-instance _AnimFrameA/B/Blend
    //     -> Shader samples animation texture, reconstructs bone matrices
    //     -> Vertices skinned in vertex shader on GPU
    //     -> Zero SkinningDeformationDispatch, one draw call for all instances
    // =========================================================================

    /// <summary>
    /// Marks an entity as using GPU texture-based skinning instead of
    /// Unity's SkinnedMeshRenderer deformation pipeline.
    ///
    /// The entity still has AnimationController + AnimationLibraryReference
    /// for animation state. GPUSkinningSystem reads those and drives rendering.
    /// </summary>
    public struct GPUSkinningTag : IComponentData { }

    /// <summary>
    /// Per-entity GPU skinning data baked at authoring time.
    /// Points to the shared animation texture and the mesh to render.
    /// </summary>
    public struct GPUSkinningData : IComponentData
    {
        /// <summary>
        /// Index into the GPUSkinningRenderSystem's shared renderer list.
        /// All entities with the same character type share one renderer entry
        /// (same texture, same material, same mesh) — enabling one draw call.
        /// </summary>
        public int RendererIndex;
    }

    /// <summary>
    /// Per-clip frame offset data baked into a blob so GPUSkinningSystem
    /// can convert AnimationController.Time -> texture row without a managed lookup.
    ///
    /// Layout mirrors AnimationLibraryBlob.Clips but only stores the data
    /// needed for texture row calculation.
    /// </summary>
    public struct GPUClipInfo
    {
        /// <summary>First row in the animation texture for this clip.</summary>
        public int TextureRowOffset;
        /// <summary>Number of rows (frames) baked for this clip.</summary>
        public int FrameCount;
        public float Duration;
        public float FrameRate;
        public bool IsLooping;
    }

    public struct GPUSkinningLibraryBlob
    {
        public BlobArray<GPUClipInfo> Clips;
        public int BoneCount;
        public int TotalFrames;
    }

    /// <summary>
    /// Reference to the per-character-type GPU skinning blob.
    /// Shared (same blob asset reference) across all entities of the same type.
    /// </summary>
    public struct GPUSkinningLibraryReference : IComponentData
    {
        public BlobAssetReference<GPUSkinningLibraryBlob> Value;
    }
}