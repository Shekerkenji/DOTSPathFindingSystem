#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Shek.ECSAnimation
{
    /// <summary>
    /// Bakes animation clips into a GPU skinning texture.
    ///
    /// PERFORMANCE: The texture stores (animBoneMatrix * bindPose) pre-multiplied.
    /// This means the vertex shader only needs ONE matrix sample per bone influence
    /// instead of two (anim + bind separately), cutting vertex texture fetches in half.
    ///
    /// Texture layout
    /// ──────────────
    ///   Width  = boneCount * 4  (4 pixels per bone = one 4x4 matrix, row-major)
    ///   Height = totalFrames    (one row per sampled frame, all clips concatenated)
    ///   Format = RGBAFloat
    ///
    ///   pixel(b*4 + col, row) = row `col` of the skin matrix for bone `b` at frame `row`
    ///   skinMatrix = animBoneMatrix * bindPose  (pre-multiplied at bake time)
    ///
    /// Mesh UVs
    /// ────────
    ///   UV2 = bone indices 0,1  |  UV3 = bone weights 0,1
    ///   UV4 = bone indices 2,3  |  UV5 = bone weights 2,3
    /// </summary>
    public static class AnimationTextureBaker
    {
        [MenuItem("Tools/DOTS Animation/Bake GPU Skinning Texture")]
        public static void BakeSelected()
        {
            var go = Selection.activeGameObject;
            if (go == null) { EditorUtility.DisplayDialog("GPU Skinning Baker", "Select a GameObject with AnimationLibraryAuthoring.", "OK"); return; }

            var authoring = go.GetComponentInChildren<AnimationLibraryAuthoring>();
            if (authoring == null) { EditorUtility.DisplayDialog("GPU Skinning Baker", "No AnimationLibraryAuthoring found.", "OK"); return; }

            var smr = go.GetComponentInChildren<SkinnedMeshRenderer>();
            if (smr == null) { EditorUtility.DisplayDialog("GPU Skinning Baker", "No SkinnedMeshRenderer found.", "OK"); return; }

            string folder = EditorUtility.SaveFolderPanel("Save GPU Skinning Assets", "Assets", "GPUSkinning");
            if (string.IsNullOrEmpty(folder)) return;
            if (folder.StartsWith(Application.dataPath))
                folder = "Assets" + folder.Substring(Application.dataPath.Length);

            BakeToFolder(authoring, smr, folder);
        }

        public static (Texture2D animTex, Mesh bakedMesh, GPUSkinningLibraryAsset libAsset)
            BakeToFolder(AnimationLibraryAuthoring authoring, SkinnedMeshRenderer smr, string assetFolder)
        {
            var animRoot = authoring.animationRoot != null ? authoring.animationRoot : authoring.gameObject;
            var boneRootGO = authoring.boneRoot;
            if (boneRootGO == null)
            {
                foreach (Transform child in animRoot.transform)
                    if (child.GetComponent<SkinnedMeshRenderer>() == null) { boneRootGO = child.gameObject; break; }
            }
            if (boneRootGO == null) { Debug.LogError("[AnimationTextureBaker] Cannot find bone root."); return default; }

            // ── Collect skeleton ──────────────────────────────────────────────
            var bones = CollectBones(boneRootGO.transform);
            int boneCount = bones.Count;
            var boneToIndex = new Dictionary<Transform, int>(boneCount);
            for (int i = 0; i < boneCount; i++) boneToIndex[bones[i]] = i;

            // ── Map SMR bind poses to skeleton bone indices ────────────────────
            // bindPoses[i] = inverse(smrBone[i].localToWorld) at bind time
            var smrBones = smr.bones;
            var bindPoses = smr.sharedMesh.bindposes;
            var bindPoseForSkelBone = new Matrix4x4[boneCount];
            for (int i = 0; i < boneCount; i++) bindPoseForSkelBone[i] = Matrix4x4.identity;
            for (int s = 0; s < smrBones.Length; s++)
                if (smrBones[s] != null && boneToIndex.TryGetValue(smrBones[s], out int idx))
                    bindPoseForSkelBone[idx] = bindPoses[s];

            // ── Count frames ──────────────────────────────────────────────────
            float sampleRate = authoring.sampleRate;
            var clips = authoring.clips;
            int clipCount = clips.Count;
            var frameCounts = new int[clipCount];
            int totalFrames = 0;
            for (int c = 0; c < clipCount; c++)
            {
                if (clips[c] == null) continue;
                frameCounts[c] = Mathf.Max(1, Mathf.CeilToInt(clips[c].length * sampleRate) + 1);
                totalFrames += frameCounts[c];
            }
            if (totalFrames == 0) { Debug.LogError("[AnimationTextureBaker] No frames."); return default; }

            // ── Create texture: width = boneCount*4, height = totalFrames ─────
            int texWidth = boneCount * 4;
            int texHeight = totalFrames;
            var tex = new Texture2D(texWidth, texHeight, TextureFormat.RGBAFloat, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            var pixels = new Color[texWidth * texHeight];

            // ── Rest-pose reference frame (animRoot local space) ──────────────
            clips[0].SampleAnimation(animRoot, 0f);
            Matrix4x4 restWorldToLocal = animRoot.transform.worldToLocalMatrix;

            // ── Bake each clip ────────────────────────────────────────────────
            var clipInfos = new GPUClipInfoAsset[clipCount];
            int rowCursor = 0;

            for (int c = 0; c < clipCount; c++)
            {
                var clip = clips[c];
                int fc = frameCounts[c];

                clipInfos[c] = new GPUClipInfoAsset
                {
                    clipName = clip != null ? clip.name : "",
                    textureRowOffset = rowCursor,
                    frameCount = fc,
                    duration = clip != null ? clip.length : 0f,
                    frameRate = sampleRate,
                    isLooping = clip != null && clip.isLooping
                };

                if (clip == null || fc == 0) continue;

                for (int fi = 0; fi < fc; fi++)
                {
                    float t = fc > 1 ? Mathf.Clamp((fi / (float)(fc - 1)) * clip.length, 0f, clip.length) : 0f;
                    clip.SampleAnimation(animRoot, t);
                    int row = rowCursor + fi;

                    for (int b = 0; b < boneCount; b++)
                    {
                        // animBoneMatrix: bone pose in animRoot local space
                        Matrix4x4 animMat = restWorldToLocal * bones[b].localToWorldMatrix;

                        // PRE-MULTIPLY with bind pose so the shader only needs one matrix fetch.
                        // skinMat = animMat * bindPose
                        // This is equivalent to: animatedWorldPose * inverse(bindWorldPose)
                        Matrix4x4 skinMat = animMat * bindPoseForSkelBone[b];

                        // Store ROWS so HLSL float4x4(r0,r1,r2,r3) is correct without transpose.
                        int px = row * texWidth + b * 4;
                        pixels[px + 0] = new Color(skinMat.m00, skinMat.m01, skinMat.m02, skinMat.m03);
                        pixels[px + 1] = new Color(skinMat.m10, skinMat.m11, skinMat.m12, skinMat.m13);
                        pixels[px + 2] = new Color(skinMat.m20, skinMat.m21, skinMat.m22, skinMat.m23);
                        pixels[px + 3] = new Color(skinMat.m30, skinMat.m31, skinMat.m32, skinMat.m33);
                    }
                }

                rowCursor += fc;
            }

            tex.SetPixels(pixels);
            tex.Apply(false, false);

            // ── Bake mesh (bone weights into UV channels) ─────────────────────
            var bakedMesh = BakeMeshWithBoneWeights(smr, boneToIndex);

            // ── Library asset ─────────────────────────────────────────────────
            var libAsset = ScriptableObject.CreateInstance<GPUSkinningLibraryAsset>();
            libAsset.boneCount = boneCount;
            libAsset.totalFrames = totalFrames;
            libAsset.sampleRate = sampleRate;
            libAsset.clips = clipInfos;

            // ── Save ──────────────────────────────────────────────────────────
            string name = authoring.gameObject.name;
            AssetDatabase.CreateAsset(tex, $"{assetFolder}/AnimTex_{name}.asset");
            AssetDatabase.CreateAsset(bakedMesh, $"{assetFolder}/BakedMesh_{name}.asset");
            AssetDatabase.CreateAsset(libAsset, $"{assetFolder}/GPUSkinLib_{name}.asset");
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[AnimationTextureBaker] Done. {texWidth}x{texHeight} tex, " +
                      $"{boneCount} bones, {totalFrames} frames, {clipCount} clips. " +
                      "Skin matrices are pre-multiplied (animMat * bindPose).");

            return (tex, bakedMesh, libAsset);
        }

        static Mesh BakeMeshWithBoneWeights(SkinnedMeshRenderer smr, Dictionary<Transform, int> boneToIndex)
        {
            var srcMesh = smr.sharedMesh;
            var newMesh = Object.Instantiate(srcMesh);
            newMesh.name = srcMesh.name + "_GPUSkin";

            var smrBones = smr.bones;
            var boneWeights = srcMesh.boneWeights;
            int vertCount = srcMesh.vertexCount;

            var uv2 = new Vector2[vertCount];
            var uv3 = new Vector2[vertCount];
            var uv4 = new Vector2[vertCount];
            var uv5 = new Vector2[vertCount];

            for (int v = 0; v < vertCount; v++)
            {
                var bw = boneWeights[v];
                uv2[v] = new Vector2(RemapBone(bw.boneIndex0, smrBones, boneToIndex), RemapBone(bw.boneIndex1, smrBones, boneToIndex));
                uv3[v] = new Vector2(bw.weight0, bw.weight1);
                uv4[v] = new Vector2(RemapBone(bw.boneIndex2, smrBones, boneToIndex), RemapBone(bw.boneIndex3, smrBones, boneToIndex));
                uv5[v] = new Vector2(bw.weight2, bw.weight3);
            }

            newMesh.SetUVs(1, uv2); newMesh.SetUVs(2, uv3);
            newMesh.SetUVs(3, uv4); newMesh.SetUVs(4, uv5);
            newMesh.RecalculateBounds();
            return newMesh;
        }

        static int RemapBone(int idx, Transform[] smrBones, Dictionary<Transform, int> boneToIndex)
        {
            if (idx < 0 || idx >= smrBones.Length) return 0;
            return smrBones[idx] != null && boneToIndex.TryGetValue(smrBones[idx], out int r) ? r : 0;
        }

        static List<Transform> CollectBones(Transform root)
        {
            var list = new List<Transform>();
            void Recurse(Transform t) { list.Add(t); for (int i = 0; i < t.childCount; i++) Recurse(t.GetChild(i)); }
            Recurse(root);
            return list;
        }
    }
}
#endif


