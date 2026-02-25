using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Burst;
using Unity.Transforms;

namespace Shek.ECSNavigation
{
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct UnitMovementSystem : ISystem
    {
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NavigationConfig>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            float dt = SystemAPI.Time.DeltaTime;
            var config = SystemAPI.GetSingleton<NavigationConfig>();

            // A* followers
            var astarJob = new FollowAStarPathJob { DeltaTime = dt };
            state.Dependency = astarJob.ScheduleParallel(state.Dependency);

            // Macro followers — NO ECB needed.
            // When the macro path finishes, the job sets nav.MacroPathDone = 1.
            // NavigationDispatchSystem reads this flag on the main thread next frame
            // and issues the final PathRequest. This avoids the
            // BeginSimulationEntityCommandBufferSystem.Singleton not-found crash.
            var macroJob = new FollowMacroPathJob { DeltaTime = dt };
            state.Dependency = macroJob.ScheduleParallel(state.Dependency);

            // Flow field followers — directions pre-sampled by FlowFieldSamplerSystem.
            // Read the static BEFORE scheduling (we are on the main thread here;
            // the job receives the NativeHashMap by value so Burst never touches the static).
            var sampledDirs = FlowFieldSamplerSystem.SampledDirections;
            bool sampledValid = sampledDirs.IsCreated;
            var emptyDirs = sampledValid
                ? default(NativeHashMap<Entity, float2>)
                : new NativeHashMap<Entity, float2>(0, Allocator.TempJob);
            var flowJob = new FollowFlowFieldJob
            {
                DeltaTime = dt,
                Config = config,
                SampledDirections = sampledValid ? sampledDirs : emptyDirs
            };
            state.Dependency = flowJob.ScheduleParallel(state.Dependency);
            if (!sampledValid) { state.Dependency.Complete(); emptyDirs.Dispose(); }
        }

        // ── A* Waypoint Follower ─────────────────────────────────────────

        [BurstCompile]
        [WithAll(typeof(PathWaypoint))]
        partial struct FollowAStarPathJob : IJobEntity
        {
            public float DeltaTime;

            void Execute(ref LocalTransform transform, ref UnitMovement movement,
                         ref AgentNavigation nav, in DynamicBuffer<PathWaypoint> path,
                         in UnitLayerPermissions perms)
            {
                if (nav.Mode != NavMode.AStar) return;
                if (movement.IsFollowingPath == 0 || path.Length == 0) return;

                if (movement.CurrentWaypointIndex >= path.Length)
                {
                    movement.IsFollowingPath = 0;
                    return;
                }

                float3 currentPos = transform.Position;
                float3 target = path[movement.CurrentWaypointIndex].Position;
                if (perms.IsFlying == 0) target.y = currentPos.y;

                float dist = math.distance(currentPos, target);

                if (dist <= movement.TurnDistance)
                {
                    movement.CurrentWaypointIndex++;
                    if (movement.CurrentWaypointIndex >= path.Length)
                    {
                        movement.IsFollowingPath = 0;
                        return;
                    }
                    target = path[movement.CurrentWaypointIndex].Position;
                    if (perms.IsFlying == 0) target.y = currentPos.y;
                    dist = math.distance(currentPos, target);
                }

                if (dist < 0.001f) return;

                float3 direction = (target - currentPos) / dist;
                quaternion targetRot = quaternion.LookRotationSafe(direction, math.up());
                transform.Rotation = math.slerp(transform.Rotation, targetRot,
                    DeltaTime * movement.TurnSpeed);

                float3 forward = math.mul(transform.Rotation, new float3(0, 0, 1));
                float alignment = math.dot(forward, direction);
                float speedScale = math.max(0.25f, alignment);

                if (movement.CurrentWaypointIndex == path.Length - 1)
                    speedScale *= math.saturate(dist / (movement.TurnDistance * 3f));

                transform.Position += forward * movement.Speed * speedScale * DeltaTime;
            }
        }

        // ── Macro Path Follower ──────────────────────────────────────────

        [BurstCompile]
        [WithAll(typeof(MacroWaypoint))]
        partial struct FollowMacroPathJob : IJobEntity
        {
            public float DeltaTime;

            void Execute(ref LocalTransform transform, ref UnitMovement movement,
                         ref AgentNavigation nav,
                         in DynamicBuffer<MacroWaypoint> macroPath,
                         in UnitLayerPermissions perms)
            {
                if (nav.Mode != NavMode.MacroOnly) return;
                if (macroPath.Length == 0) return;

                if (movement.CurrentWaypointIndex >= macroPath.Length)
                {
                    // Signal main thread to issue the final A* path request.
                    // Writing nav fields directly is safe here (no ECB race).
                    nav.MacroPathDone = 1;
                    nav.Mode = NavMode.AStar;
                    movement.IsFollowingPath = 0;
                    movement.CurrentWaypointIndex = 0;
                    return;
                }

                float3 currentPos = transform.Position;
                float3 target = macroPath[movement.CurrentWaypointIndex].WorldEntryPoint;
                if (perms.IsFlying == 0) target.y = currentPos.y;

                float dist = math.distance(currentPos, target);

                const float chunkReachDist = 10f;
                if (dist <= chunkReachDist)
                {
                    movement.CurrentWaypointIndex++;
                    return;
                }

                float3 direction = (target - currentPos) / dist;
                quaternion targetRot = quaternion.LookRotationSafe(direction, math.up());
                transform.Rotation = math.slerp(transform.Rotation, targetRot,
                    DeltaTime * movement.TurnSpeed);

                float3 forward = math.mul(transform.Rotation, new float3(0, 0, 1));
                float alignment = math.dot(forward, direction);
                transform.Position += forward * movement.Speed * math.max(0.25f, alignment) * DeltaTime;
            }
        }

        // ── Flow Field Follower ──────────────────────────────────────────


        // FIX: FollowFlowFieldJob now receives sampled directions via a NativeHashMap
        // keyed by Entity. FlowFieldSamplerSystem (main thread, non-Burst) samples
        // FlowFieldSystem.TrySampleField each frame and writes results here.
        // The Burst job reads directions without needing to call managed code.
        [BurstCompile]
        partial struct FollowFlowFieldJob : IJobEntity
        {
            public float DeltaTime;
            public NavigationConfig Config;
            [ReadOnly] public NativeHashMap<Entity, float2> SampledDirections;

            void Execute(Entity entity, ref LocalTransform transform, ref UnitMovement movement,
                         ref AgentNavigation nav, in FlowFieldFollower follower,
                         EnabledRefRO<FlowFieldFollower> followerEnabled,
                         in UnitLayerPermissions perms)
            {
                if (!followerEnabled.ValueRO) return;
                if (nav.Mode != NavMode.FlowField) return;
                if (nav.HasDestination == 0) return;

                float3 currentPos = transform.Position;
                float dist = math.distance(currentPos, nav.Destination);
                if (dist < nav.ArrivalThreshold) return;

                // Use sampled flow field direction if available, else fall back to direct
                float3 direction;
                if (SampledDirections.TryGetValue(entity, out float2 fieldDir) &&
                    math.lengthsq(fieldDir) > 0.001f)
                {
                    direction = new float3(fieldDir.x, 0f, fieldDir.y);
                }
                else
                {
                    // Fallback: steer directly — field not ready yet for this chunk
                    float3 toTarget = nav.Destination - currentPos;
                    direction = math.normalize(new float3(toTarget.x, 0f, toTarget.z));
                }

                if (math.lengthsq(direction) < 0.001f) return;
                direction = math.normalize(direction);

                quaternion targetRot = quaternion.LookRotationSafe(direction, math.up());
                transform.Rotation = math.slerp(transform.Rotation, targetRot,
                    DeltaTime * movement.TurnSpeed);

                float3 forward = math.mul(transform.Rotation, new float3(0, 0, 1));
                float speedScale = math.max(0.5f, math.dot(forward, direction));
                transform.Position += forward * movement.Speed * speedScale * DeltaTime;
            }
        }
    }

    // ── Path Success Handler ─────────────────────────────────────────────────

    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(AStarSystem))]
    [UpdateBefore(typeof(UnitMovementSystem))]
    [BurstCompile]
    public partial struct PathSuccessHandlerSystem : ISystem
    {
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.TempJob);

            foreach (var (movement, nav, successEnabled, entity) in
                SystemAPI.Query<RefRW<UnitMovement>, RefRO<AgentNavigation>,
                                EnabledRefRO<PathfindingSuccess>>()
                    .WithEntityAccess())
            {
                if (!successEnabled.ValueRO) continue;
                if (nav.ValueRO.Mode == NavMode.AStar || nav.ValueRO.Mode == NavMode.MacroOnly)
                {
                    movement.ValueRW.IsFollowingPath = 1;
                    movement.ValueRW.CurrentWaypointIndex = 0;
                }
                ecb.SetComponentEnabled<PathfindingSuccess>(entity, false);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }

    // ── Flow Field Sampler ───────────────────────────────────────────────────
    // Runs on main thread (non-Burst) before UnitMovementSystem so it can call
    // FlowFieldSystem.TrySampleField (managed). Writes results to a static
    // NativeHashMap that FollowFlowFieldJob reads as a plain data lookup.

    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(UnitMovementSystem))]
    [UpdateAfter(typeof(FlowFieldSystem))]
    public partial class FlowFieldSamplerSystem : SystemBase
    {
        public static NativeHashMap<Entity, float2> SampledDirections;

        private FlowFieldSystem _flowFieldSystem;

        protected override void OnCreate()
        {
            RequireForUpdate<NavigationConfig>();
            SampledDirections = new NativeHashMap<Entity, float2>(512, Allocator.Persistent);
        }

        protected override void OnDestroy()
        {
            if (SampledDirections.IsCreated) SampledDirections.Dispose();
        }

        protected override void OnStartRunning()
        {
            _flowFieldSystem = World.GetExistingSystemManaged<FlowFieldSystem>();
        }

        protected override void OnUpdate()
        {
            if (_flowFieldSystem == null) return;
            var config = SystemAPI.GetSingleton<NavigationConfig>();

            SampledDirections.Clear();

            foreach (var (nav, transform, followerEnabled, entity) in
                SystemAPI.Query<RefRO<AgentNavigation>, RefRO<Unity.Transforms.LocalTransform>,
                                EnabledRefRO<FlowFieldFollower>>()
                    .WithEntityAccess())
            {
                if (!followerEnabled.ValueRO) continue;
                if (nav.ValueRO.Mode != NavMode.FlowField) continue;
                if (nav.ValueRO.HasDestination == 0) continue;

                ulong destHash = FlowFieldSystem.DestinationHash(nav.ValueRO.Destination, config);
                if (_flowFieldSystem.TrySampleField(destHash, transform.ValueRO.Position,
                        config, out float2 dir))
                {
                    SampledDirections[entity] = dir;
                }
            }
        }
    }
}
namespace Shek.ECSNavigation
{
    // ── Movement Event System ────────────────────────────────────────────────
    // Runs after UnitMovementSystem. Compares IsFollowingPath against
    // PreviousIsFollowingPath to detect 0->1 and 1->0 transitions and fire
    // the one-shot StartedMoving / StoppedMoving events.

    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(UnitMovementSystem))]
    [BurstCompile]
    public partial struct MovementEventSystem : ISystem
    {
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.TempJob);
            var job = new FireMovementEventsJob { ECBWriter = ecb.AsParallelWriter() };
            state.Dependency = job.ScheduleParallel(state.Dependency);
            state.Dependency.Complete();
            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        [BurstCompile]
        partial struct FireMovementEventsJob : IJobEntity
        {
            public EntityCommandBuffer.ParallelWriter ECBWriter;

            void Execute([ChunkIndexInQuery] int sortKey, Entity entity,
                         ref UnitMovement movement)
            {
                byte prev = movement.PreviousIsFollowingPath;
                byte curr = movement.IsFollowingPath;

                if (prev == 0 && curr == 1)
                    ECBWriter.SetComponentEnabled<StartedMoving>(sortKey, entity, true);
                else if (prev == 1 && curr == 0)
                    ECBWriter.SetComponentEnabled<StoppedMoving>(sortKey, entity, true);

                movement.PreviousIsFollowingPath = curr;
            }
        }
    }

    // ── Movement Event Cleanup System ────────────────────────────────────────
    // Runs at the end of the frame and disables StartedMoving / StoppedMoving
    // so they are active for exactly one frame.
    // [WithAll] on an IEnableableComponent filters to entities where it is ENABLED —
    // this is the correct pattern; [WithAny] does NOT filter by enabled state.

    // Cleanup runs on the main thread with direct SetComponentEnabled calls -
    // no ECB, no playback timing ambiguity.
    [UpdateInGroup(typeof(LateSimulationSystemGroup))]
    public partial class MovementEventCleanupSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            foreach (var (_, entity) in
                SystemAPI.Query<EnabledRefRO<StartedMoving>>()
                    .WithAll<StartedMoving>()
                    .WithEntityAccess())
            {
                SystemAPI.SetComponentEnabled<StartedMoving>(entity, false);
            }

            foreach (var (_, entity) in
                SystemAPI.Query<EnabledRefRO<StoppedMoving>>()
                    .WithAll<StoppedMoving>()
                    .WithEntityAccess())
            {
                SystemAPI.SetComponentEnabled<StoppedMoving>(entity, false);
            }
        }
    }
}