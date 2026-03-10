using Unity.Entities;
using Unity.Mathematics;


public enum NavAgentStatus : byte
{
    Idle = 0,
    Requesting = 1,
    Moving = 2,
    Arrived = 3,
    PathFailed = 4,
}

public enum UnitState : byte
{
    Idle = 0,
    Moving = 1,
    Fighting = 2,
    Dead = 3,
}

public struct NavAgent : IComponentData
{
    public float3 Destination;
    public float MoveSpeed;
    public float StoppingDistance;
    public NavAgentStatus Status;
    public int CurrentPathIndex;
    public float3 FormationOffset;
    public Entity GroupEntity;
}

/// <summary>
/// Enableable: enabled while a path request is pending.
/// Replaces structural add/remove of PathRequest.
/// Agents must have this component baked in; systems toggle it enabled/disabled.
/// </summary>
public struct PathRequest : IComponentData, IEnableableComponent
{
    public float3 Start;
    public float3 End;
    public int RequestId;
}

public struct PathWaypoint : IBufferElementData
{
    public float3 Position;
}

/// <summary>
/// Enableable tag: enabled when a valid path has been computed this frame.
/// Replaces structural add/remove of PathReady.
/// </summary>
public struct PathReady : IComponentData, IEnableableComponent { }

/// <summary>
/// Enableable tag: enabled when pathfinding failed this frame.
/// Replaces structural add/remove of PathFailed.
/// </summary>
public struct PathFailed : IComponentData, IEnableableComponent { }

public struct GroupMoveOrder : IComponentData, IEnableableComponent
{
    public float3 Destination;
}

public struct BigGroupMoveOrder : IComponentData, IEnableableComponent
{
    public float3 Destination;
}