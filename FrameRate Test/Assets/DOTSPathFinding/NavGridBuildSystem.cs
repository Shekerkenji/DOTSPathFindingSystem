using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using Unity.Mathematics;

/// <summary>
/// Creates the NavGridSingleton on startup — that's all.
/// No raycasting, no physics, no layer queries.
///
/// The grid starts completely empty (all cells walkable by default).
/// Obstacles are registered at runtime via NavGridOccupancyAPI.
/// </summary>
[WorldSystemFilter(WorldSystemFilterFlags.Default)]
[UpdateInGroup(typeof(InitializationSystemGroup))]
public partial struct NavGridBuildSystem : ISystem
{
    private bool _built;

    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<NavGridConfig>();
        _built = false;
    }

    public void OnUpdate(ref SystemState state)
    {
        if (_built) return;
        _built = true;

        var config = SystemAPI.GetSingleton<NavGridConfig>();

        var singleton = new NavGridSingleton
        {
            CellSize = config.CellSize,
            // Start with a modest capacity — the HashMap grows automatically.
            // Pre-size if you know your obstacle count upfront.
            Cells = new NativeHashMap<int2, GridCell>(1024, Allocator.Persistent),
        };

        var entity = state.EntityManager.CreateEntity();
        state.EntityManager.SetName(entity, "NavGridSingleton");
        state.EntityManager.AddComponentObject(entity, singleton);

        Debug.Log($"[NavGrid] Ready — cell size {config.CellSize}. " +
                  "All cells walkable by default. " +
                  "Use NavGridOccupancyAPI to mark obstacles.");
    }

    public void OnDestroy(ref SystemState state)
    {
        foreach (var g in SystemAPI.Query<NavGridSingleton>()) g.Dispose();
    }
}
