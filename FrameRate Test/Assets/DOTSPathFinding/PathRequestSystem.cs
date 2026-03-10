using Unity.Entities;
using Unity.Mathematics;

[WorldSystemFilter(WorldSystemFilterFlags.Default)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(GroupMoveOrderSystem))]
[UpdateBefore(typeof(AStarPathfindingSystem))]
public partial struct PathRequestSystem : ISystem
{
    public void OnCreate(ref SystemState state)
        => state.RequireForUpdate<NavGridConfig>();

    public void OnUpdate(ref SystemState state)
    {
        NavGridSingleton grid = null;
        foreach (var g in SystemAPI.Query<NavGridSingleton>()) { grid = g; break; }
        if (grid == null || !grid.IsCreated) return;

        // Query only entities whose PathRequest is currently enabled.
        foreach (var (request, agent, waypoints, entity) in
            SystemAPI.Query<RefRW<PathRequest>, RefRW<NavAgent>, DynamicBuffer<PathWaypoint>>()
                     .WithAll<PathRequest>()
                     .WithEntityAccess())
        {
            // Already moving to same destination — skip, disable request
            if (agent.ValueRO.Status == NavAgentStatus.Moving &&
                math.distancesq(agent.ValueRO.Destination, request.ValueRO.End) < 0.01f)
            {
                SystemAPI.SetComponentEnabled<PathRequest>(entity, false);
                continue;
            }

            // Clamp destination to nearest walkable cell
            float3 end = NearestWalkable(grid, request.ValueRO.End);
            var r = request.ValueRW; r.End = end; request.ValueRW = r;

            waypoints.Clear();

            var a = agent.ValueRW;
            a.Destination = end; a.Status = NavAgentStatus.Requesting; a.CurrentPathIndex = 0;
            agent.ValueRW = a;

            // PathRequest stays enabled — AStarPathfindingSystem will pick it up next
        }
    }

    private static float3 NearestWalkable(NavGridSingleton grid, float3 pos)
    {
        int2 coord = grid.WorldToGrid(pos);
        if (grid.IsWalkable(coord)) return pos;
        for (int r = 1; r <= 8; r++)
            for (int dx = -r; dx <= r; dx++)
                for (int dz = -r; dz <= r; dz++)
                {
                    if (math.abs(dx) != r && math.abs(dz) != r) continue;
                    var c = coord + new int2(dx, dz);
                    if (grid.IsWalkable(c)) return grid.GridToWorld(c);
                }
        return pos;
    }
}