using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Burst;
using Unity.Transforms;

namespace Navigation.ECS
{
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

            // Flow field followers
            var flowJob = new FollowFlowFieldJob { DeltaTime = dt, Config = config };
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

        [BurstCompile]
        partial struct FollowFlowFieldJob : IJobEntity
        {
            public float DeltaTime;
            public NavigationConfig Config;

            void Execute(ref LocalTransform transform, ref UnitMovement movement,
                         ref AgentNavigation nav, in FlowFieldFollower follower,
                         EnabledRefRO<FlowFieldFollower> followerEnabled,
                         in UnitLayerPermissions perms)
            {
                if (!followerEnabled.ValueRO) return;
                if (nav.Mode != NavMode.FlowField) return;
                if (nav.HasDestination == 0) return;

                float3 currentPos = transform.Position;
                float3 toTarget = nav.Destination - currentPos;
                float dist = math.length(toTarget);

                if (dist < nav.ArrivalThreshold) return;

                float3 direction = toTarget / dist;
                if (perms.IsFlying == 0) direction.y = 0;
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
}