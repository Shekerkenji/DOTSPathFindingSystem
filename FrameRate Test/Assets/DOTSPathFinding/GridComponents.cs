using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;


    // ─────────────────────────────────────────────
    //  Grid Config
    // ─────────────────────────────────────────────

    /// <summary>
    /// Singleton config — baked once via NavGridConfigAuthoring.
    /// No physics, no layer masks. The grid is purely logical:
    /// every cell is walkable by default; you mark cells occupied
    /// when you place a building/obstacle via NavGridOccupancyAPI.
    /// </summary>
    public struct NavGridConfig : IComponentData
    {
        /// <summary>World-space width and depth of one cell.</summary>
        public float CellSize;
    }

    // ─────────────────────────────────────────────
    //  Grid Cell
    // ─────────────────────────────────────────────

    /// <summary>
    /// Only non-default cells are stored in the sparse map.
    /// A cell absent from the map = open ground = walkable.
    /// A cell present in the map = either occupied or has a special movement cost.
    /// </summary>
    public struct GridCell
    {
        /// <summary>
        /// True when a building, wall, tree, or any impassable object occupies this cell.
        /// Set via NavGridOccupancyAPI.Occupy / .Free.
        /// </summary>
        public bool IsOccupied;

        /// <summary>
        /// Optional movement cost multiplier (1 = normal road/grass, 2 = mud, 3 = shallow water…).
        /// Set via NavGridOccupancyAPI.SetCost.
        /// </summary>
        public byte MovementCost;
    }

    // ─────────────────────────────────────────────
    //  Sparse Grid Singleton
    // ─────────────────────────────────────────────

    /// <summary>
    /// Managed singleton — class so the NativeHashMap lifetime is explicit.
    ///
    /// Sparse: only cells that differ from the default (walkable, cost=1) are stored.
    /// For a 10 000 × 10 000 map with 10 % obstacles that is ~10 M entries
    /// instead of 100 M — roughly a 10× memory saving.
    /// </summary>
    public class NavGridSingleton : IComponentData
    {
        /// <summary>Key = grid coordinate.  Value = cell data.</summary>
        public NativeHashMap<int2, GridCell> Cells;
        public float CellSize;

        public bool IsCreated => Cells.IsCreated;

        public void Dispose()
        {
            if (Cells.IsCreated) Cells.Dispose();
        }

        // ── Coordinate helpers ──────────────────────────────────────────────

        public int2 WorldToGrid(float3 worldPos) => new int2(
            (int)math.floor(worldPos.x / CellSize),
            (int)math.floor(worldPos.z / CellSize));

        public float3 GridToWorld(int2 coord) => new float3(
            (coord.x + 0.5f) * CellSize,
            0f,
            (coord.y + 0.5f) * CellSize);

        // ── Walkability ─────────────────────────────────────────────────────

        /// <summary>
        /// A cell not in the map is walkable (default open world).
        /// A cell in the map is walkable only if IsOccupied == false.
        /// </summary>
        public bool IsWalkable(int2 coord)
        {
            if (!Cells.TryGetValue(coord, out var cell)) return true;
            return !cell.IsOccupied;
        }

        public bool TryGetCell(int2 coord, out GridCell cell)
            => Cells.TryGetValue(coord, out cell);

        // ── Internal mutators (called by NavGridOccupancyAPI only) ──────────

        internal void SetOccupied(int2 coord, bool occupied)
        {
            if (Cells.TryGetValue(coord, out var cell))
            {
                cell.IsOccupied  = occupied;
                Cells[coord]     = cell;

                // If cell is back to default (walkable, cost=1) remove it to stay sparse
                if (!cell.IsOccupied && cell.MovementCost <= 1)
                    Cells.Remove(coord);
            }
            else if (occupied)
            {
                Cells.TryAdd(coord, new GridCell { IsOccupied = true, MovementCost = 1 });
            }
            // occupied==false and cell not present → already walkable, nothing to do
        }

        internal void SetMovementCost(int2 coord, byte cost)
        {
            if (Cells.TryGetValue(coord, out var cell))
            {
                cell.MovementCost = cost;
                Cells[coord]      = cell;
                if (!cell.IsOccupied && cell.MovementCost <= 1)
                    Cells.Remove(coord);
            }
            else if (cost > 1)
            {
                Cells.TryAdd(coord, new GridCell { IsOccupied = false, MovementCost = cost });
            }
        }
    }

