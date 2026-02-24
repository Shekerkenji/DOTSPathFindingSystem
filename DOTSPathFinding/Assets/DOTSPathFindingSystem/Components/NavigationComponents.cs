using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;

namespace Navigation.ECS
{
    // ─────────────────────────────────────────────
    // GRID & CHUNK COMPONENTS
    // ─────────────────────────────────────────────

    /// <summary>
    /// Global navigation config singleton. One per world.
    /// </summary>
    public struct NavigationConfig : IComponentData
    {
        public float CellSize;           // World units per cell (e.g. 1.0f)
        public int ChunkCellCount;       // Cells per chunk side (e.g. 64 → 64x64 chunk)
        public int GhostRingRadius;      // How many chunks out from active to keep as ghost
        public int ActiveRingRadius;     // How many chunks around player are fully active
        public float AgentRadius;        // Used when baking slope/obstacle clearance
        public int UnwalkablePhysicsLayer;
        public int GroundPhysicsLayer;
        public float MaxSlopeAngle;      // Degrees — steeper = blocked for ground units
        public float BakeRaycastHeight;  // How high above cell centre to start downward raycast
    }

    /// <summary>
    /// Per-cell static data. Baked once, read-only at runtime.
    /// 4 bytes per node.
    /// </summary>
    public struct NodeStatic
    {
        public byte WalkableLayerMask;  // Bitmask — which unit layer types may enter
        public byte TerrainCostMask;    // Bitmask index into cost lookup table
        public byte SlopeFlags;         // 0 = flat, 1 = too steep for ground, 2 = partial
        public byte Reserved;           // Future use / padding
    }

    /// <summary>
    /// Per-cell dynamic data. Runtime only, only allocated for Active chunks.
    /// 4 bytes per node.
    /// </summary>
    public struct NodeDynamic
    {
        public byte OccupancyCount;     // How many units currently in this cell (saturates at 255)
        public byte DynamicBlockFlags;  // Runtime obstacles (destroyed buildings, etc)
        public short Reserved;
    }

    /// <summary>
    /// Chunk state enum.
    /// </summary>
    public enum ChunkState : byte
    {
        Unloaded = 0,
        Ghost = 1,   // Static walkability only, no simulation
        Active = 2    // Full data, units can exist here
    }

    /// <summary>
    /// Chunk component — one entity per chunk.
    /// </summary>
    public struct GridChunk : IComponentData
    {
        public int2 ChunkCoord;         // Chunk grid coordinate (not world space)
        public ChunkState State;
        public byte StaticDataReady;    // True once bake is complete
    }

    /// <summary>
    /// Blob asset for static chunk data. Lives on disk / is streamed.
    /// </summary>
    public struct ChunkStaticBlob
    {
        public BlobArray<NodeStatic> Nodes; // Length = ChunkCellCount * ChunkCellCount
        public int2 ChunkCoord;
        public int CellCount;               // Side length
        // Macro connectivity: 8 directions, each is a cost (0 = blocked)
        public BlobArray<byte> MacroConnectivity;
    }

    /// <summary>
    /// Reference to blob stored on the chunk entity.
    /// </summary>
    public struct ChunkStaticData : IComponentData
    {
        public BlobAssetReference<ChunkStaticBlob> Blob;
    }

    /// <summary>
    /// Dynamic chunk data — NativeArrays, only allocated when chunk is Active.
    /// Stored as a component on the chunk entity. Disposed when chunk deactivates.
    /// </summary>
    public struct ChunkDynamicData : IComponentData
    {
        public NativeArray<NodeDynamic> Nodes;
        public byte IsAllocated;
    }

    // ─────────────────────────────────────────────
    // UNIT / AGENT COMPONENTS
    // ─────────────────────────────────────────────

    /// <summary>
    /// Which terrain layers this unit type is allowed to enter.
    /// ANDed against NodeStatic.WalkableLayerMask at query time.
    /// </summary>
    [ChunkSerializable]
    public struct UnitLayerPermissions : IComponentData
    {
        public byte WalkableLayers;     // Bitmask
        public byte CostLayerWeights;   // Bitmask — which cost tiers to apply
        public byte IsFlying;           // True = use 3D 26-neighbour A*, false = 2D 8-neighbour
    }

    /// <summary>
    /// Navigation mode for this unit.
    /// </summary>
    public enum NavMode : byte
    {
        Idle = 0,
        AStar = 1,    // Individual pathfinding
        FlowField = 2,    // Following a shared flow field
        MacroOnly = 3     // Moving through ghost/unloaded chunks on macro path
    }

    /// <summary>
    /// Core navigation state per agent.
    /// </summary>
    [ChunkSerializable]
    public struct AgentNavigation : IComponentData
    {
        public float3 Destination;
        public float3 LastKnownPosition;
        public NavMode Mode;
        public int FlowFieldId;             // Which flow field to sample (-1 = none)
        public float RepathCooldown;        // Time until next repath allowed
        public float StuckTimer;
        public float ArrivalThreshold;      // Distance to destination = arrived
        public byte HasDestination;
    }

    /// <summary>
    /// Unit movement parameters.
    /// </summary>
    [ChunkSerializable]
    public struct UnitMovement : IComponentData
    {
        public float Speed;
        public float TurnSpeed;
        public float TurnDistance;      // Waypoint reach distance
        public int CurrentWaypointIndex;
        public byte IsFollowingPath;
    }

    /// <summary>
    /// Buffer of path waypoints from A* result. Per-agent.
    /// </summary>
    public struct PathWaypoint : IBufferElementData
    {
        public float3 Position;
    }

    /// <summary>
    /// Macro path through chunks. Used when crossing ghost/unloaded territory.
    /// </summary>
    public struct MacroWaypoint : IBufferElementData
    {
        public int2 ChunkCoord;
        public float3 WorldEntryPoint;
    }

    // ─────────────────────────────────────────────
    // FLOW FIELD COMPONENTS
    // ─────────────────────────────────────────────

    /// <summary>
    /// Singleton registry — maps flow field IDs to their chunk coverage.
    /// Actual field data lives on FlowField entities.
    /// </summary>
    public struct FlowFieldRegistry : IComponentData
    {
        public int NextId;
    }

    /// <summary>
    /// A flow field entity — one per destination (per chunk it covers).
    /// </summary>
    public struct FlowFieldData : IComponentData
    {
        public int FieldId;
        public int2 ChunkCoord;              // Which chunk this field tile covers
        public float3 Destination;
        public ulong DestinationHash;        // Quantized destination key
        public NativeArray<float2> Vectors;  // XZ direction per cell, length = cells²
        public NativeArray<int> Integration; // Cost from goal, used during build
        public byte IsReady;
        public float BuildTime;              // When it was last built
    }

    /// <summary>
    /// Tag: this agent is currently sampling a flow field.
    /// </summary>
    [ChunkSerializable]
    public struct FlowFieldFollower : IComponentData, IEnableableComponent
    {
        public int FieldId;
    }

    // ─────────────────────────────────────────────
    // PATHFINDING REQUEST / RESULT
    // ─────────────────────────────────────────────

    /// <summary>
    /// Queued A* request. Added to agent entity, processed by PathRequestSystem.
    /// </summary>
    [ChunkSerializable]
    public struct PathRequest : IComponentData, IEnableableComponent
    {
        public float3 Start;
        public float3 End;
        public int Priority;        // Higher = processed sooner this frame
        public float RequestTime;
    }

    /// <summary>
    /// Tag: pathfinding completed successfully this frame.
    /// </summary>
    public struct PathfindingSuccess : IComponentData, IEnableableComponent { }

    /// <summary>
    /// Tag: pathfinding failed (no path exists or timed out).
    /// </summary>
    public struct PathfindingFailed : IComponentData, IEnableableComponent { }

    /// <summary>
    /// Tag: needs a new path (destination changed, stuck, repath timer elapsed).
    /// </summary>
    public struct NeedsRepath : IComponentData, IEnableableComponent { }

    // ─────────────────────────────────────────────
    // A* INTERNALS (Temp-allocated per query, not stored in grid)
    // ─────────────────────────────────────────────

    /// <summary>
    /// A* node used only during query execution. Never stored in the grid.
    /// </summary>
    public struct AStarNode : System.IComparable<AStarNode>
    {
        public int Index;       // Flat index into chunk node array
        public int GCost;
        public int HCost;
        public int FCost => GCost + HCost;
        public int ParentIndex;

        public int CompareTo(AStarNode other)
        {
            int cmp = FCost.CompareTo(other.FCost);
            if (cmp == 0) cmp = HCost.CompareTo(other.HCost);
            return -cmp; // Inverted for min-heap behaviour
        }
    }

    // ─────────────────────────────────────────────
    // CHUNK STREAMING REQUESTS
    // ─────────────────────────────────────────────

    /// <summary>
    /// Request to transition a chunk from one state to another.
    /// </summary>
    public struct ChunkTransitionRequest : IComponentData, IEnableableComponent
    {
        public int2 ChunkCoord;
        public ChunkState TargetState;
    }

    /// <summary>
    /// Per-entity streaming anchor. Any entity can be an anchor — player, squad leader,
    /// cinematic camera, AI director, preloaded POI. Multiple anchors are fully supported.
    /// ChunkManagerSystem unions ALL anchor positions and loads chunks around each of them.
    /// Priority scales the active ring: activeRingRadius * Priority per anchor.
    /// </summary>
    [ChunkSerializable]
    public struct StreamingAnchor : IComponentData
    {
        public float3 WorldPosition;
        public int2 CurrentChunkCoord;
        public int Priority;  // 1 = normal ring, 2 = double active ring, etc.
    }

    // ─────────────────────────────────────────────
    // TERRAIN COST TABLE (Singleton)
    // ─────────────────────────────────────────────

    /// <summary>
    /// Lookup table: TerrainCostMask index → movement cost multiplier.
    /// Stored as a blob so Burst can read it.
    /// </summary>
    public struct TerrainCostTable : IComponentData
    {
        public BlobAssetReference<TerrainCostBlob> Blob;
    }

    public struct TerrainCostBlob
    {
        public BlobArray<int> Costs; // Index 0–255, value is cost (10 = normal, 20 = slow, etc)
    }

    // ─────────────────────────────────────────────
    // STUCK DETECTION
    // ─────────────────────────────────────────────

    [ChunkSerializable]
    public struct StuckDetection : IComponentData
    {
        public float3 LastCheckedPosition;
        public float NextCheckTime;
        public float CheckInterval;
        public float StuckDistanceThreshold;
        public int StuckCount;              // Consecutive stuck checks
        public int MaxStuckCount;           // Before forcing repath
    }

    // ─────────────────────────────────────────────
    // NAVIGATION COMMANDS (pure ECS move orders)
    // ─────────────────────────────────────────────

    /// <summary>
    /// Enable this on an agent entity to issue a move order.
    /// NavigationCommandSystem reads it, enables PathRequest, then disables this.
    /// Any system or job can write this — fully ECS, no MonoBehaviour required.
    /// </summary>
    [ChunkSerializable]
    public struct NavigationMoveCommand : IComponentData, IEnableableComponent
    {
        public float3 Destination;
        public int Priority;   // Higher = processed sooner when requests are queued
    }

    /// <summary>
    /// Enable this on an agent entity to stop it immediately next frame.
    /// </summary>
    public struct NavigationStopCommand : IComponentData, IEnableableComponent { }
}