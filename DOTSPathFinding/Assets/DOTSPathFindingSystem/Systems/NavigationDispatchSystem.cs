using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Burst;
using Unity.Transforms;

namespace Navigation.ECS
{
    /// <summary>
    /// Decides A* vs FlowField per agent each frame.
    /// Routes stuck detection and repath requests.
    /// Uses SystemAPI.Query throughout — no deprecated Entities.ForEach.
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

            // 1. Count agents per quantized destination
            var destCounts = new NativeHashMap<ulong, int>(64, Allocator.Temp);
            foreach (var nav in SystemAPI.Query<RefRO<AgentNavigation>>())
            {
                if (nav.ValueRO.HasDestination == 0) continue;
                ulong key = QuantizeDestination(nav.ValueRO.Destination, config);
                destCounts.TryGetValue(key, out int count);
                destCounts[key] = count + 1;
            }

            // 2. Assign mode per agent
            foreach (var (nav, movement, transform, perms, entity) in
                SystemAPI.Query<RefRW<AgentNavigation>, RefRW<UnitMovement>,
                                RefRO<LocalTransform>, RefRO<UnitLayerPermissions>>()
                    .WithEntityAccess())
            {
                if (nav.ValueRO.HasDestination == 0) continue;

                // Arrival check
                float distToDest = math.distance(transform.ValueRO.Position, nav.ValueRO.Destination);
                if (distToDest <= nav.ValueRO.ArrivalThreshold)
                {
                    nav.ValueRW.HasDestination = 0;
                    nav.ValueRW.Mode = NavMode.Idle;
                    movement.ValueRW.IsFollowingPath = 0;
                    ecb.SetComponentEnabled<FlowFieldFollower>(entity, false);
                    continue;
                }

                // Determine desired mode
                int2 destChunk = ChunkManagerSystem.WorldToChunkCoord(nav.ValueRO.Destination, config);
                int2 startChunk = ChunkManagerSystem.WorldToChunkCoord(transform.ValueRO.Position, config);
                bool crossChunk = !math.all(startChunk == destChunk);

                NavMode desiredMode;
                if (crossChunk && nav.ValueRO.Mode != NavMode.MacroOnly)
                    desiredMode = NavMode.MacroOnly;
                else
                {
                    ulong key = QuantizeDestination(nav.ValueRO.Destination, config);
                    int cnt = destCounts.TryGetValue(key, out int c) ? c : 1;
                    desiredMode = cnt >= CrowdThreshold ? NavMode.FlowField : NavMode.AStar;
                }

                // Handle mode transition
                if (desiredMode != nav.ValueRO.Mode)
                {
                    nav.ValueRW.Mode = desiredMode;
                    if (desiredMode == NavMode.AStar || desiredMode == NavMode.MacroOnly)
                    {
                        ecb.SetComponentEnabled<FlowFieldFollower>(entity, false);
                        IssuePathRequest(entity, transform.ValueRO.Position, nav.ValueRO.Destination, ecb);
                    }
                    else if (desiredMode == NavMode.FlowField)
                    {
                        movement.ValueRW.IsFollowingPath = 0;
                        ecb.SetComponentEnabled<FlowFieldFollower>(entity, true);
                    }
                }

                // Repath if A* agent has no path and cooldown elapsed
                if (nav.ValueRO.Mode == NavMode.AStar &&
                    movement.ValueRO.IsFollowingPath == 0 &&
                    time >= nav.ValueRO.RepathCooldown)
                {
                    IssuePathRequest(entity, transform.ValueRO.Position, nav.ValueRO.Destination, ecb);
                    nav.ValueRW.RepathCooldown = time + 0.5f;
                }
            }

            // 3. Stuck detection — Burst parallel job
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
        }

        private static void IssuePathRequest(Entity entity, float3 start, float3 end,
                                              EntityCommandBuffer ecb)
        {
            ecb.SetComponentEnabled<PathRequest>(entity, true);
            ecb.SetComponent(entity, new PathRequest
            { Start = start, End = end, Priority = 1, RequestTime = 0f });
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
                SystemAPI.Query<RefRO<AgentNavigation>, RefRO<LocalTransform>, EnabledRefRO<NeedsRepath>>()
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