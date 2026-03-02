using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Shek.ECSGameplay
{
    /// <summary>
    /// Add to a unit GameObject (alongside CombatAgentAuthoring) to give it patrol behaviour.
    ///
    /// SETUP:
    ///   1. Add this component to your unit prefab.
    ///   2. Assign the Waypoints list — these are world-space Transform positions.
    ///      Drag empty GameObjects into the list; only their positions are used.
    ///   3. Choose a patrol Mode and set Wait Duration if you want the unit to pause.
    ///
    /// RUNTIME:
    ///   When the unit has no enemy target (CurrentTarget.HasTarget == 0) the
    ///   PatrolSystem moves it between waypoints using NavigationMoveCommand.
    ///   The moment ThreatScanSystem assigns a target the unit transitions to
    ///   Chase/Attack. When the enemy dies or leaves chase range it returns to patrol.
    /// </summary>
    [AddComponentMenu("Gameplay/Patrol Agent")]
    public class PatrolAuthoring : MonoBehaviour
    {
        [Tooltip("World-space patrol waypoints. Drag empty GameObjects in here.")]
        public Transform[] waypoints = System.Array.Empty<Transform>();

        [Tooltip("How the unit cycles through waypoints.")]
        public PatrolMode mode = PatrolMode.Loop;

        [Tooltip("Seconds to wait at each waypoint before continuing. 0 = move immediately.")]
        public float waitDuration = 1f;

        [Tooltip("Distance from a waypoint centre that counts as 'arrived'.")]
        public float arrivalRadius = 1.2f;

        [Tooltip("Which waypoint index to start from.")]
        public int startWaypointIndex = 0;
    }

    public class PatrolBaker : Baker<PatrolAuthoring>
    {
        public override void Bake(PatrolAuthoring a)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);

            // Clamp start index to valid range
            int startIdx = a.waypoints.Length > 0
                ? math.clamp(a.startWaypointIndex, 0, a.waypoints.Length - 1)
                : 0;

            AddComponent(entity, new PatrolData
            {
                CurrentWaypointIndex = startIdx,
                PingPongDirection = 1,
                Mode = a.mode,
                WaitDuration = a.waitDuration,
                WaitTimer = 0f,
                ArrivalRadius = math.max(0.1f, a.arrivalRadius),
                RandomSeed = (uint)(entity.Index ^ 0xDEADBEEF)
            });

            // Bake waypoint positions into a dynamic buffer
            var buf = AddBuffer<PatrolWaypoint>(entity);
            foreach (var wp in a.waypoints)
            {
                if (wp == null) continue;
                // DependsOn so the baker re-runs if any waypoint Transform moves
                DependsOn(wp);
                buf.Add(new PatrolWaypoint { Position = wp.position });
            }
        }
    }
}