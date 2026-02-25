using Unity.Entities;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using System.Collections.Generic;

namespace DOTSAnimation
{
    /// <summary>
    /// Bakes a list of AnimationClips into a single <see cref="AnimationLibraryBlob"/>.
    ///
    /// Frame data is written in BOTH AOS and SOA layout so the runtime can choose the
    /// fastest path per situation:
    ///   • AOS  (Frames[])           — crossfade blending path (reads two bones at once)
    ///   • SOA  (FramesPos/Rot/Scl)  — fast single-clip path (Burst auto-vectorizes per
    ///                                  component across all bones)
    ///
    /// Bone names are stored as FixedString64Bytes (not BlobString) so
    /// BoneIndexCachingSystem can be Burst-compiled.
    /// </summary>
    public static class AnimationLibraryBaker
    {
        public static BlobAssetReference<AnimationLibraryBlob> BakeLibrary(
            IReadOnlyList<AnimationClip> clips,
            GameObject animationRoot,
            GameObject boneRoot,
            float sampleRate = 60f)
        {
            if (clips == null || clips.Count == 0 || animationRoot == null || boneRoot == null)
            {
                Debug.LogError("[AnimationLibraryBaker] Null / empty argument.");
                return default;
            }

            // -- Collect skeleton --------------------------------------------------
            var bones = GetBoneHierarchy(boneRoot.transform);
            int boneCount = bones.Length;
            if (boneCount == 0)
            {
                Debug.LogError("[AnimationLibraryBaker] No bones found under boneRoot.");
                return default;
            }

            var parentIndices = new int[boneCount];
            var boneToIndex = new Dictionary<Transform, int>(boneCount);
            for (int i = 0; i < boneCount; i++) boneToIndex[bones[i]] = i;
            for (int i = 0; i < boneCount; i++)
            {
                var p = bones[i].parent;
                parentIndices[i] = (p != null && boneToIndex.TryGetValue(p, out int pi)) ? pi : -1;
            }

            // -- Rest-pose reference frame (t=0 of first clip) --------------------
            var hipsTr = boneRoot.transform;
            clips[0].SampleAnimation(animationRoot, 0f);
            Matrix4x4 hipsRestWorldToLocal = hipsTr.worldToLocalMatrix;

            // -- Pre-compute per-clip frame counts --------------------------------
            int clipCount = clips.Count;
            var frameCounts = new int[clipCount];
            int totalFrames = 0;
            int totalTimes = 0;

            for (int c = 0; c < clipCount; c++)
            {
                var clip = clips[c];
                if (clip == null) { frameCounts[c] = 0; continue; }
                int fc = Mathf.Max(1, Mathf.CeilToInt(clip.length * sampleRate) + 1);
                frameCounts[c] = fc;
                totalFrames += fc * boneCount;
                totalTimes += fc;
            }

            // -- Build blob -------------------------------------------------------
            var builder = new BlobBuilder(Allocator.Temp);
            ref var root = ref builder.ConstructRoot<AnimationLibraryBlob>();

            root.BoneCount = boneCount;

            var clipsArray = builder.Allocate(ref root.Clips, clipCount);
            // AOS -- used by blending path
            var framesAOS = builder.Allocate(ref root.Frames, totalFrames);
            // SOA -- used by fast single-clip path; Burst can vectorize per-component lerp
            var framesPos = builder.Allocate(ref root.FramesPos, totalFrames);
            var framesRot = builder.Allocate(ref root.FramesRot, totalFrames);
            var framesScl = builder.Allocate(ref root.FramesScl, totalFrames);

            var timesArray = builder.Allocate(ref root.FrameTimes, totalTimes);
            var boneNamesArr = builder.Allocate(ref root.BoneNames, boneCount);
            var parentIdxArr = builder.Allocate(ref root.ParentIndices, boneCount);

            // Bone names as FixedString64Bytes -- Burst-safe, no managed heap
            for (int i = 0; i < boneCount; i++)
            {
                boneNamesArr[i] = new FixedString64Bytes(bones[i].name);
                parentIdxArr[i] = parentIndices[i];
            }

            // -- Sample each clip -------------------------------------------------
            int frameCursor = 0;
            int timeCursor = 0;

            for (int c = 0; c < clipCount; c++)
            {
                var clip = clips[c];
                int fc = frameCounts[c];

                if (clip == null || fc == 0)
                {
                    clipsArray[c] = new AnimationClipInfo
                    {
                        FrameOffset = frameCursor,
                        FrameCount = 0,
                        TimeOffset = timeCursor,
                        Duration = 0f,
                        FrameRate = sampleRate,
                        IsLooping = false,
                        NameHash = 0
                    };
                    continue;
                }

                float duration = clip.length;
                var nameFs = new FixedString64Bytes(clip.name);

                clipsArray[c] = new AnimationClipInfo
                {
                    FrameOffset = frameCursor,
                    FrameCount = fc,
                    TimeOffset = timeCursor,
                    Duration = duration,
                    FrameRate = sampleRate,
                    IsLooping = clip.isLooping,
                    NameHash = AnimationLibraryBlob.HashName(nameFs)
                };

                for (int fi = 0; fi < fc; fi++)
                {
                    float time = fc > 1
                        ? Mathf.Clamp((fi / (float)(fc - 1)) * duration, 0f, duration)
                        : 0f;

                    timesArray[timeCursor + fi] = time;
                    clip.SampleAnimation(animationRoot, time);

                    for (int b = 0; b < boneCount; b++)
                    {
                        Matrix4x4 m = hipsRestWorldToLocal * bones[b].localToWorldMatrix;
                        float3 pos = (Vector3)m.GetColumn(3);
                        quaternion rot = m.rotation;
                        float3 scl = new float3(
                            m.GetColumn(0).magnitude,
                            m.GetColumn(1).magnitude,
                            m.GetColumn(2).magnitude);

                        int idx = frameCursor + fi * boneCount + b;

                        // AOS
                        framesAOS[idx] = new BoneTransform { Position = pos, Rotation = rot, Scale = scl };
                        // SOA -- same data split into three parallel arrays
                        framesPos[idx] = pos;
                        framesRot[idx] = rot;
                        framesScl[idx] = scl;
                    }
                }

                frameCursor += fc * boneCount;
                timeCursor += fc;
            }

            var result = builder.CreateBlobAssetReference<AnimationLibraryBlob>(Allocator.Persistent);
            builder.Dispose();
            return result;
        }

        static Transform[] GetBoneHierarchy(Transform root)
        {
            var list = new List<Transform>();
            Collect(root, list);
            return list.ToArray();
        }

        static void Collect(Transform t, List<Transform> list)
        {
            list.Add(t);
            for (int i = 0; i < t.childCount; i++) Collect(t.GetChild(i), list);
        }
    }
}