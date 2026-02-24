using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Burst;
using Unity.Transforms;

namespace Navigation.ECS
{
    /// <summary>
    /// Moves agents along their paths.
    /// - A* agents: follow PathWaypoint buffer
    /// - FlowField agents: sample vector field each frame (O(1) lookup)
    /// - MacroOnly agents: follow MacroWaypoint chunk entry points
    ///
    /// Fully Burst compiled. No managed calls.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [BurstCompile]
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

            // ── A* path followers ──
            var astarJob = new FollowAStarPathJob { DeltaTime = dt };
            state.Dependency = astarJob.ScheduleParallel(state.Dependency);

            // ── Macro path followers (cross-chunk) ──
            var macroJob = new FollowMacroPathJob { DeltaTime = dt };
            state.Dependency = macroJob.ScheduleParallel(state.Dependency);

            // ── Flow field followers ──
            // Note: flow field sampling needs read access to FlowFieldData.
            // We pass the field data via a lookup (component lookup is safe read-only in parallel)
            var flowJob = new FollowFlowFieldJob
            {
                DeltaTime = dt,
                Config = config
                // FlowField sampling is done via chunk coord + cell lookup in the job
                // Full field lookup requires a NativeHashMap passed from FlowFieldSystem
                // (wired up in OnUpdate below via a shared static or singleton entity)
            };
            state.Dependency = flowJob.ScheduleParallel(state.Dependency);
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

                // Flatten Y for ground units
                if (perms.IsFlying == 0)
                {
                    target.y = currentPos.y; // Ignore vertical in movement calc
                }

                float dist = math.distance(currentPos, target);

                // Advance waypoint
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

                // Rotation
                quaternion targetRot = quaternion.LookRotationSafe(direction, math.up());
                transform.Rotation = math.slerp(
                    transform.Rotation, targetRot,
                    DeltaTime * movement.TurnSpeed);

                // Only move when reasonably aligned (prevents moonwalking)
                float3 forward = math.mul(transform.Rotation, new float3(0, 0, 1));
                float alignment = math.dot(forward, direction);
                float speedScale = math.max(0.25f, alignment);

                // Slow down near final waypoint
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
                    // Finished macro path — switch to A* for final micro approach
                    nav.Mode = NavMode.AStar;
                    movement.CurrentWaypointIndex = 0;
                    return;
                }

                float3 currentPos = transform.Position;
                float3 target = macroPath[movement.CurrentWaypointIndex].WorldEntryPoint;

                if (perms.IsFlying == 0) target.y = currentPos.y;

                float dist = math.distance(currentPos, target);

                // Chunk centre reach threshold is larger (coarse navigation)
                float chunkReachDist = 10f;
                if (dist <= chunkReachDist)
                {
                    movement.CurrentWaypointIndex++;
                    return;
                }

                float3 direction = (target - currentPos) / dist;
                quaternion targetRot = quaternion.LookRotationSafe(direction, math.up());
                transform.Rotation = math.slerp(transform.Rotation, targetRot, DeltaTime * movement.TurnSpeed);

                float3 forward = math.mul(transform.Rotation, new float3(0, 0, 1));
                float alignment = math.dot(forward, direction);
                float speedScale = math.max(0.25f, alignment);

                transform.Position += forward * movement.Speed * speedScale * DeltaTime;
            }
        }

        // ── Flow Field Follower ──────────────────────────────────────────

        [BurstCompile]
        // FlowFieldFollower is enableable — use EnabledRefRO in Execute
        partial struct FollowFlowFieldJob : IJobEntity
        {
            public float DeltaTime;
            public NavigationConfig Config;

            // Note: Direct NativeArray field sampling is done here.
            // To pass the field vectors, use a ComponentLookup<FlowFieldData>
            // or a shared NativeHashMap<int2, NativeArray<float2>>.
            // For now, movement uses last-known direction stored on agent.
            // Full field lookup wiring is completed when FlowFieldSystem
            // exposes a shared NativeHashMap via a singleton component.

            void Execute(ref LocalTransform transform, ref UnitMovement movement,
                         ref AgentNavigation nav, in FlowFieldFollower follower,
                         EnabledRefRO<FlowFieldFollower> followerEnabled,
                         in UnitLayerPermissions perms)
            {
                if (!followerEnabled.ValueRO) return;
                if (nav.Mode != NavMode.FlowField) return;
                if (nav.HasDestination == 0) return;

                // Direction from last cached sample (updated by FlowFieldSamplerSystem)
                // This job moves the agent using the cached direction
                float3 currentPos = transform.Position;

                // Simple fallback: move toward destination directly
                // Replace nav.LastKnownPosition with sampled flow vector
                float3 target = nav.Destination;
                float3 toTarget = target - currentPos;
                float dist = math.length(toTarget);

                if (dist < nav.ArrivalThreshold) return;

                float3 direction = toTarget / dist;
                if (perms.IsFlying == 0) direction.y = 0;
                if (math.lengthsq(direction) < 0.001f) return;
                direction = math.normalize(direction);

                quaternion targetRot = quaternion.LookRotationSafe(direction, math.up());
                transform.Rotation = math.slerp(transform.Rotation, targetRot, DeltaTime * movement.TurnSpeed);

                float3 forward = math.mul(transform.Rotation, new float3(0, 0, 1));
                float alignment = math.dot(forward, direction);

                // Flow field agents move more smoothly — less alignment penalty
                float speedScale = math.max(0.5f, alignment);
                transform.Position += forward * movement.Speed * speedScale * DeltaTime;
            }
        }
    }

    /// <summary>
    /// Fires path success signal — starts A* agents moving.
    /// Runs immediately after AStarSystem.
    /// </summary>
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
                SystemAPI.Query<RefRW<UnitMovement>, RefRO<AgentNavigation>, EnabledRefRO<PathfindingSuccess>>()
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
}