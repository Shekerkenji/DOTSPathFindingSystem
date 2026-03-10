#if UNITY_EDITOR
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

// ?????????????????????????????????????????????????????????????????????????????
//  AStarDebugSystem.cs  — TEMPORARY, remove once movement works
//
//  Runs AFTER AStarPathfindingSystem and NavAgentMoveSystem to report the
//  final state of every agent each frame they have an active path request
//  or just received a path result.
// ?????????????????????????????????????????????????????????????????????????????

[WorldSystemFilter(WorldSystemFilterFlags.Default)]
[UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
[UpdateAfter(typeof(NavAgentMoveSystem))]
public partial struct AStarDebugSystem : ISystem
{
    // Only log for N frames after a PathRequest is issued to avoid spam
    private int _logCountdown;

    public void OnCreate(ref SystemState state) { }
    public void OnDestroy(ref SystemState state) { }

    public void OnUpdate(ref SystemState state)
    {
        bool anyRequesting = false;

        foreach (var (agent, waypoints, entity) in
            SystemAPI.Query<RefRO<NavAgent>, DynamicBuffer<PathWaypoint>>()
                     .WithEntityAccess())
        {
            var status = agent.ValueRO.Status;

            // Only care about non-idle agents
            if (status == NavAgentStatus.Idle) continue;

            anyRequesting = true;

            bool pathReqEnabled = SystemAPI.IsComponentEnabled<PathRequest>(entity);
            bool pathReadyEnabled = SystemAPI.IsComponentEnabled<PathReady>(entity);
            bool pathFailEnabled = SystemAPI.IsComponentEnabled<PathFailed>(entity);

            Debug.Log($"[AStarDebug] Entity={entity}\n" +
                      $"  Status={status}  WaypointCount={waypoints.Length}\n" +
                      $"  PathRequest(enabled)={pathReqEnabled}\n" +
                      $"  PathReady(enabled)={pathReadyEnabled}\n" +
                      $"  PathFailed(enabled)={pathFailEnabled}\n" +
                      $"  Destination={agent.ValueRO.Destination}\n" +
                      $"  CurrentPathIndex={agent.ValueRO.CurrentPathIndex}");

            if (pathFailEnabled)
                Debug.LogError($"[AStarDebug] PATH FAILED for {entity}! " +
                               $"Start was set by MoveCommandSystem. " +
                               $"Check that Start grid coord is walkable and " +
                               $"destination grid coord is walkable.");

            if (status == NavAgentStatus.Moving && waypoints.Length > 0)
            {
                Debug.Log($"[AStarDebug] First waypoint: {waypoints[0].Position}  " +
                          $"Last waypoint: {waypoints[waypoints.Length - 1].Position}");
            }

            if (status == NavAgentStatus.Requesting && !pathReqEnabled && !pathReadyEnabled && !pathFailEnabled)
            {
                Debug.LogError($"[AStarDebug] Entity stuck in Requesting status with no flags enabled! " +
                               $"AStarPathfindingSystem may not be seeing this entity. " +
                               $"Check that PathReady and PathFailed components are baked on this entity.");
            }
        }

        // Also check if PathRequest is enabled but agent is Idle (MoveCommand fired but agent wasn't updated)
        foreach (var (agent, entity) in
            SystemAPI.Query<RefRO<NavAgent>>()
                     .WithAll<PathRequest>()
                     .WithEntityAccess())
        {
            if (agent.ValueRO.Status == NavAgentStatus.Idle)
            {
                Debug.LogWarning($"[AStarDebug] PathRequest is ENABLED but NavAgent.Status=Idle on {entity}. " +
                                 $"MoveCommandSystem may not have updated the agent status.");
            }
        }
    }
}
#endif