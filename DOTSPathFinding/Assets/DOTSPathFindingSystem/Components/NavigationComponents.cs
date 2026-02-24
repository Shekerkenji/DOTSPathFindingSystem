using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;

namespace Navigation.ECS
{
    // ─────────────────────────────────────────────
    // GRID & CHUNK COMPONENTS
    // ─────────────────────────────────────────────

    public struct NavigationConfig : IComponentData
    {
        public float CellSize;
        public int ChunkCellCount;
        public int GhostRingRadius;
        public int ActiveRingRadius;
        public float AgentRadius;
        public int UnwalkablePhysicsLayer;
        public int GroundPhysicsLayer;
        public float MaxSlopeAngle;
        public float BakeRaycastHeight;
    }

    public struct NodeStatic
    {
        public byte WalkableLayerMask;
        public byte TerrainCostMask;
        public byte SlopeFlags;
        public byte Reserved;
    }

    public struct NodeDynamic
    {
        public byte OccupancyCount;
        public byte DynamicBlockFlags;
        public short Reserved;
    }

    public enum ChunkState : byte
    {
        Unloaded = 0,
        Ghost = 1,
        Active = 2
    }

    public struct GridChunk : IComponentData
    {
        public int2 ChunkCoord;
        public ChunkState State;
        public byte StaticDataReady;
    }

    public struct ChunkStaticBlob
    {
        public BlobArray<NodeStatic> Nodes;
        public int2 ChunkCoord;
        public int CellCount;
        public BlobArray<byte> MacroConnectivity;
    }

    public struct ChunkStaticData : IComponentData
    {
        public BlobAssetReference<ChunkStaticBlob> Blob;
    }

    public struct ChunkDynamicData : IComponentData
    {
        public NativeArray<NodeDynamic> Nodes;
        public byte IsAllocated;
    }

    // ─────────────────────────────────────────────
    // UNIT / AGENT COMPONENTS
    // ─────────────────────────────────────────────

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
        /// Set to 1 by FollowMacroPathJob when the macro path is finished.
        /// Read and cleared by NavigationDispatchSystem on the main thread,
        /// which then issues the final A* PathRequest.
        /// Avoids needing an ECB inside the Burst job.
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
    }

    public struct PathWaypoint : IBufferElementData
    {
        public float3 Position;
    }

    public struct MacroWaypoint : IBufferElementData
    {
        public int2 ChunkCoord;
        public float3 WorldEntryPoint;
    }

    // ─────────────────────────────────────────────
    // FLOW FIELD COMPONENTS
    // ─────────────────────────────────────────────

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

    // ─────────────────────────────────────────────
    // PATHFINDING REQUEST / RESULT
    // ─────────────────────────────────────────────

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

    // ─────────────────────────────────────────────
    // A* INTERNALS
    // ─────────────────────────────────────────────

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

    // ─────────────────────────────────────────────
    // CHUNK STREAMING REQUESTS
    // ─────────────────────────────────────────────

    public struct ChunkTransitionRequest : IComponentData, IEnableableComponent
    {
        public int2 ChunkCoord;
        public ChunkState TargetState;
    }

    [ChunkSerializable]
    public struct StreamingAnchor : IComponentData
    {
        public float3 WorldPosition;
        public int2 CurrentChunkCoord;
        public int Priority;
    }

    // ─────────────────────────────────────────────
    // TERRAIN COST TABLE
    // ─────────────────────────────────────────────

    public struct TerrainCostTable : IComponentData
    {
        public BlobAssetReference<TerrainCostBlob> Blob;
    }

    public struct TerrainCostBlob
    {
        public BlobArray<int> Costs;
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
        public int StuckCount;
        public int MaxStuckCount;
    }

    // ─────────────────────────────────────────────
    // NAVIGATION COMMANDS
    // ─────────────────────────────────────────────

    [ChunkSerializable]
    public struct NavigationMoveCommand : IComponentData, IEnableableComponent
    {
        public float3 Destination;
        public int Priority;
    }

    public struct NavigationStopCommand : IComponentData, IEnableableComponent { }
}