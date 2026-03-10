using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Public API for updating cell occupancy at runtime.
/// Call these whenever you place, move, or remove a building/obstacle.
///
/// ── Examples ────────────────────────────────────────────────────────────────
///
///   // Building placed at world position, covering a 3×3 cell footprint:
///   NavGridOccupancyAPI.OccupyRect(world, buildingCenter, footprintWidth, footprintDepth);
///
///   // Building demolished — free the same cells:
///   NavGridOccupancyAPI.FreeRect(world, buildingCenter, footprintWidth, footprintDepth);
///
///   // Single tree placed:
///   NavGridOccupancyAPI.OccupyCell(world, treePosition);
///
///   // Road tile — mark lower movement cost:
///   NavGridOccupancyAPI.SetCost(world, roadCenter, cost: 1);   // 1 = fast
///   NavGridOccupancyAPI.SetCost(world, mudCenter,  cost: 3);   // 3 = slow
///
///   // Query from an ECS system:
///   NavGridOccupancyAPI.OccupyRect(ref grid, center, w, d);
///
/// ────────────────────────────────────────────────────────────────────────────
/// </summary>
public static class NavGridOccupancyAPI
{
    // ── Single cell ──────────────────────────────────────────────────────────

    public static void OccupyCell(World world, Vector3 worldPos)
        => ModifyCell(world, worldPos, occupied: true);

    public static void FreeCell(World world, Vector3 worldPos)
        => ModifyCell(world, worldPos, occupied: false);

    // ── Rect footprint (most common: buildings) ──────────────────────────────

    /// <summary>
    /// Mark all cells covered by a world-space rectangle as occupied.
    /// <paramref name="centre"/> is the world centre of the footprint.
    /// <paramref name="width"/> and <paramref name="depth"/> are world-space sizes (X and Z).
    /// </summary>
    public static void OccupyRect(World world, Vector3 centre, float width, float depth)
        => ModifyRect(world, centre, width, depth, occupied: true);

    public static void FreeRect(World world, Vector3 centre, float width, float depth)
        => ModifyRect(world, centre, width, depth, occupied: false);

    // ── Radius (trees, rocks, circular objects) ──────────────────────────────

    /// <summary>Mark all cells within <paramref name="radius"/> world units as occupied.</summary>
    public static void OccupyRadius(World world, Vector3 centre, float radius)
        => ModifyRadius(world, centre, radius, occupied: true);

    public static void FreeRadius(World world, Vector3 centre, float radius)
        => ModifyRadius(world, centre, radius, occupied: false);

    // ── Movement cost ────────────────────────────────────────────────────────

    /// <summary>
    /// Set movement cost for a rect area.
    /// cost=1 normal, cost=2 slow (mud), cost=3 very slow (shallow water) etc.
    /// </summary>
    public static void SetCostRect(World world, Vector3 centre, float width, float depth, byte cost)
    {
        var grid = GetGrid(world);
        if (grid == null) return;
        ForEachCellInRect(grid, ToFloat3(centre), width, depth,
            coord => grid.SetMovementCost(coord, cost));
    }

    public static void SetCost(World world, Vector3 worldPos, byte cost)
    {
        var grid = GetGrid(world);
        if (grid == null) return;
        grid.SetMovementCost(grid.WorldToGrid(ToFloat3(worldPos)), cost);
    }

    // ── ECS-system overloads (NavGridSingleton is a class — no ref needed) ────
    // NavGridSingleton is a managed class, so passing by value already passes
    // the reference. No ref keyword needed, and lambdas can capture it freely.

    public static void OccupyRect(NavGridSingleton grid, float3 centre, float width, float depth)
        => ForEachCellInRect(grid, centre, width, depth, c => grid.SetOccupied(c, true));

    public static void FreeRect(NavGridSingleton grid, float3 centre, float width, float depth)
        => ForEachCellInRect(grid, centre, width, depth, c => grid.SetOccupied(c, false));

    public static void OccupyRadius(NavGridSingleton grid, float3 centre, float radius)
        => ForEachCellInRadius(grid, centre, radius, c => grid.SetOccupied(c, true));

    public static void FreeRadius(NavGridSingleton grid, float3 centre, float radius)
        => ForEachCellInRadius(grid, centre, radius, c => grid.SetOccupied(c, false));

    // ── Internal helpers ─────────────────────────────────────────────────────

    private static void ModifyCell(World world, Vector3 worldPos, bool occupied)
    {
        var grid = GetGrid(world);
        if (grid == null) return;
        grid.SetOccupied(grid.WorldToGrid(ToFloat3(worldPos)), occupied);
    }

    private static void ModifyRect(World world, Vector3 centre, float width, float depth, bool occupied)
    {
        var grid = GetGrid(world);
        if (grid == null) return;
        ForEachCellInRect(grid, ToFloat3(centre), width, depth, c => grid.SetOccupied(c, occupied));
    }

    private static void ModifyRadius(World world, Vector3 centre, float radius, bool occupied)
    {
        var grid = GetGrid(world);
        if (grid == null) return;
        ForEachCellInRadius(grid, ToFloat3(centre), radius, c => grid.SetOccupied(c, occupied));
    }

    private static void ForEachCellInRect(
        NavGridSingleton grid,
        float3 centre, float width, float depth,
        System.Action<Unity.Mathematics.int2> action)
    {
        float hw = width * 0.5f;
        float hd = depth * 0.5f;
        var min = grid.WorldToGrid(centre - new float3(hw, 0, hd));
        var max = grid.WorldToGrid(centre + new float3(hw, 0, hd));
        for (int x = min.x; x <= max.x; x++)
            for (int z = min.y; z <= max.y; z++)
                action(new Unity.Mathematics.int2(x, z));
    }

    private static void ForEachCellInRadius(
        NavGridSingleton grid,
        float3 centre, float radius,
        System.Action<Unity.Mathematics.int2> action)
    {
        var min = grid.WorldToGrid(centre - new float3(radius, 0, radius));
        var max = grid.WorldToGrid(centre + new float3(radius, 0, radius));
        float3 gridCentre = centre;
        float r2 = radius * radius;
        float cs = grid.CellSize;

        for (int x = min.x; x <= max.x; x++)
            for (int z = min.y; z <= max.y; z++)
            {
                // Circle check in world space
                float wx = (x + 0.5f) * cs - gridCentre.x;
                float wz = (z + 0.5f) * cs - gridCentre.z;
                if (wx * wx + wz * wz <= r2)
                    action(new Unity.Mathematics.int2(x, z));
            }
    }

    // ── World → Grid singleton lookup ────────────────────────────────────────

    private static NavGridSingleton GetGrid(World world)
    {
        if (world == null || !world.IsCreated)
        {
            Debug.LogWarning("[NavGridOccupancyAPI] World is null or not created.");
            return null;
        }
        // NavGridSingleton is managed IComponentData — query via EntityQuery
        using var query = world.EntityManager.CreateEntityQuery(
            ComponentType.ReadOnly<NavGridSingleton>());

        if (query.IsEmpty)
        {
            Debug.LogWarning("[NavGridOccupancyAPI] NavGridSingleton not found. " +
                             "Has NavGridBuildSystem run yet?");
            return null;
        }
        return query.GetSingleton<NavGridSingleton>();
    }

    private static float3 ToFloat3(Vector3 v) => new float3(v.x, v.y, v.z);
}