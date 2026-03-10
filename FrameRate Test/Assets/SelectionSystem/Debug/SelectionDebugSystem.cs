using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
//  SelectionDebugSystem.cs
//
//  DROP THIS IN YOUR PROJECT TEMPORARILY.
//  Prints exactly what is happening every frame a left-click occurs.
//  Remove or disable once movement is working.
// ─────────────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR

[WorldSystemFilter(WorldSystemFilterFlags.Default)]
[UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]  // runs AFTER everything else
public partial struct SelectionDebugSystem : ISystem
{
    public void OnCreate(ref SystemState state) { }
    public void OnDestroy(ref SystemState state) { }

    public void OnUpdate(ref SystemState state)
    {
        // ── Only log on left-click frames to avoid spam ───────────────────────
        if (!Input.GetMouseButtonDown(0)) return;

        var hasInput = SystemAPI.ManagedAPI.HasSingleton<PlayerInputSingleton>();
        var hasSel = SystemAPI.ManagedAPI.HasSingleton<SelectionSingleton>();

        Debug.Log("══════════════════════════════════════════════════════");
        Debug.Log("[DEBUG] LEFT CLICK FRAME");

        // ── 1. Singleton existence ────────────────────────────────────────────
        Debug.Log($"[DEBUG] PlayerInputSingleton exists : {hasInput}");
        Debug.Log($"[DEBUG] SelectionSingleton exists   : {hasSel}");

        if (!hasInput || !hasSel)
        {
            Debug.LogError("[DEBUG] Missing singletons — PlayerInputGatherSystem may not have run OnCreate yet.");
            return;
        }

        var input = SystemAPI.ManagedAPI.GetSingleton<PlayerInputSingleton>();
        var sel = SystemAPI.ManagedAPI.GetSingleton<SelectionSingleton>();

        // ── 2. Input state ────────────────────────────────────────────────────
        Debug.Log($"[DEBUG] LeftClickThisFrame        : {input.LeftClickThisFrame}");
        Debug.Log($"[DEBUG] ClickConsumedBySelection  : {input.ClickConsumedBySelection}");
        Debug.Log($"[DEBUG] HitGround                 : {input.HitGround}");
        Debug.Log($"[DEBUG] GroundHitPoint            : {input.GroundHitPoint}");
        Debug.Log($"[DEBUG] RayOrigin                 : {input.RayOrigin}");
        Debug.Log($"[DEBUG] RayDirection              : {input.RayDirection}");
        Debug.Log($"[DEBUG] ShiftHeld                 : {input.ShiftHeld}");

        if (!input.LeftClickThisFrame)
            Debug.LogWarning("[DEBUG] LeftClickThisFrame=false by the time DebugSystem runs — input may have been consumed or reset too early.");

        if (!input.HitGround)
            Debug.LogWarning("[DEBUG] HitGround=false. Is your ground mesh/plane at y=0? " +
                             "If not, adjust the ground plane Y in PlayerInputGatherSystem.");

        if (input.ClickConsumedBySelection)
            Debug.LogWarning("[DEBUG] ClickConsumedBySelection=true — UnitSelectionSystem thinks you clicked a unit. " +
                             "Check ray-vs-cylinder radius: Unit.Size may be too large.");

        // ── 3. Selection state ────────────────────────────────────────────────
        Debug.Log($"[DEBUG] Selected entity count     : {sel.SelectedEntities.Length}");

        for (int i = 0; i < sel.SelectedEntities.Length; i++)
        {
            Entity e = sel.SelectedEntities[i];
            bool exists = SystemAPI.Exists(e);
            bool selEnabled = exists && SystemAPI.IsComponentEnabled<Selected>(e);
            bool hasPathReq = exists && SystemAPI.HasComponent<PathRequest>(e);
            bool hasNavAgent = exists && SystemAPI.HasComponent<NavAgent>(e);
            bool hasTransform = exists && SystemAPI.HasComponent<LocalTransform>(e);
            bool hasWaypoints = exists && SystemAPI.HasBuffer<PathWaypoint>(e);
            bool hasGrpOrder = exists && SystemAPI.HasComponent<GroupMoveOrder>(e);
            bool hasBigOrder = exists && SystemAPI.HasComponent<BigGroupMoveOrder>(e);

            bool pathReqEnabled = hasPathReq && SystemAPI.IsComponentEnabled<PathRequest>(e);
            bool grpOrderEnabled = hasGrpOrder && SystemAPI.IsComponentEnabled<GroupMoveOrder>(e);
            bool bigOrderEnabled = hasBigOrder && SystemAPI.IsComponentEnabled<BigGroupMoveOrder>(e);

            NavAgentStatus agentStatus = default;
            float3 agentDest = default;
            if (hasNavAgent)
            {
                var agent = SystemAPI.GetComponent<NavAgent>(e);
                agentStatus = agent.Status;
                agentDest = agent.Destination;
            }

            float3 pos = default;
            if (hasTransform)
                pos = SystemAPI.GetComponent<LocalTransform>(e).Position;

            int waypointCount = 0;
            if (hasWaypoints)
                waypointCount = SystemAPI.GetBuffer<PathWaypoint>(e).Length;

            Debug.Log($"[DEBUG]   Entity[{i}] = {e}\n" +
                      $"          Exists={exists}  Selected(enabled)={selEnabled}\n" +
                      $"          Position={pos}\n" +
                      $"          HasPathRequest={hasPathReq}  PathRequest(enabled)={pathReqEnabled}\n" +
                      $"          HasNavAgent={hasNavAgent}  Status={agentStatus}  Destination={agentDest}\n" +
                      $"          HasLocalTransform={hasTransform}\n" +
                      $"          HasPathWaypointBuffer={hasWaypoints}  WaypointCount={waypointCount}\n" +
                      $"          HasGroupMoveOrder={hasGrpOrder}  GroupMoveOrder(enabled)={grpOrderEnabled}\n" +
                      $"          HasBigGroupMoveOrder={hasBigOrder}  BigGroupMoveOrder(enabled)={bigOrderEnabled}");

            // ── Per-entity diagnosis ──────────────────────────────────────────
            if (!exists)
                Debug.LogError($"[DEBUG]   Entity[{i}] does not exist — stale handle in SelectionSingleton.");
            else if (!selEnabled)
                Debug.LogWarning($"[DEBUG]   Entity[{i}] is in SelectedEntities list but Selected component is DISABLED.");
            else if (!hasPathReq && !hasGrpOrder && !hasBigOrder)
                Debug.LogError($"[DEBUG]   Entity[{i}] has NONE of: PathRequest, GroupMoveOrder, BigGroupMoveOrder. " +
                               "Add ECSNavAgentAuthoring to the GameObject and rebake the SubScene.");
            else if (!hasNavAgent)
                Debug.LogWarning($"[DEBUG]   Entity[{i}] has no NavAgent component. " +
                                 "ECSNavAgentAuthoring may be missing.");
            else if (!hasTransform)
                Debug.LogWarning($"[DEBUG]   Entity[{i}] has no LocalTransform. " +
                                 "Baker TransformUsageFlags.Dynamic may be missing.");
            else if (!hasWaypoints)
                Debug.LogWarning($"[DEBUG]   Entity[{i}] has no PathWaypoint buffer. " +
                                 "AddBuffer<PathWaypoint>(e) missing from baker.");
        }

        // ── 4. NavGrid state ──────────────────────────────────────────────────
        bool hasGrid = false;
        bool gridCreated = false;
        float cellSize = 0f;
        int cellCount = 0;

        foreach (var g in SystemAPI.Query<NavGridSingleton>())
        {
            hasGrid = true;
            gridCreated = g.IsCreated;
            cellSize = g.CellSize;
            cellCount = g.Cells.IsCreated ? g.Cells.Capacity : -1;
            break;
        }

        Debug.Log($"[DEBUG] NavGridSingleton exists   : {hasGrid}");
        Debug.Log($"[DEBUG] NavGrid.IsCreated         : {gridCreated}");
        Debug.Log($"[DEBUG] NavGrid.CellSize          : {cellSize}");
        Debug.Log($"[DEBUG] NavGrid occupied cells    : {cellCount}");

        if (!hasGrid || !gridCreated)
            Debug.LogError("[DEBUG] NavGrid is missing or not created — NavGridBuildSystem has not run. " +
                           "Is NavGridConfigAuthoring in your SubScene?");

        // ── 5. Check if destination is walkable ───────────────────────────────
        if (hasGrid && gridCreated && input.HitGround)
        {
            foreach (var g in SystemAPI.Query<NavGridSingleton>())
            {
                var coord = g.WorldToGrid(input.GroundHitPoint);
                bool walkable = g.IsWalkable(coord);
                Debug.Log($"[DEBUG] GroundHitPoint grid coord : {coord}  Walkable={walkable}");
                if (!walkable)
                    Debug.LogWarning("[DEBUG] Destination cell is NOT walkable — A* will fail. " +
                                     "PathRequestSystem will try to find nearest walkable cell.");
                break;
            }
        }

        Debug.Log("══════════════════════════════════════════════════════");
    }
}

#endif