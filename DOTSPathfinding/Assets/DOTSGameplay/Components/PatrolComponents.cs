using Unity.Entities;
using Unity.Mathematics;

namespace Shek.ECSGameplay
{
    // ?????????????????????????????????????????????
    // PATROL STATE
    // ?????????????????????????????????????????????

    public enum PatrolMode : byte
    {
        Loop = 0,   // A ? B ? C ? A ? ...
        PingPong = 1,  // A ? B ? C ? B ? A ? ...
        Random = 2,  // picks a random waypoint each time
    }

    /// <summary>
    /// Added to units that should patrol between waypoints when idle.
    /// PatrolSystem reads this; AIDecisionSystem reads PatrolTarget to know
    /// the current navigation destination while in patrol mode.
    /// </summary>
    public struct PatrolData : IComponentData
    {
        /// <summary>Index of the waypoint the unit is currently walking toward.</summary>
        public int CurrentWaypointIndex;
        /// <summary>Direction of travel for PingPong mode (+1 or -1).</summary>
        public int PingPongDirection;
        public PatrolMode Mode;
        /// <summary>
        /// How long (seconds) to wait at each waypoint before moving to the next.
        /// Set to 0 to move immediately.
        /// </summary>
        public float WaitDuration;
        /// <summary>Counts down while the unit waits at a waypoint.</summary>
        public float WaitTimer;
        /// <summary>Radius within which the unit is considered to have reached a waypoint.</summary>
        public float ArrivalRadius;
        /// <summary>Random seed for PatrolMode.Random. Mutated each pick.</summary>
        public uint RandomSeed;
    }

    /// <summary>
    /// Buffer on a unit — each element is one world-space patrol waypoint.
    /// Populated by PatrolAuthoring at bake time.
    /// At runtime any system can add / remove waypoints via EntityManager.
    /// </summary>
    public struct PatrolWaypoint : IBufferElementData
    {
        public float3 Position;
    }
}