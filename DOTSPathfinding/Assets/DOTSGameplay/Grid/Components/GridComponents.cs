using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;

namespace Shek.ECSGrid
{
    // =========================================================================
    // GRID CONFIGURATION
    // =========================================================================

    /// <summary>
    /// Singleton. Baked by GridConfigAuthoring. Shared by all grid consumers
    /// (navigation, buildings, fog-of-war, resource placement, etc.).
    /// </summary>
    public struct GridConfig : IComponentData
    {
        public float CellSize;
        public int   ChunkCellCount;

        // Streaming rings — how many chunks to keep alive around each anchor.
        public int ActiveRingRadius;
        public int GhostRingRadius;

        // Physics bake settings
        public float AgentRadius;
        public int   UnwalkablePhysicsLayer;
        public int   GroundPhysicsLayer;
        public float MaxSlopeAngle;
        public float BakeRaycastHeight;
    }

    // =========================================================================
    // PER-NODE DATA
    // =========================================================================

    /// <summary>
    /// Baked once at chunk-load time. Immutable thereafter (stored in a BlobAsset).
    /// WalkableLayerMask == 0 → physically blocked (wall, cliff, no ground).
    /// SlopeFlags == 1       → slope too steep for ground units.
    /// TerrainCostMask       → terrain tier index into TerrainCostTable.
    /// </summary>
    public struct NodeStatic
    {
        public byte WalkableLayerMask;
        public byte TerrainCostMask;
        public byte SlopeFlags;
        public byte Reserved;
    }

    /// <summary>
    /// Runtime mutable state for a single cell.
    /// Updated each frame by GridCellWriteSystem after processing CellRequests.
    ///
    /// OccupancyCount  – how many occupants (units, buildings) currently claim this cell.
    /// BlockFlags      – bitmask of runtime-imposed block reasons:
    ///                     bit 0 = building/structure placed here
    ///                     bit 1 = dynamic obstacle (vehicle, crate, etc.)
    ///                     bit 2–7 = reserved for game-specific uses
    /// </summary>
    public struct NodeDynamic
    {
        public byte OccupancyCount;
        public byte BlockFlags;
        public short Reserved;

        /// <summary>True if this cell is passable at runtime (not over-occupied or flagged).</summary>
        public bool IsPassable => OccupancyCount == 0 && BlockFlags == 0;
    }

    // =========================================================================
    // CHUNK STATE
    // =========================================================================

    public enum ChunkState : byte
    {
        Unloaded = 0,
        Ghost    = 1,   // Static data baked, dynamic data absent (pathfinding can sample)
        Active   = 2    // Static + dynamic data present (full simulation)
    }

    /// <summary>ECS component on every chunk entity.</summary>
    public struct GridChunk : IComponentData
    {
        public int2       ChunkCoord;
        public ChunkState State;
        public byte       StaticDataReady;  // 1 once BlobAsset is baked and valid
    }

    // =========================================================================
    // BLOB ASSETS (immutable, Burst-safe)
    // =========================================================================

    public struct ChunkStaticBlob
    {
        public BlobArray<NodeStatic> Nodes;
        public int2                  ChunkCoord;
        public int                   CellCount;
        /// <summary>
        /// 8-direction macro-connectivity costs for chunk-level A* (0 = blocked).
        /// Order: N, NE, E, SE, S, SW, W, NW.
        /// </summary>
        public BlobArray<byte>       MacroConnectivity;
    }

    // =========================================================================
    // CHUNK COMPONENTS
    // =========================================================================

    public struct ChunkStaticData : IComponentData
    {
        public BlobAssetReference<ChunkStaticBlob> Blob;
    }

    public struct ChunkDynamicData : IComponentData
    {
        public NativeArray<NodeDynamic> Nodes;
        public byte IsAllocated;
    }

    // =========================================================================
    // STREAMING ANCHOR
    // =========================================================================

    /// <summary>
    /// Add to any entity that should drive chunk streaming: player, squads,
    /// cameras, AI commanders, POIs…
    /// Priority 1 = normal active radius, 2 = double radius, etc.
    /// </summary>
    [ChunkSerializable]
    public struct StreamingAnchor : IComponentData
    {
        public float3 WorldPosition;
        public int2   CurrentChunkCoord;
        public int    Priority;
    }

    // =========================================================================
    // CHUNK LIFECYCLE REQUEST
    // =========================================================================

    public struct ChunkTransitionRequest : IComponentData, IEnableableComponent
    {
        public int2       ChunkCoord;
        public ChunkState TargetState;
    }

    // =========================================================================
    // RUNTIME CELL MODIFICATION API
    // =========================================================================

    /// <summary>
    /// Operation type for a CellRequest.
    /// </summary>
    public enum CellRequestType : byte
    {
        /// <summary>Increment OccupancyCount by 1.</summary>
        Occupy    = 0,
        /// <summary>Decrement OccupancyCount by 1 (floors at 0).</summary>
        Vacate    = 1,
        /// <summary>Set a bit in BlockFlags (arg = bit index 0–7).</summary>
        SetBlock  = 2,
        /// <summary>Clear a bit in BlockFlags (arg = bit index 0–7).</summary>
        ClearBlock = 3,
        /// <summary>Force-set OccupancyCount to 0 and BlockFlags to 0.</summary>
        Reset     = 4,
    }

    /// <summary>
    /// Enqueue one of these (via GridCellRequestBuffer on the GridManager singleton)
    /// to mutate NodeDynamic data at runtime.
    ///
    /// Workflow (any system or MonoBehaviour):
    ///
    ///   // Mark cells under a newly placed building as blocked.
    ///   var buf = SystemAPI.GetSingletonBuffer&lt;GridCellRequest&gt;();
    ///   foreach (int2 cell in buildingFootprint)
    ///       buf.Add(new GridCellRequest
    ///       {
    ///           WorldPosition = CellCentreWorld(cell),
    ///           Type          = CellRequestType.SetBlock,
    ///           Arg           = 0  // bit 0 = building
    ///       });
    ///
    /// GridCellWriteSystem processes all requests each frame and clears the buffer.
    /// Changes are visible to A* / FlowField the same frame (they run after the write system).
    /// </summary>
    public struct GridCellRequest : IBufferElementData
    {
        /// <summary>Any world position inside the target cell is fine.</summary>
        public float3          WorldPosition;
        public CellRequestType Type;
        /// <summary>Bit index for SetBlock/ClearBlock; ignored for other types.</summary>
        public byte            Arg;
    }

    // =========================================================================
    // TERRAIN COST TABLE
    // =========================================================================

    public struct TerrainCostTable : IComponentData
    {
        public BlobAssetReference<TerrainCostBlob> Blob;
    }

    public struct TerrainCostBlob
    {
        public BlobArray<int> Costs;
    }

    // =========================================================================
    // GRID MANAGER TAG  (singleton entity marker)
    // =========================================================================

    /// <summary>
    /// Tag placed on the GridManager singleton entity.
    /// GridCellRequest buffer and TerrainCostTable also live on this entity.
    /// </summary>
    public struct GridManagerTag : IComponentData { }
}
