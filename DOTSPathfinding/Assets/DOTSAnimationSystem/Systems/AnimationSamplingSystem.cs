using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace Shek.ECSAnimation
{
    /// <summary>
    /// Samples animation poses each frame and writes float4x4 matrices into BoneTransformBuffer.
    ///
    /// Optimisation layers applied here:
    ///
    ///   1. CULLING  — entities with AnimationCulled enabled are skipped entirely.
    ///
    ///   2. LOD      — entities with AnimationLOD accumulate delta time and only re-sample
    ///                 when enough time has elapsed. Frozen poses cost one float add + compare.
    ///
    ///   3. SOA PATH — single-clip playback reads from FramesPos/Rot/Scl (SOA layout) so
    ///                 Burst can auto-vectorize the per-bone lerp: all positions in one
    ///                 contiguous array, all rotations in another, etc. Crossfade blending
    ///                 still uses the AOS Frames[] path which reads both bones together.
    ///
    ///   4. PARALLEL — IJobChunk scheduled with ScheduleParallel; each chunk runs on its
    ///                 own worker thread.
    /// </summary>
    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct AnimationSamplingSystem : ISystem
    {
        private EntityQuery _query;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _query = SystemAPI.QueryBuilder()
                .WithAll<AnimationController, AnimationLibraryReference, BoneTransformBuffer>()
                .WithAll<AnimationActive>()
                .Build();

            state.RequireForUpdate(_query);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var job = new LibrarySamplingJob
            {
                DeltaTime = SystemAPI.Time.DeltaTime,
                ControllerType = SystemAPI.GetComponentTypeHandle<AnimationController>(false),
                LibraryRefType = SystemAPI.GetComponentTypeHandle<AnimationLibraryReference>(true),
                BoneTransformType = SystemAPI.GetBufferTypeHandle<BoneTransformBuffer>(false),
                AnimActiveType = SystemAPI.GetComponentTypeHandle<AnimationActive>(true),
                LODType = SystemAPI.GetComponentTypeHandle<AnimationLOD>(false),
                CulledType = SystemAPI.GetComponentTypeHandle<AnimationCulled>(true),
            };
            state.Dependency = job.ScheduleParallel(_query, state.Dependency);
        }
    }

    [BurstCompile]
    struct LibrarySamplingJob : IJobChunk
    {
        public float DeltaTime;

        public ComponentTypeHandle<AnimationController> ControllerType;
        [ReadOnly] public ComponentTypeHandle<AnimationLibraryReference> LibraryRefType;
        public BufferTypeHandle<BoneTransformBuffer> BoneTransformType;
        [ReadOnly] public ComponentTypeHandle<AnimationActive> AnimActiveType;
        // Optional — not all entities have these; we check hasLOD / hasCulled per chunk.
        public ComponentTypeHandle<AnimationLOD> LODType;
        [ReadOnly] public ComponentTypeHandle<AnimationCulled> CulledType;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex,
            bool useEnabledMask, in v128 chunkEnabledMask)
        {
            var controllers = chunk.GetNativeArray(ref ControllerType);
            var libraryRefs = chunk.GetNativeArray(ref LibraryRefType);
            var boneBufferAcc = chunk.GetBufferAccessor(ref BoneTransformType);

            bool hasLOD = chunk.Has(ref LODType);
            bool hasCulled = chunk.Has(ref CulledType);

            // GetNativeArray on optional components only when the chunk has them.
            NativeArray<AnimationLOD> lods = hasLOD
                ? chunk.GetNativeArray(ref LODType)
                : default;

            for (int i = 0; i < chunk.Count; i++)
            {
                // -- 1. Skip if AnimationActive is disabled ----------------------
                if (!chunk.IsComponentEnabled(ref AnimActiveType, i)) continue;

                // -- 2. Skip if culled -------------------------------------------
                if (hasCulled && chunk.IsComponentEnabled(ref CulledType, i)) continue;

                var ctrl = controllers[i];
                if (!ctrl.IsPlaying && !ctrl.IsTransitioning) continue;

                var libRef = libraryRefs[i];
                if (!libRef.Value.IsCreated) continue;

                ref var lib = ref libRef.Value.Value;
                if (ctrl.ClipIndex >= lib.Clips.Length) continue;

                // -- 3. LOD: accumulate delta, skip sample if not yet due --------
                if (hasLOD)
                {
                    var lod = lods[i];
                    if (lod.SampleRateMultiplier <= 0f)
                    {
                        // Frozen — still advance controller time so state is consistent,
                        // but do not re-sample the pose.
                        AdvanceTime(ref ctrl, ref lib, DeltaTime);
                        controllers[i] = ctrl;
                        continue;
                    }

                    ref var curClip = ref lib.Clips[ctrl.ClipIndex];
                    float targetInterval = 1f / (curClip.FrameRate * lod.SampleRateMultiplier);
                    lod.AccumulatedDelta += DeltaTime;

                    if (lod.AccumulatedDelta < targetInterval)
                    {
                        // Not time to sample yet — advance time only.
                        AdvanceTime(ref ctrl, ref lib, DeltaTime);
                        controllers[i] = ctrl;
                        lods[i] = lod;
                        continue;
                    }

                    // Use the accumulated delta as the effective DeltaTime this sample.
                    DeltaTime = lod.AccumulatedDelta;
                    lod.AccumulatedDelta = 0f;
                    lods[i] = lod;
                }

                var boneTransforms = boneBufferAcc[i];

                // -- 4. Advance time and handle crossfade ------------------------
                AdvanceTime(ref ctrl, ref lib, DeltaTime);

                if (ctrl.IsTransitioning)
                {
                    if (ctrl.NextClipIndex >= lib.Clips.Length)
                    {
                        ctrl.NextClipIndex = AnimationController.NoTransition;
                    }
                    else
                    {
                        ref var nextClip = ref lib.Clips[ctrl.NextClipIndex];

                        ctrl.NextClipTime += DeltaTime * ctrl.NextClipSpeed;
                        if (nextClip.IsLooping && nextClip.Duration > 0f)
                            ctrl.NextClipTime = math.fmod(ctrl.NextClipTime, nextClip.Duration);
                        else
                            ctrl.NextClipTime = math.min(ctrl.NextClipTime, nextClip.Duration);

                        ctrl.TransitionTime += DeltaTime;
                        float alpha = math.saturate(ctrl.TransitionTime / ctrl.TransitionDuration);

                        if (alpha >= 1f)
                        {
                            ctrl.ClipIndex = ctrl.NextClipIndex;
                            ctrl.Time = ctrl.NextClipTime;
                            ctrl.Speed = ctrl.NextClipSpeed;
                            ctrl.IsPlaying = true;
                            ctrl.HasLooped = false;
                            ctrl.HasFinished = false;
                            ctrl.NextClipIndex = AnimationController.NoTransition;
                            ctrl.TransitionTime = 0f;
                            // Use SOA fast path for the finalised clip.
                            SampleClipSOA(ref lib, ctrl.ClipIndex, ctrl.Time, ref boneTransforms);
                        }
                        else
                        {
                            // Crossfade — AOS path reads both bone structs together.
                            SampleBlendedAOS(ref lib,
                                ctrl.ClipIndex, ctrl.Time,
                                ctrl.NextClipIndex, ctrl.NextClipTime,
                                alpha,
                                ref boneTransforms);
                        }

                        controllers[i] = ctrl;
                        continue;
                    }
                }

                // -- 5. Single-clip: SOA fast path --------------------------------
                SampleClipSOA(ref lib, ctrl.ClipIndex, ctrl.Time, ref boneTransforms);
                controllers[i] = ctrl;
            }
        }

        // ── Time advancement (shared between LOD-skip and normal paths) ────────
        static void AdvanceTime(ref AnimationController ctrl,
            ref AnimationLibraryBlob lib, float dt)
        {
            if (!ctrl.IsPlaying) return;
            ref var clip = ref lib.Clips[ctrl.ClipIndex];

            ctrl.HasLooped = false;
            ctrl.HasFinished = false;
            ctrl.Time += dt * ctrl.Speed;

            if (clip.IsLooping)
            {
                if (clip.Duration > 0f && ctrl.Time >= clip.Duration)
                {
                    ctrl.Time = math.fmod(ctrl.Time, clip.Duration);
                    ctrl.HasLooped = true;
                }
            }
            else
            {
                if (ctrl.Time >= clip.Duration)
                {
                    ctrl.Time = clip.Duration;
                    ctrl.HasFinished = true;
                    ctrl.IsPlaying = false;
                }
            }
        }

        // ── SOA fast path — single-clip ────────────────────────────────────────
        // Reads three separate contiguous arrays (pos, rot, scl) so Burst can
        // issue vector loads and auto-vectorize the lerp across all bones.
        static void SampleClipSOA(
            ref AnimationLibraryBlob lib,
            ushort clipIndex,
            float time,
            ref DynamicBuffer<BoneTransformBuffer> outBones)
        {
            ref var clip = ref lib.Clips[clipIndex];
            if (clip.FrameCount == 0) return;

            int frameIdx = FindFrameIndex(ref lib.FrameTimes, clip.TimeOffset, clip.FrameCount, time);
            int nextFrameIdx = math.min(frameIdx + 1, clip.FrameCount - 1);
            float t = InterpT(ref lib.FrameTimes, clip.TimeOffset, frameIdx, nextFrameIdx, time);

            int boneCount = lib.BoneCount;
            int baseA = clip.FrameOffset + frameIdx * boneCount;
            int baseB = clip.FrameOffset + nextFrameIdx * boneCount;

            for (int b = 0; b < boneCount; b++)
            {
                // Each read is into a flat, contiguous typed array — cache-friendly,
                // and Burst can pipeline/vectorize the lerp across consecutive bones.
                float3 pos = math.lerp(lib.FramesPos[baseA + b], lib.FramesPos[baseB + b], t);
                quaternion rot = math.slerp(lib.FramesRot[baseA + b], lib.FramesRot[baseB + b], t);
                float3 scl = math.lerp(lib.FramesScl[baseA + b], lib.FramesScl[baseB + b], t);

                outBones[b] = new BoneTransformBuffer
                {
                    Matrix = float4x4.TRS(pos, rot, scl)
                };
            }
        }

        // ── AOS blending path — crossfade ──────────────────────────────────────
        // Reads BoneTransform structs (pos+rot+scl together) which is better for
        // crossfade since we need all three components of both poses simultaneously.
        static void SampleBlendedAOS(
            ref AnimationLibraryBlob lib,
            ushort fromClip, float fromTime,
            ushort toClip, float toTime,
            float alpha,
            ref DynamicBuffer<BoneTransformBuffer> outBones)
        {
            ref var clipA = ref lib.Clips[fromClip];
            ref var clipB = ref lib.Clips[toClip];
            if (clipA.FrameCount == 0 && clipB.FrameCount == 0) return;

            int boneCount = lib.BoneCount;

            int fA = FindFrameIndex(ref lib.FrameTimes, clipA.TimeOffset, clipA.FrameCount, fromTime);
            int fA2 = math.min(fA + 1, clipA.FrameCount - 1);
            float tA = InterpT(ref lib.FrameTimes, clipA.TimeOffset, fA, fA2, fromTime);

            int fB = FindFrameIndex(ref lib.FrameTimes, clipB.TimeOffset, clipB.FrameCount, toTime);
            int fB2 = math.min(fB + 1, clipB.FrameCount - 1);
            float tB = InterpT(ref lib.FrameTimes, clipB.TimeOffset, fB, fB2, toTime);

            int baseA1 = clipA.FrameOffset + fA * boneCount;
            int baseA2 = clipA.FrameOffset + fA2 * boneCount;
            int baseB1 = clipB.FrameOffset + fB * boneCount;
            int baseB2 = clipB.FrameOffset + fB2 * boneCount;

            for (int b = 0; b < boneCount; b++)
            {
                // NlerpFast for intra-clip; the outer cross-clip blend normalizes anyway.
                var poseA = BoneTransform.NlerpFast(lib.Frames[baseA1 + b], lib.Frames[baseA2 + b], tA);
                var poseB = BoneTransform.NlerpFast(lib.Frames[baseB1 + b], lib.Frames[baseB2 + b], tB);

                float3 pos = math.lerp(poseA.Position, poseB.Position, alpha);
                quaternion rot = math.normalizesafe(
                    math.lerp(poseA.Rotation.value, poseB.Rotation.value, alpha));
                float3 scl = math.lerp(poseA.Scale, poseB.Scale, alpha);

                outBones[b] = new BoneTransformBuffer { Matrix = float4x4.TRS(pos, rot, scl) };
            }
        }

        // ── Helpers ────────────────────────────────────────────────────────────
        static int FindFrameIndex(
            ref BlobArray<float> allTimes, int timeOffset, int frameCount, float time)
        {
            if (frameCount == 0) return 0;
            if (time <= allTimes[timeOffset]) return 0;
            if (time >= allTimes[timeOffset + frameCount - 1]) return frameCount - 1;

            int low = 0, high = frameCount - 1;
            while (low <= high)
            {
                int mid = (low + high) >> 1;
                float mt = allTimes[timeOffset + mid];
                if (mt == time) return mid;
                else if (mt < time) low = mid + 1;
                else high = mid - 1;
            }
            return high;
        }

        static float InterpT(
            ref BlobArray<float> allTimes, int timeOffset,
            int frameIdx, int nextFrameIdx, float time)
        {
            if (frameIdx == nextFrameIdx) return 0f;
            float fd = allTimes[timeOffset + nextFrameIdx] - allTimes[timeOffset + frameIdx];
            return fd > 0.0001f ? (time - allTimes[timeOffset + frameIdx]) / fd : 0f;
        }
    }
}