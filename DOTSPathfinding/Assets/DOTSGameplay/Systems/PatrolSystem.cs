using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using Shek.ECSNavigation;

namespace Shek.ECSGameplay
{
    /// <summary>
    /// Drives patrol behaviour for units that have a PatrolData component.
    ///
    /// Lifecycle each frame:
    ///   1. Skip if unit is Dead, or if it has a live enemy target → combat
    ///      systems (AIDecisionSystem) take over completely in that case.
    ///   2. Skip if in Hit state (DamageSystem handles recovery).
    ///   3. If waiting at a waypoint, count down WaitTimer.
    ///   4. Once the wait expires (or ArrivalRadius is met while not waiting),
    ///      advance to the next waypoint and issue a NavigationMoveCommand.
    ///   5. Each command is only re-issued when the unit actually needs to
    ///      move to a new waypoint — no redundant commands per frame.
    ///
    /// Navigation integration:
    ///   Issues NavigationMoveCommand exactly as RTSSystem and AIDecisionSystem do.
    ///   NavigationCommandSystem (in ECSNavigation) picks it up next frame,
    ///   triggers A* / FlowField, and hands off to UnitMovementSystem.
    ///
    /// Update order:
    ///   Runs AFTER ThreatScanSystem (so CurrentTarget is fresh) and
    ///   BEFORE AIDecisionSystem (so if we set the state to Patrol, AI sees it).
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ThreatScanSystem))]
    [UpdateBefore(typeof(AllyPingSystem))]
    public partial class PatrolSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<PatrolData>();
        }

        protected override void OnUpdate()
        {
            float dt = SystemAPI.Time.DeltaTime;
            float time = (float)SystemAPI.Time.ElapsedTime;

            var ecb = new EntityCommandBuffer(Allocator.TempJob);

            // Main-thread foreach — kept single-threaded because:
            //  a) We write to DynamicBuffers (PatrolWaypoint) which requires
            //     BufferLookup + safe parallel access patterns, adding complexity.
            //  b) Patrol logic only runs for units without a target, so in heavy
            //     combat scenes very few units will execute this path.
            //  c) NavigationMoveCommand is issued rarely (once per waypoint arrival),
            //     not every frame, so the per-frame cost is minimal.
            foreach (var (patrol, currentTarget, aiState, transform, entity) in
                SystemAPI.Query<
                    RefRW<PatrolData>,
                    RefRO<CurrentTarget>,
                    RefRW<AIState>,
                    RefRO<LocalTransform>>()
                    .WithDisabled<DeadTag>()
                    .WithEntityAccess())
            {
                // ── Hand off to combat if a target exists ──────────────────
                // AIDecisionSystem handles everything once HasTarget == 1.
                if (currentTarget.ValueRO.HasTarget == 1)
                {
                    // If we were patrolling, mark state so AIDecisionSystem knows
                    // the unit is now transitioning rather than in Moving.
                    // (AIDecisionSystem will overwrite State itself; this just
                    // ensures StateTimer is reasonable.)
                    continue;
                }

                // ── Skip during hit-stun ───────────────────────────────────
                if (aiState.ValueRO.State == UnitState.Hit) continue;

                // ── No waypoints → stay Idle ───────────────────────────────
                var waypointBuf = SystemAPI.GetBuffer<PatrolWaypoint>(entity);
                if (waypointBuf.Length == 0)
                {
                    if (aiState.ValueRO.State != UnitState.Idle)
                    {
                        aiState.ValueRW.State = UnitState.Idle;
                        aiState.ValueRW.StateTimer = 0f;
                    }
                    continue;
                }

                int wpCount = waypointBuf.Length;
                int idx = math.clamp(patrol.ValueRO.CurrentWaypointIndex, 0, wpCount - 1);
                float3 target = waypointBuf[idx].Position;
                float3 myPos = transform.ValueRO.Position;
                float dist = math.distance(
                    new float3(myPos.x, 0f, myPos.z),
                    new float3(target.x, 0f, target.z));   // flat-plane distance

                // ── Waiting at waypoint ────────────────────────────────────
                if (patrol.ValueRO.WaitTimer > 0f)
                {
                    patrol.ValueRW.WaitTimer -= dt;

                    // Ensure navigation is stopped while waiting
                    if (aiState.ValueRO.State != UnitState.Idle)
                    {
                        aiState.ValueRW.State = UnitState.Idle;
                        aiState.ValueRW.StateTimer = 0f;
                        ecb.SetComponentEnabled<NavigationStopCommand>(entity, true);
                    }

                    // When timer expires, advance and immediately issue move command
                    if (patrol.ValueRW.WaitTimer <= 0f)
                    {
                        patrol.ValueRW.WaitTimer = 0f;
                        AdvanceWaypoint(ref patrol.ValueRW, wpCount);
                        IssueMove(ref aiState.ValueRW, entity,
                                  waypointBuf[patrol.ValueRO.CurrentWaypointIndex].Position,
                                  ecb);
                    }
                    continue;
                }

                // ── Arrived at current waypoint ────────────────────────────
                if (dist <= patrol.ValueRO.ArrivalRadius)
                {
                    // Start wait timer (even if 0 — will advance next frame)
                    patrol.ValueRW.WaitTimer = patrol.ValueRO.WaitDuration;

                    if (patrol.ValueRO.WaitDuration <= 0f)
                    {
                        // No wait — advance immediately and issue command
                        AdvanceWaypoint(ref patrol.ValueRW, wpCount);
                        IssueMove(ref aiState.ValueRW, entity,
                                  waypointBuf[patrol.ValueRO.CurrentWaypointIndex].Position,
                                  ecb);
                    }
                    else
                    {
                        // Stop and wait
                        aiState.ValueRW.State = UnitState.Idle;
                        aiState.ValueRW.StateTimer = 0f;
                        ecb.SetComponentEnabled<NavigationStopCommand>(entity, true);
                    }
                    continue;
                }

                // ── Still moving toward waypoint ───────────────────────────
                // Only issue a new move command if we aren't already en-route.
                // (Avoids re-triggering A* every single frame.)
                if (aiState.ValueRO.State != UnitState.Moving)
                {
                    IssueMove(ref aiState.ValueRW, entity, target, ecb);
                }
            }

            ecb.Playback(EntityManager);
            ecb.Dispose();
        }

        // ── Helpers ────────────────────────────────────────────────────────

        private static void IssueMove(ref AIState aiState, Entity entity,
                                       float3 destination,
                                       EntityCommandBuffer ecb)
        {
            aiState.State = UnitState.Moving;
            aiState.StateTimer = 0f;

            ecb.SetComponent(entity, new NavigationMoveCommand
            {
                Destination = destination,
                Priority = 0          // Lower priority than combat orders (priority 1)
            });
            ecb.SetComponentEnabled<NavigationMoveCommand>(entity, true);
        }

        /// <summary>
        /// Increments CurrentWaypointIndex according to the patrol mode.
        /// Mutates patrol.RandomSeed in-place for the Random mode.
        /// </summary>
        private static void AdvanceWaypoint(ref PatrolData patrol, int wpCount)
        {
            if (wpCount <= 1) { patrol.CurrentWaypointIndex = 0; return; }

            switch (patrol.Mode)
            {
                case PatrolMode.Loop:
                    patrol.CurrentWaypointIndex = (patrol.CurrentWaypointIndex + 1) % wpCount;
                    break;

                case PatrolMode.PingPong:
                    int next = patrol.CurrentWaypointIndex + patrol.PingPongDirection;
                    if (next >= wpCount) { next = wpCount - 2; patrol.PingPongDirection = -1; }
                    else if (next < 0) { next = 1; patrol.PingPongDirection = 1; }
                    patrol.CurrentWaypointIndex = math.clamp(next, 0, wpCount - 1);
                    break;

                case PatrolMode.Random:
                    // Simple LCG — deterministic, Burst-safe, no managed Random
                    patrol.RandomSeed = patrol.RandomSeed * 1664525u + 1013904223u;
                    int rng = (int)(patrol.RandomSeed >> 1) % wpCount;
                    // Avoid picking the same waypoint twice in a row
                    if (rng == patrol.CurrentWaypointIndex)
                        rng = (rng + 1) % wpCount;
                    patrol.CurrentWaypointIndex = rng;
                    break;
            }
        }
    }
}