// =============================================================================
// COMPATIBILITY SHIM — ChunkManagerSystem
//
// The chunk streaming / baking logic has been promoted to Shek.ECSGrid so the
// grid can be used independently of navigation (buildings, fog-of-war, etc.).
//
// This file keeps the Shek.ECSNavigation.ChunkManagerSystem name alive so
// existing code that references it continues to compile without changes.
//
// All real work is delegated to Shek.ECSGrid.GridManagerSystem.
// The coordinate utility methods are forwarded to GridManagerSystem too.
// =============================================================================

using Unity.Entities;
using Unity.Mathematics;

using Shek.ECSGrid;

namespace Shek.ECSNavigation
{
    /// <summary>
    /// Thin shim. All chunk management is performed by
    /// <see cref="Shek.ECSGrid.GridManagerSystem"/>; this class exposes the
    /// same static coordinate utilities under the original namespace so
    /// AStarSystem, FlowFieldSystem, and NavigationDispatchSystem compile
    /// without modification.
    /// </summary>
    public static class ChunkManagerSystem
    {
        // ── Coordinate utilities (forwarded to GridManagerSystem) ─────────────

        public static float3 ChunkCoordToWorld(int2 coord, NavigationConfig config)
        {
            // Reconstruct a minimal GridConfig just for the math (no allocation).
            var gc = ToGridConfig(config);
            return GridManagerSystem.ChunkCoordToWorld(coord, gc);
        }

        public static int2 WorldToChunkCoord(float3 worldPos, NavigationConfig config)
            => GridManagerSystem.WorldToChunkCoord(worldPos, ToGridConfig(config));

        public static int2 WorldToCellLocal(float3 worldPos, int2 chunkCoord,
                                             NavigationConfig config)
            => GridManagerSystem.WorldToCellLocal(worldPos, chunkCoord, ToGridConfig(config));

        public static int CellLocalToIndex(int2 localCell, int chunkCellCount)
            => GridManagerSystem.CellLocalToIndex(localCell, chunkCellCount);

        // ── Conversion helper ─────────────────────────────────────────────────

        /// <summary>
        /// Creates a GridConfig from the fields mirrored into NavigationConfig.
        /// Only the geometry fields are needed for coordinate math.
        /// </summary>
        private static GridConfig ToGridConfig(NavigationConfig nav) => new GridConfig
        {
            CellSize       = nav.CellSize,
            ChunkCellCount = nav.ChunkCellCount,
        };
    }
}
