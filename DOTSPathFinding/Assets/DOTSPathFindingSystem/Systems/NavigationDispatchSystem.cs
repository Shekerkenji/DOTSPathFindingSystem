using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Burst;
using Unity.Transforms;

namespace Shek.ECSNavigation
{
    /// <summary>
    /// Decides A* vs FlowField vs MacroOnly per agent each frame.
    ///
    /// KEY RULES:
    /// - MacroOnly is ONLY used when the destination chunk has no static data loaded.
    ///   For short moves (destination in an adjacent active chunk), A* is used directly.
    /// - Agents actively following a valid path are not re-evaluated — prevents thrashing.
    /// - MacroPathDone flag (written by FollowMacroPathJob) triggers final A* request
    ///   on the main thread, avoiding the BeginSimulationECBSystem.Singleton crash.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(AStarSystem))]
    public partial class NavigationDispatchSystem : SystemBase
    {
        private const int CrowdThreshold = 12;

        protected override void OnCreate()
        {
            RequireForUpdate<NavigationConfig>();
        }

        protected override void OnUpdate()
        {
            var config = SystemAPI.GetSingleton<NavigationConfig>();
            float time = (float)SystemAPI.Time.ElapsedTime;
            var ecb = new EntityCommandBuffer(Allocator.TempJob);

            // Build set of chunk coords that have valid static data.
            // These are the only chunks A* can pathfind inside.
            var readyChunks = new NativeHashSet<int2>(128, Allocator.Temp);
            foreach (var chunk in SystemAPI.Query<RefRO<GridChunk>>())
                if (chunk.ValueRO.StaticDataReady == 1)
                    readyChunks.Add(chunk.ValueRO.ChunkCoord);

            // Count agents per destination for FlowField threshold
            var destCounts = new NativeHashMap<ulong, int>(64, Allocator.Temp);
            foreach (var nav in SystemAPI.Query<RefRO<AgentNavigation>>())
            {
                if (nav.ValueRO.HasDestination == 0) continue;
                ulong key = QuantizeDestination(nav.ValueRO.Destination, config);
                destCounts.TryGetValue(key, out int count);
                destCounts[key] = count + 1;
            }

            foreach (var (nav, movement, transform, perms, entity) in
                SystemAPI.Query<RefRW<AgentNavigation>, RefRW<UnitMovement>,
                                RefRO<LocalTransform>, RefRO<UnitLayerPermissions>>()
                    .WithEntityAccess())
            {
                if (nav.ValueRO.HasDestination == 0) continue;

                // ── Arrival ────────────────────────────────────────────────
                float distToDest = math.distance(transform.ValueRO.Position, nav.ValueRO.Destination);
                // Use a slightly larger arrival threshold to account for destination being
                // snapped away from the exact click point (e.g. click on wall snaps to
                // nearest walkable cell centre which may be >ArrivalThreshold from click).
                float effectiveArrival = math.max(nav.ValueRO.ArrivalThreshold, 1.5f);
                if (distToDest <= effectiveArrival)
                {
                    nav.ValueRW.HasDestination = 0;
                    nav.ValueRW.Mode = NavMode.Idle;
                    nav.ValueRW.MacroPathDone = 0;
                    movement.ValueRW.IsFollowingPath = 0;
                    movement.ValueRW.CurrentWaypointIndex = 0;
                    ecb.SetComponentEnabled<FlowFieldFollower>(entity, false);
                    continue;
                }

                // ── MacroPathDone handoff ──────────────────────────────────
                // FollowMacroPathJob sets this when it finishes the chunk corridor.
                // We issue the final micro A* request here on the main thread.
                if (nav.ValueRO.MacroPathDone == 1)
                {
                    nav.ValueRW.MacroPathDone = 0;
                    nav.ValueRW.Mode = NavMode.AStar;
                    movement.ValueRW.IsFollowingPath = 0;
                    IssuePathRequest(entity, transform.ValueRO.Position,
                                     nav.ValueRO.Destination, ecb);
                    nav.ValueRW.RepathCooldown = time + 0.5f;
                    continue;
                }

                // ── Skip agents already moving on a valid path ─────────────
                // Re-evaluating mode every frame would interrupt valid paths.
                if (movement.ValueRO.IsFollowingPath == 1 &&
                    nav.ValueRO.Mode != NavMode.Idle)
                    continue;

                // ── Determine mode ─────────────────────────────────────────
                int2 destChunk = ChunkManagerSystem.WorldToChunkCoord(
                    nav.ValueRO.Destination, config);

                NavMode desiredMode;
                if (!readyChunks.Contains(destChunk))
                {
                    // Destination chunk not baked yet — need macro nav to approach
                    desiredMode = NavMode.MacroOnly;
                }
                else
                {
                    // Destination chunk has static data — use A* directly.
                    // A* handles cross-chunk moves fine when both chunks are loaded.
                    ulong key = QuantizeDestination(nav.ValueRO.Destination, config);
                    int cnt = destCounts.TryGetValue(key, out int c) ? c : 1;
                    desiredMode = cnt >= CrowdThreshold ? NavMode.FlowField : NavMode.AStar;
                }

                // ── Apply mode ─────────────────────────────────────────────
                if (desiredMode != nav.ValueRO.Mode ||
                    (movement.ValueRO.IsFollowingPath == 0 && time >= nav.ValueRO.RepathCooldown))
                {
                    nav.ValueRW.Mode = desiredMode;

                    if (desiredMode == NavMode.AStar || desiredMode == NavMode.MacroOnly)
                    {
                        ecb.SetComponentEnabled<FlowFieldFollower>(entity, false);
                        IssuePathRequest(entity, transform.ValueRO.Position,
                                         nav.ValueRO.Destination, ecb);
                        nav.ValueRW.RepathCooldown = time + 0.5f;
                    }
                    else if (desiredMode == NavMode.FlowField)
                    {
                        movement.ValueRW.IsFollowingPath = 0;
                        ecb.SetComponentEnabled<FlowFieldFollower>(entity, true);
                    }
                }
            }

            // Stuck detection
            var stuckJob = new StuckDetectionJob
            {
                CurrentTime = time,
                ECBWriter = ecb.AsParallelWriter()
            };
            Dependency = stuckJob.ScheduleParallel(Dependency);
            Dependency.Complete();

            ecb.Playback(EntityManager);
            ecb.Dispose();
            destCounts.Dispose();
            readyChunks.Dispose();
        }

        private static void IssuePathRequest(Entity entity, float3 start, float3 end,
                                              EntityCommandBuffer ecb)
        {
            ecb.SetComponentEnabled<PathRequest>(entity, true);
            ecb.SetComponent(entity, new PathRequest
            {
                Start = start,
                End = end,
                Priority = 1,
                RequestTime = 0f
            });
        }

        private static ulong QuantizeDestination(float3 pos, NavigationConfig config)
        {
            int2 cell = new int2(
                (int)math.floor(pos.x / config.CellSize),
                (int)math.floor(pos.z / config.CellSize));
            return (ulong)((long)cell.x << 32 | (uint)cell.y);
        }

        [BurstCompile]
        [WithAll(typeof(AgentNavigation))]
        partial struct StuckDetectionJob : IJobEntity
        {
            public float CurrentTime;
            public EntityCommandBuffer.ParallelWriter ECBWriter;

            void Execute([ChunkIndexInQuery] int sortKey, Entity entity,
                         ref StuckDetection stuck, ref AgentNavigation nav,
                         ref UnitMovement movement, in LocalTransform transform)
            {
                if (nav.HasDestination == 0 || nav.Mode == NavMode.Idle) return;
                if (CurrentTime < stuck.NextCheckTime) return;

                float moved = math.distance(transform.Position, stuck.LastCheckedPosition);
                if (moved < stuck.StuckDistanceThreshold && movement.IsFollowingPath == 1)
                {
                    stuck.StuckCount++;
                    if (stuck.StuckCount >= stuck.MaxStuckCount)
                    {
                        movement.IsFollowingPath = 0;
                        movement.CurrentWaypointIndex = 0;
                        stuck.StuckCount = 0;
                        ECBWriter.SetComponentEnabled<NeedsRepath>(sortKey, entity, true);
                    }
                }
                else stuck.StuckCount = 0;

                stuck.LastCheckedPosition = transform.Position;
                stuck.NextCheckTime = CurrentTime + stuck.CheckInterval;
            }
        }
    }

    /// <summary>
    /// Converts NeedsRepath tags into PathRequest components.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(NavigationDispatchSystem))]
    [UpdateBefore(typeof(AStarSystem))]
    public partial struct RepathSystem : ISystem
    {
        public void OnCreate(ref SystemState state) => state.RequireForUpdate<NavigationConfig>();

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.TempJob);
            foreach (var (nav, transform, repathEnabled, entity) in
                SystemAPI.Query<RefRO<AgentNavigation>, RefRO<LocalTransform>,
                                EnabledRefRO<NeedsRepath>>()
                    .WithEntityAccess())
            {
                if (nav.ValueRO.HasDestination == 0) continue;
                ecb.SetComponentEnabled<NeedsRepath>(entity, false);
                ecb.SetComponentEnabled<PathRequest>(entity, true);
                ecb.SetComponent(entity, new PathRequest
                {
                    Start = transform.ValueRO.Position,
                    End = nav.ValueRO.Destination,
                    Priority = 2,
                    RequestTime = (float)SystemAPI.Time.ElapsedTime
                });
            }
            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }
}