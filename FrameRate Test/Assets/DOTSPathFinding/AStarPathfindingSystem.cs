using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

/// <summary>
/// A* pathfinding. Processes all entities with PathRequest enabled.
/// Uses enableable components (PathRequest / PathReady / PathFailed) instead of
/// structural add/remove, eliminating chunk moves every frame.
/// </summary>
[WorldSystemFilter(WorldSystemFilterFlags.Default)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateBefore(typeof(NavAgentMoveSystem))]
public partial struct AStarPathfindingSystem : ISystem
{
    private const int MaxIterations = 8192;

    public void OnCreate(ref SystemState state)
        => state.RequireForUpdate<NavGridConfig>();

    public void OnUpdate(ref SystemState state)
    {
        NavGridSingleton grid = null;
        foreach (var g in SystemAPI.Query<NavGridSingleton>()) { grid = g; break; }
        if (grid == null || !grid.IsCreated) return;

        // KEY FIX: removed .WithDisabled<PathReady>() — that filter silently
        // excluded entities that never had PathReady baked into their archetype.
        // We only require PathRequest to be enabled. PathReady/PathFailed are
        // optional — we check HasComponent before touching them.
        foreach (var (request, waypoints, entity) in
            SystemAPI.Query<
                RefRO<PathRequest>,
                DynamicBuffer<PathWaypoint>>()
                     .WithAll<PathRequest>()   // PathRequest must be enabled
                     .WithEntityAccess())
        {
            waypoints.Clear();

            bool found = RunAStar(
                grid,
                grid.WorldToGrid(request.ValueRO.Start),
                grid.WorldToGrid(request.ValueRO.End),
                waypoints);

            if (found)
            {
                if (SystemAPI.HasComponent<PathReady>(entity))
                    SystemAPI.SetComponentEnabled<PathReady>(entity, true);
                if (SystemAPI.HasComponent<PathFailed>(entity))
                    SystemAPI.SetComponentEnabled<PathFailed>(entity, false);
            }
            else
            {
                if (SystemAPI.HasComponent<PathFailed>(entity))
                    SystemAPI.SetComponentEnabled<PathFailed>(entity, true);
                if (SystemAPI.HasComponent<PathReady>(entity))
                    SystemAPI.SetComponentEnabled<PathReady>(entity, false);

#if UNITY_EDITOR
                UnityEngine.Debug.LogWarning(
                    $"[AStar] Path NOT FOUND for {entity}. " +
                    $"Start={request.ValueRO.Start} → grid{grid.WorldToGrid(request.ValueRO.Start)}, " +
                    $"End={request.ValueRO.End} → grid{grid.WorldToGrid(request.ValueRO.End)}. " +
                    $"Start walkable={grid.IsWalkable(grid.WorldToGrid(request.ValueRO.Start))}, " +
                    $"End walkable={grid.IsWalkable(grid.WorldToGrid(request.ValueRO.End))}");
#endif
            }

            // Disable PathRequest — work is done (non-structural)
            SystemAPI.SetComponentEnabled<PathRequest>(entity, false);
        }
    }

    // ── A* ────────────────────────────────────────────────────────────────────

    private static bool RunAStar(
        NavGridSingleton grid,
        int2 start,
        int2 goal,
        DynamicBuffer<PathWaypoint> outPath)
    {
        if (!grid.IsWalkable(goal)) return false;
        if (start.Equals(goal))
        {
            outPath.Add(new PathWaypoint { Position = grid.GridToWorld(goal) });
            return true;
        }

        var openHeap = new NativeList<Node>(256, Allocator.Temp);
        var gScore = new NativeHashMap<int2, float>(512, Allocator.Temp);
        var cameFrom = new NativeHashMap<int2, int2>(512, Allocator.Temp);
        var closed = new NativeHashSet<int2>(512, Allocator.Temp);

        gScore[start] = 0f;
        Push(ref openHeap, new Node { Coord = start, GCost = 0f, FCost = Heuristic(start, goal) });

        bool found = false;
        int iter = 0;

        while (openHeap.Length > 0 && iter++ < MaxIterations)
        {
            var cur = Pop(ref openHeap);
            if (closed.Contains(cur.Coord)) continue;
            closed.Add(cur.Coord);

            if (cur.Coord.Equals(goal)) { found = true; break; }

            for (int i = 0; i < 8; i++)
            {
                int2 nb = cur.Coord + Offset(i);
                if (closed.Contains(nb) || !grid.IsWalkable(nb)) continue;

                float moveCost = (i < 4) ? 1f : 1.414f;
                if (grid.TryGetCell(nb, out var cell) && cell.MovementCost > 1)
                    moveCost *= cell.MovementCost;

                float g = cur.GCost + moveCost;
                if (!gScore.TryGetValue(nb, out float eg) || g < eg)
                {
                    gScore[nb] = g;
                    cameFrom[nb] = cur.Coord;
                    Push(ref openHeap, new Node { Coord = nb, GCost = g, FCost = g + Heuristic(nb, goal) });
                }
            }
        }

        if (found)
        {
            var raw = new NativeList<int2>(64, Allocator.Temp);
            var c = goal;
            while (cameFrom.TryGetValue(c, out var prev)) { raw.Add(c); c = prev; }
            raw.Add(c);
            for (int i = raw.Length - 1; i >= 0; i--)
                outPath.Add(new PathWaypoint { Position = grid.GridToWorld(raw[i]) });
            raw.Dispose();
        }

        openHeap.Dispose(); gScore.Dispose(); cameFrom.Dispose(); closed.Dispose();
        return found;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static float Heuristic(int2 a, int2 b)
    {
        float dx = math.abs(a.x - b.x), dy = math.abs(a.y - b.y);
        return dx + dy + (1.414f - 2f) * math.min(dx, dy);
    }

    private static int2 Offset(int i) => i switch
    {
        0 => new int2(0, 1),
        1 => new int2(0, -1),
        2 => new int2(1, 0),
        3 => new int2(-1, 0),
        4 => new int2(1, 1),
        5 => new int2(1, -1),
        6 => new int2(-1, 1),
        _ => new int2(-1, -1),
    };

    private static void Push(ref NativeList<Node> h, Node n)
    {
        h.Add(n);
        int i = h.Length - 1;
        while (i > 0)
        {
            int p = (i - 1) / 2;
            if (h[p].FCost <= h[i].FCost) break;
            (h[p], h[i]) = (h[i], h[p]);
            i = p;
        }
    }

    private static Node Pop(ref NativeList<Node> h)
    {
        var top = h[0];
        h[0] = h[h.Length - 1];
        h.RemoveAt(h.Length - 1);
        int i = 0;
        while (true)
        {
            int l = 2 * i + 1, r = 2 * i + 2, s = i;
            if (l < h.Length && h[l].FCost < h[s].FCost) s = l;
            if (r < h.Length && h[r].FCost < h[s].FCost) s = r;
            if (s == i) break;
            (h[s], h[i]) = (h[i], h[s]);
            i = s;
        }
        return top;
    }

    private struct Node { public int2 Coord; public float GCost, FCost; }
}