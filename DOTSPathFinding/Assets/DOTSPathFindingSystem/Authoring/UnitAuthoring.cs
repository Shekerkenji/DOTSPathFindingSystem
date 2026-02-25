using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Navigation.ECS
{
    /// <summary>
    /// Add to any GameObject that should be a navigation agent.
    /// The baker converts it into ECS components at bake time.
    ///
    /// To issue move orders at runtime, use:
    ///   NavigationAPI.SetDestination(EntityManager, entity, targetPosition);
    /// </summary>
    [AddComponentMenu("Navigation/Unit")]
    public class UnitAuthoring : MonoBehaviour
    {
        [Header("Movement")]
        [Tooltip("World units per second.")]
        public float speed = 5f;

        [Tooltip("How fast the unit rotates toward its target direction.")]
        public float turnSpeed = 8f;

        [Tooltip("Distance to a waypoint before advancing to the next one.")]
        public float turnDistance = 0.5f;

        [Header("Navigation")]
        [Tooltip("Distance to the final destination that counts as arrived.")]
        public float arrivalThreshold = 1f;

        [Tooltip("Minimum seconds between automatic repath attempts.")]
        public float repathCooldown = 0.5f;

        [Header("Layer Permissions")]
        [Tooltip(
            "Bitmask � which terrain layers this unit can enter.\n" +
            "Bit 0 (0x01) = Ground/Infantry\n" +
            "Bit 1 (0x02) = Flying\n" +
            "Bit 2 (0x04) = Vehicle\n" +
            "Bit 3 (0x08) = Amphibious\n" +
            "0xFF = all layers allowed (default)")]
        public byte walkableLayers = 0xFF;

        [Tooltip("True = uses 3D (26-neighbour) A* and ignores slope blocking.")]
        public bool isFlying = false;

        [Header("Stuck Detection")]
        [Tooltip("How often (seconds) to check if the unit is stuck.")]
        public float stuckCheckInterval = 2f;

        [Tooltip("Minimum distance the unit must move per check interval or it's considered stuck.")]
        public float stuckMoveThreshold = 0.3f;

        [Tooltip("How many consecutive stuck checks before forcing a repath.")]
        public int maxStuckCount = 3;
    }

    public class UnitBaker : Baker<UnitAuthoring>
    {
        public override void Bake(UnitAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);

            AddComponent(entity, new UnitMovement
            {
                Speed = authoring.speed,
                TurnSpeed = authoring.turnSpeed,
                TurnDistance = authoring.turnDistance,
                CurrentWaypointIndex = 0,
                IsFollowingPath = 0
            });

            AddComponent(entity, new AgentNavigation
            {
                Destination = float3.zero,
                LastKnownPosition = float3.zero,
                Mode = NavMode.Idle,
                FlowFieldId = -1,
                RepathCooldown = 0f,
                StuckTimer = 0f,
                ArrivalThreshold = authoring.arrivalThreshold,
                HasDestination = 0
            });

            AddComponent(entity, new UnitLayerPermissions
            {
                WalkableLayers = authoring.walkableLayers,
                CostLayerWeights = 0xFF,
                IsFlying = (byte)(authoring.isFlying ? 1 : 0)
            });

            AddComponent(entity, new StuckDetection
            {
                LastCheckedPosition = authoring.transform.position,
                NextCheckTime = 2f,
                CheckInterval = authoring.stuckCheckInterval,
                StuckDistanceThreshold = authoring.stuckMoveThreshold,
                StuckCount = 0,
                MaxStuckCount = authoring.maxStuckCount
            });

            // Path waypoint buffers
            AddBuffer<PathWaypoint>(entity);
            AddBuffer<MacroWaypoint>(entity);

            // Enableable tag components � all disabled at spawn
            AddComponent(entity, new PathRequest());
            SetComponentEnabled<PathRequest>(entity, false);

            AddComponent(entity, new PathfindingSuccess());
            SetComponentEnabled<PathfindingSuccess>(entity, false);

            AddComponent(entity, new PathfindingFailed());
            SetComponentEnabled<PathfindingFailed>(entity, false);

            AddComponent(entity, new NeedsRepath());
            SetComponentEnabled<NeedsRepath>(entity, false);

            AddComponent(entity, new FlowFieldFollower());
            SetComponentEnabled<FlowFieldFollower>(entity, false);

            // Navigation command receivers � disabled until a command is issued
            AddComponent(entity, new NavigationMoveCommand());
            SetComponentEnabled<NavigationMoveCommand>(entity, false);

            AddComponent(entity, new NavigationStopCommand());
            SetComponentEnabled<NavigationStopCommand>(entity, false);

            // Movement event signals — disabled at spawn, enabled for one frame by the system
            AddComponent(entity, new StartedMoving());
            SetComponentEnabled<StartedMoving>(entity, false);

            AddComponent(entity, new StoppedMoving());
            SetComponentEnabled<StoppedMoving>(entity, false);
        }
    }
}