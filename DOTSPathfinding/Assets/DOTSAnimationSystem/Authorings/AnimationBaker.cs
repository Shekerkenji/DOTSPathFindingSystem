using Unity.Entities;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Shek.ECSAnimation
{
    public static class AnimationBaker
    {
        /// <summary>
        /// Bakes animation clip to object-space transforms.
        /// animationRoot: where to call SampleAnimation (usually Ninja)
        /// boneRoot: where to start hierarchy walk (usually Hips)
        /// </summary>
        public static BlobAssetReference<AnimationClipBlob> BakeAnimationClip(
            AnimationClip unityClip,
            GameObject animationRoot,
            GameObject boneRoot,
            float sampleRate = 60f,
            string clipName = null)
        {
            if (unityClip == null || animationRoot == null || boneRoot == null)
            {
                Debug.LogError("[AnimationBaker] Null argument!");
                return default;
            }

            var bones = GetBoneHierarchy(boneRoot.transform);
            int boneCount = bones.Length;
            if (boneCount == 0)
            {
                Debug.LogError("[AnimationBaker] No bones found!");
                return default;
            }

            // Build parent indices
            var parentIndices = new int[boneCount];
            var boneToIndex = new System.Collections.Generic.Dictionary<Transform, int>(boneCount);
            for (int i = 0; i < boneCount; i++)
                boneToIndex[bones[i]] = i;

            for (int i = 0; i < boneCount; i++)
            {
                var parent = bones[i].parent;
                parentIndices[i] = (parent != null && boneToIndex.TryGetValue(parent, out int pi)) ? pi : -1;
            }

            float duration = unityClip.length;
            int frameCount = Mathf.Max(1, Mathf.CeilToInt(duration * sampleRate) + 1);

            var builder = new BlobBuilder(Allocator.Temp);
            ref var root = ref builder.ConstructRoot<AnimationClipBlob>();

            root.BoneCount = boneCount;
            root.Duration = duration;
            root.FrameRate = sampleRate;
            root.IsLooping = unityClip.isLooping;

            // Hash the clip name so AnimationController can look it up by uint
            var nameStr = new Unity.Collections.FixedString64Bytes(clipName ?? unityClip.name);
            root.NameHash = (uint)nameStr.GetHashCode();

            var framesArray = builder.Allocate(ref root.Frames, frameCount * boneCount);
            var frameTimesArray = builder.Allocate(ref root.FrameTimes, frameCount);
            var boneNamesArray = builder.Allocate(ref root.BoneNames, boneCount);
            var parentIdxArray = builder.Allocate(ref root.ParentIndices, boneCount);

            // ── Coordinate space ─────────────────────────────────────────────────
            //
            // Unity's bindposes are defined as:
            //   bindPose[i] = inverse(bone[i].localToWorld)
            // when the mesh was bound (T-pose, character at world origin, identity rotation).
            //
            // SkinningMatrixSystem computes:
            //   skinMat = TRS(boneOS) * bindPose[i]
            //
            // For correct deformation at rest, boneOS must equal inverse(bindPose[i]),
            // i.e. bone[i].localToWorld at rest pose. At runtime it becomes
            // bone[i].localToWorld_animated.
            //
            // The bind poses were authored with identity world transform (no animRoot
            // rotation/translation). We must express baked bone transforms in the same
            // space — world space with the character at origin.
            //
            // We achieve this by sampling the REST POSE once, recording Hips'
            // world-to-local matrix as the fixed reference frame, then for every
            // animation frame expressing each bone relative to that rest-pose Hips.
            //
            // This correctly handles:
            //   • animRoot's 180° Y rotation (it cancels out since both rest and
            //     animated poses are expressed relative to the same Hips rest frame)
            //   • Hips translation (Hips becomes identity at rest, matching bindpose)
            //   • Animated Hips movement (expressed as delta from rest)

            // Sample rest pose (t=0) to get the fixed reference frame.
            var hipsTr = boneRoot.transform;
            unityClip.SampleAnimation(animationRoot, 0f);
            Matrix4x4 hipsRestWorldToLocal = hipsTr.worldToLocalMatrix;

            for (int frameIdx = 0; frameIdx < frameCount; frameIdx++)
            {
                float time = frameCount > 1
                    ? Mathf.Clamp((frameIdx / (float)(frameCount - 1)) * duration, 0f, duration)
                    : 0f;
                frameTimesArray[frameIdx] = time;

                unityClip.SampleAnimation(animationRoot, time);

                for (int b = 0; b < boneCount; b++)
                {
                    // Express bone in hips-rest space: fixed reference, no animated rotation contamination.
                    Matrix4x4 m = hipsRestWorldToLocal * bones[b].localToWorldMatrix;

                    Vector3 pos = m.GetColumn(3);
                    Quaternion rot = m.rotation;
                    Vector3 scl = new Vector3(
                        m.GetColumn(0).magnitude,
                        m.GetColumn(1).magnitude,
                        m.GetColumn(2).magnitude);

                    framesArray[frameIdx * boneCount + b] = new BoneTransform
                    {
                        Position = pos,
                        Rotation = rot,
                        Scale = scl
                    };
                }
            }

            for (int i = 0; i < boneCount; i++)
            {
                // FixedString64Bytes — Burst-safe, avoids managed BlobString.ToString() in jobs
                boneNamesArray[i] = new FixedString64Bytes(bones[i].name);
                parentIdxArray[i] = parentIndices[i];
            }

            var result = builder.CreateBlobAssetReference<AnimationClipBlob>(Allocator.Persistent);
            builder.Dispose();
            return result;
        }

        public static BlobAssetReference<SkinnedMeshBonesBlob> BakeSkinnedMeshBones(
            SkinnedMeshRenderer skinnedMesh)
        {
            if (skinnedMesh == null)
            {
                Debug.LogError("[AnimationBaker] SMR is null!");
                return default;
            }

            var bones = skinnedMesh.bones;
            var bindPoses = skinnedMesh.sharedMesh.bindposes;
            int boneCount = bones.Length;

            var builder = new BlobBuilder(Allocator.Temp);
            ref var root = ref builder.ConstructRoot<SkinnedMeshBonesBlob>();

            var boneIndicesArray = builder.Allocate(ref root.BoneIndices, boneCount);
            var bindPosesArray = builder.Allocate(ref root.BindPoses, boneCount);
            // FixedString64Bytes instead of BlobString — enables Burst in BoneIndexCachingSystem
            var boneNamesArray = builder.Allocate(ref root.BoneNames, boneCount);

            for (int i = 0; i < boneCount; i++)
            {
                boneIndicesArray[i] = -1; // Resolved at runtime by name
                bindPosesArray[i] = bindPoses[i];
                boneNamesArray[i] = new FixedString64Bytes(bones[i] != null ? bones[i].name : "");
            }

            root.RootBoneIndex = 0;

            var result = builder.CreateBlobAssetReference<SkinnedMeshBonesBlob>(Allocator.Persistent);
            builder.Dispose();
            return result;
        }

        private static Transform[] GetBoneHierarchy(Transform root)
        {
            var bones = new System.Collections.Generic.List<Transform>();
            CollectBonesRecursive(root, bones);
            return bones.ToArray();
        }

        private static void CollectBonesRecursive(Transform current,
            System.Collections.Generic.List<Transform> bones)
        {
            bones.Add(current);
            for (int i = 0; i < current.childCount; i++)
                CollectBonesRecursive(current.GetChild(i), bones);
        }
    }
}