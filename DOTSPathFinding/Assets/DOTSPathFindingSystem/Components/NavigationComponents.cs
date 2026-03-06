using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;

// Grid primitives (GridChunk, ChunkStaticData, NodeStatic, StreamingAnchor, etc.)
// now live in Shek.ECSGrid. Navigation merely consumes them.
using Shek.ECSGrid;

namespace Shek.ECSNavigation
{
    // =========================================================================
    // NAVIGATION CONFIG
    // Lightweight — only nav-specific knobs. Grid knobs live in GridConfig.
    // =========================================================================

    /// <summary>
    /// Navigation-specific singleton baked by NavigationConfigAuthoring.
    /// Grid geometry (cell size, chunk size, physics layers) comes from GridConfig.
    /// </summary>
    public struct NavigationConfig : IComponentData
    {
        // These mirror GridConfig values so Burst jobs can be given a single config
        // struct without reaching for two singletons. Populated by NavigationConfigBaker.
        public float CellSize;
        public int ChunkCellCount;
        public float AgentRadius;
        public int UnwalkablePhysicsLayer;
        public int GroundPhysicsLayer;
        public float MaxSlopeAngle;
        public float BakeRaycastHeight;
        public int GhostRingRadius;
        public int ActiveRingRadius;
    }

    // =========================================================================
    // AGENT COMPONENTS
    // =========================================================================

    [ChunkSerializable]
    public struct UnitLayerPermissions : IComponentData
    {
        public byte WalkableLayers;
        public byte CostLayerWeights;
        public byte IsFlying;
    }

    public enum NavMode : byte
    {
        Idle = 0,
        AStar = 1,
        FlowField = 2,
        MacroOnly = 3
    }

    [ChunkSerializable]
    public struct AgentNavigation : IComponentData
    {
        public float3 Destination;
        public float3 LastKnownPosition;
        public NavMode Mode;
        public int FlowFieldId;
        public float RepathCooldown;
        public float StuckTimer;
        public float ArrivalThreshold;
        public byte HasDestination;

        /// <summary>
        /// Set to 1 by FollowMacroPathJob when the macro path is complete.
        /// Read and cleared by NavigationDispatchSystem on the main thread,
        /// which then issues the final A* PathRequest.
        /// </summary>
        public byte MacroPathDone;
    }

    [ChunkSerializable]
    public struct UnitMovement : IComponentData
    {
        public float Speed;
        public float TurnSpeed;
        public float TurnDistance;
        public int CurrentWaypointIndex;
        public byte IsFollowingPath;
        /// <summary>
        /// Tracks previous frame value for start/stop event detection.
        /// Only MovementEventSystem should write this.
        /// </summary>
        public byte PreviousIsFollowingPath;
    }

    // =========================================================================
    // PATH BUFFERS
    // =========================================================================

    public struct PathWaypoint : IBufferElementData
    {
        public float3 Position;
    }

    public struct MacroWaypoint : IBufferElementData
    {
        public int2 ChunkCoord;
        public float3 WorldEntryPoint;
    }

    // =========================================================================
    // FLOW FIELD COMPONENTS
    // =========================================================================

    public struct FlowFieldRegistry : IComponentData
    {
        public int NextId;
    }

    public struct FlowFieldData : IComponentData
    {
        public int FieldId;
        public int2 ChunkCoord;
        public float3 Destination;
        public ulong DestinationHash;
        public NativeArray<float2> Vectors;
        public NativeArray<int> Integration;
        public byte IsReady;
        public float BuildTime;
    }

    [ChunkSerializable]
    public struct FlowFieldFollower : IComponentData, IEnableableComponent
    {
        public int FieldId;
    }

    // =========================================================================
    // PATH REQUEST / RESULT
    // =========================================================================

    [ChunkSerializable]
    public struct PathRequest : IComponentData, IEnableableComponent
    {
        public float3 Start;
        public float3 End;
        public int Priority;
        public float RequestTime;
    }

    public struct PathfindingSuccess : IComponentData, IEnableableComponent { }
    public struct PathfindingFailed : IComponentData, IEnableableComponent { }
    public struct NeedsRepath : IComponentData, IEnableableComponent { }

    // =========================================================================
    // A* INTERNALS
    // =========================================================================

    public struct AStarNode : System.IComparable<AStarNode>
    {
        public int Index;
        public int GCost;
        public int HCost;
        public int FCost => GCost + HCost;
        public int ParentIndex;

        public int CompareTo(AStarNode other)
        {
            int cmp = FCost.CompareTo(other.FCost);
            if (cmp == 0) cmp = HCost.CompareTo(other.HCost);
            return -cmp;
        }
    }

    // =========================================================================
    // STUCK DETECTION
    // =========================================================================

    [ChunkSerializable]
    public struct StuckDetection : IComponentData
    {
        public float3 LastCheckedPosition;
        public float NextCheckTime;
        public float CheckInterval;
        public float StuckDistanceThreshold;
        public int StuckCount;
        public int MaxStuckCount;
    }

    // =========================================================================
    // NAVIGATION COMMANDS
    // =========================================================================

    [ChunkSerializable]
    public struct NavigationMoveCommand : IComponentData, IEnableableComponent
    {
        public float3 Destination;
        public int Priority;
    }

    public struct NavigationStopCommand : IComponentData, IEnableableComponent { }

    // =========================================================================
    // MOVEMENT EVENTS
    // =========================================================================

    public struct StartedMoving : IComponentData, IEnableableComponent { }
    public struct StoppedMoving : IComponentData, IEnableableComponent { }
}
