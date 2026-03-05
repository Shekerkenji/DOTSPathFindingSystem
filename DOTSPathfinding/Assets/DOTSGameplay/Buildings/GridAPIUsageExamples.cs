// =============================================================================
// GRID API USAGE EXAMPLES
// =============================================================================
//
// This file shows how non-navigation systems (buildings, units claiming tiles,
// fog-of-war, resource systems) interact with the grid without depending on
// anything in Shek.ECSNavigation.
//
// You do NOT need to add this file to your project — it is documentation only.
// =============================================================================

using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Burst;

using Shek.ECSGrid;

namespace Shek.Examples
{
    // ─────────────────────────────────────────────────────────────────────────
    // EXAMPLE 1: BUILDING PLACEMENT SYSTEM
    //
    // When a building is placed, mark every cell under its footprint as blocked.
    // When demolished, clear those cells.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Component added to building entities to record which cells they occupy.
    /// </summary>
    public struct BuildingOccupancy : IComponentData
    {
        /// <summary>World position of the building's pivot (bottom-left of footprint).</summary>
        public float3 FootprintOrigin;
        /// <summary>Footprint size in cells (width × depth).</summary>
        public int2   FootprintCells;
        /// <summary>True once the building has registered its cells.</summary>
        public byte   Registered;
    }

    /// <summary>Tag added when a building is demolished so cells can be freed.</summary>
    public struct BuildingDemolished : IComponentData, IEnableableComponent { }

    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial class BuildingOccupancySystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<GridConfig>();
            RequireForUpdate<GridManagerTag>();
        }

        protected override void OnUpdate()
        {
            var config        = SystemAPI.GetSingleton<GridConfig>();
            var managerEntity = SystemAPI.GetSingletonEntity<GridManagerTag>();
            var requests      = EntityManager.GetBuffer<GridCellRequest>(managerEntity);

            // ── Register newly placed buildings ───────────────────────────────
            foreach (var (occ, entity) in
                SystemAPI.Query<RefRW<BuildingOccupancy>>()
                    .WithEntityAccess())
            {
                if (occ.ValueRO.Registered == 1) continue;

                int2   cells  = occ.ValueRO.FootprintCells;
                float3 origin = occ.ValueRO.FootprintOrigin;

                for (int x = 0; x < cells.x; x++)
                for (int z = 0; z < cells.y; z++)
                {
                    float3 cellWorld = origin + new float3(
                        (x + 0.5f) * config.CellSize, 0f,
                        (z + 0.5f) * config.CellSize);

                    requests.Add(new GridCellRequest
                    {
                        WorldPosition = cellWorld,
                        Type          = CellRequestType.SetBlock,
                        Arg           = 0    // bit 0 = building/structure
                    });
                }

                occ.ValueRW.Registered = 1;
            }

            // ── Free cells when a building is demolished ───────────────────────
            foreach (var (occ, demolishedEnabled, entity) in
                SystemAPI.Query<RefRO<BuildingOccupancy>, EnabledRefRO<BuildingDemolished>>()
                    .WithEntityAccess())
            {
                if (!demolishedEnabled.ValueRO) continue;

                int2   cells  = occ.ValueRO.FootprintCells;
                float3 origin = occ.ValueRO.FootprintOrigin;

                for (int x = 0; x < cells.x; x++)
                for (int z = 0; z < cells.y; z++)
                {
                    float3 cellWorld = origin + new float3(
                        (x + 0.5f) * config.CellSize, 0f,
                        (z + 0.5f) * config.CellSize);

                    requests.Add(new GridCellRequest
                    {
                        WorldPosition = cellWorld,
                        Type          = CellRequestType.ClearBlock,
                        Arg           = 0    // clear the building bit
                    });
                }

                // Consume the tag so we only fire once
                SystemAPI.SetComponentEnabled<BuildingDemolished>(entity, false);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // EXAMPLE 2: UNIT RESERVATION SYSTEM
    //
    // Units increment OccupancyCount when they enter a cell and decrement when
    // they leave. A* / FlowField skip cells where OccupancyCount > 0.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Tracks which cell a unit currently occupies for reservation purposes.</summary>
    public struct UnitCellReservation : IComponentData
    {
        public int2   OccupiedCell;
        public int2   OccupiedChunk;
        public byte   HasReservation;
    }

    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial class UnitCellReservationSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<GridConfig>();
            RequireForUpdate<GridManagerTag>();
        }

        protected override void OnUpdate()
        {
            var config        = SystemAPI.GetSingleton<GridConfig>();
            var managerEntity = SystemAPI.GetSingletonEntity<GridManagerTag>();
            var requests      = EntityManager.GetBuffer<GridCellRequest>(managerEntity);

            foreach (var (reservation, transform) in
                SystemAPI.Query<RefRW<UnitCellReservation>,
                                RefRO<Unity.Transforms.LocalTransform>>())
            {
                float3 pos        = transform.ValueRO.Position;
                int2   newChunk   = GridManagerSystem.WorldToChunkCoord(pos, config);
                int2   newCell    = GridManagerSystem.WorldToCellLocal(pos, newChunk, config);
                float3 cellCentre = GridManagerSystem.CellLocalToWorld(newCell, newChunk, config);

                // Only update when the unit has moved to a new cell
                bool same = reservation.ValueRO.HasReservation == 1 &&
                            math.all(newCell  == reservation.ValueRO.OccupiedCell) &&
                            math.all(newChunk == reservation.ValueRO.OccupiedChunk);
                if (same) continue;

                // Release previous cell
                if (reservation.ValueRO.HasReservation == 1)
                {
                    float3 oldCentre = GridManagerSystem.CellLocalToWorld(
                        reservation.ValueRO.OccupiedCell,
                        reservation.ValueRO.OccupiedChunk,
                        config);
                    requests.Add(new GridCellRequest
                    {
                        WorldPosition = oldCentre,
                        Type          = CellRequestType.Vacate,
                    });
                }

                // Claim new cell
                requests.Add(new GridCellRequest
                {
                    WorldPosition = cellCentre,
                    Type          = CellRequestType.Occupy,
                });

                reservation.ValueRW.OccupiedCell  = newCell;
                reservation.ValueRW.OccupiedChunk = newChunk;
                reservation.ValueRW.HasReservation = 1;
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // EXAMPLE 3: DIRECT CHUNK / CELL QUERY FROM ANY SYSTEM
    //
    // Read grid state without going through the request buffer.
    // ─────────────────────────────────────────────────────────────────────────

    public partial class GridQueryExampleSystem : SystemBase
    {
        protected override void OnCreate() =>
            RequireForUpdate<GridConfig>();

        protected override void OnUpdate()
        {
            var config = SystemAPI.GetSingleton<GridConfig>();

            // ── Is a world position walkable right now? ────────────────────────
            float3 queryPoint  = new float3(10f, 0f, 15f);
            int2   queryChunk  = GridManagerSystem.WorldToChunkCoord(queryPoint, config);
            int2   queryCell   = GridManagerSystem.WorldToCellLocal(queryPoint, queryChunk, config);
            int    queryCellIdx = GridManagerSystem.CellLocalToIndex(queryCell, config.ChunkCellCount);

            bool staticWalkable  = false;
            bool dynamicPassable = true;

            foreach (var (chunk, staticData) in
                SystemAPI.Query<RefRO<GridChunk>, RefRO<ChunkStaticData>>())
            {
                if (!math.all(chunk.ValueRO.ChunkCoord == queryChunk)) continue;
                if (chunk.ValueRO.StaticDataReady == 0) break;
                ref ChunkStaticBlob blob = ref staticData.ValueRO.Blob.Value;
                staticWalkable = blob.Nodes[queryCellIdx].WalkableLayerMask != 0 &&
                                 blob.Nodes[queryCellIdx].SlopeFlags        == 0;
                break;
            }

            foreach (var (chunk, dynData) in
                SystemAPI.Query<RefRO<GridChunk>, RefRO<ChunkDynamicData>>())
            {
                if (!math.all(chunk.ValueRO.ChunkCoord == queryChunk)) continue;
                if (dynData.ValueRO.IsAllocated == 0) break;
                dynamicPassable = dynData.ValueRO.Nodes[queryCellIdx].IsPassable;
                break;
            }

            bool fullyPassable = staticWalkable && dynamicPassable;
            // Use fullyPassable for building placement validation, fog-of-war, etc.
            _ = fullyPassable;
        }
    }
}
