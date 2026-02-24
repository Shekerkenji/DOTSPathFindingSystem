using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Burst;
using Unity.Transforms;

namespace Navigation.ECS
{
    /// <summary>
    /// Processes NavigationMoveCommand and NavigationStopCommand each frame.
    ///
    /// HOW TO ISSUE MOVE ORDERS (pure ECS):
    ///
    ///   From any system or job, enable the command component and set the destination:
    ///
    ///     // Single agent move
    ///     ecb.SetComponentEnabled<NavigationMoveCommand>(agentEntity, true);
    ///     ecb.SetComponent(agentEntity, new NavigationMoveCommand
    ///     {
    ///         Destination = targetPosition,
    ///         Priority    = 1
    ///     });
    ///
    ///     // Stop
    ///     ecb.SetComponentEnabled<NavigationStopCommand>(agentEntity, true);
    ///
    /// That's it. This system reads the commands next frame, updates AgentNavigation,
    /// issues PathRequests, and disables the command components.
    ///
    /// Works identically for 1 agent or 10,000 — the job is parallel and Burst compiled.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(NavigationDispatchSystem))]
    [BurstCompile]
    public partial struct NavigationCommandSystem : ISystem
    {
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NavigationConfig>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            // FIX: Use SEPARATE ECBs for each job.
            // Sharing one ECB.AsParallelWriter() across two ScheduleParallel calls
            // causes an AtomicSafetyHandle write conflict — both jobs hold a write
            // handle to the same NativeArray inside the ECB simultaneously.

            // ── Move commands ──────────────────────────────────────────────
            var moveEcb = new EntityCommandBuffer(Allocator.TempJob);
            var moveJob = new ProcessMoveCommandJob
            {
                ECBWriter = moveEcb.AsParallelWriter(),
                CurrentTime = (float)SystemAPI.Time.ElapsedTime
            };
            var moveDep = moveJob.ScheduleParallel(state.Dependency);
            moveDep.Complete();
            moveEcb.Playback(state.EntityManager);
            moveEcb.Dispose();

            // ── Stop commands ──────────────────────────────────────────────
            var stopEcb = new EntityCommandBuffer(Allocator.TempJob);
            var stopJob = new ProcessStopCommandJob
            {
                ECBWriter = stopEcb.AsParallelWriter()
            };
            var stopDep = stopJob.ScheduleParallel(state.Dependency);
            stopDep.Complete();
            stopEcb.Playback(state.EntityManager);
            stopEcb.Dispose();

            // Update state.Dependency to signal both jobs are done
            state.Dependency = Unity.Jobs.JobHandle.CombineDependencies(moveDep, stopDep);
        }

        // ── Move ────────────────────────────────────────────────────────────

        [BurstCompile]
        partial struct ProcessMoveCommandJob : IJobEntity
        {
            public EntityCommandBuffer.ParallelWriter ECBWriter;
            public float CurrentTime;

            void Execute([ChunkIndexInQuery] int sortKey, Entity entity,
                         ref AgentNavigation nav,
                         ref UnitMovement movement,
                         in NavigationMoveCommand cmd,
                         EnabledRefRO<NavigationMoveCommand> cmdEnabled,
                         in LocalTransform transform)
            {
                if (!cmdEnabled.ValueRO) return;
                nav.Destination = cmd.Destination;
                nav.HasDestination = 1;
                nav.Mode = NavMode.AStar;
                nav.RepathCooldown = 0f;
                nav.MacroPathDone = 0;

                // Issue path request
                ECBWriter.SetComponentEnabled<PathRequest>(sortKey, entity, true);
                ECBWriter.SetComponent(sortKey, entity, new PathRequest
                {
                    Start = transform.Position,
                    End = cmd.Destination,
                    Priority = cmd.Priority,
                    RequestTime = CurrentTime
                });

                // Clear any active flow field following
                ECBWriter.SetComponentEnabled<FlowFieldFollower>(sortKey, entity, false);

                // Consume — disable so it doesn't re-trigger next frame
                ECBWriter.SetComponentEnabled<NavigationMoveCommand>(sortKey, entity, false);
            }
        }

        // ── Stop ────────────────────────────────────────────────────────────

        [BurstCompile]
        partial struct ProcessStopCommandJob : IJobEntity
        {
            public EntityCommandBuffer.ParallelWriter ECBWriter;

            void Execute([ChunkIndexInQuery] int sortKey, Entity entity,
                         ref AgentNavigation nav,
                         ref UnitMovement movement,
                         EnabledRefRO<NavigationStopCommand> stopEnabled)
            {
                if (!stopEnabled.ValueRO) return;
                nav.HasDestination = 0;
                nav.Mode = NavMode.Idle;
                movement.IsFollowingPath = 0;
                movement.CurrentWaypointIndex = 0;

                ECBWriter.SetComponentEnabled<FlowFieldFollower>(sortKey, entity, false);
                ECBWriter.SetComponentEnabled<PathRequest>(sortKey, entity, false);
                ECBWriter.SetComponentEnabled<NavigationStopCommand>(sortKey, entity, false);
            }
        }
    }
}