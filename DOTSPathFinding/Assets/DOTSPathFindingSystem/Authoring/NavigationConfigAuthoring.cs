using Unity.Entities;
using UnityEngine;

// Grid config lives in ECSGrid — we reference it but don't duplicate it.
using Shek.ECSGrid;

namespace Shek.ECSNavigation
{
    /// <summary>
    /// Place ONE of these on the same (or a different) GameObject alongside
    /// GridConfigAuthoring. It bakes only the NavigationConfig singleton —
    /// all grid geometry is owned by GridConfig / GridConfigAuthoring.
    ///
    /// The baker copies shared fields (CellSize, ChunkCellCount, etc.) from
    /// GridConfigAuthoring so navigation Burst jobs can receive a single
    /// NavigationConfig without fetching two singletons.
    ///
    /// Also bakes the FlowFieldRegistry singleton required by FlowFieldSystem.
    /// </summary>
    public class NavigationConfigAuthoring : MonoBehaviour
    {
        [Header("Grid Reference")]
        [Tooltip(
            "Drag the GameObject that has GridConfigAuthoring here. " +
            "Navigation will mirror its geometry settings at bake time.")]
        public GridConfigAuthoring gridConfig;

        // If you ever want navigation-specific overrides that differ from the
        // global grid config, add them here. For now we just re-expose the
        // grid authoring so the baker can read it directly.
    }

    public class NavigationConfigBaker : Baker<NavigationConfigAuthoring>
    {
        public override void Bake(NavigationConfigAuthoring auth)
        {
            if (auth.gridConfig == null)
            {
                Debug.LogError(
                    "[NavigationConfigAuthoring] No GridConfigAuthoring assigned. " +
                    "Navigation config will not be baked.");
                return;
            }

            var g = auth.gridConfig;     // shorthand

            var entity = GetEntity(TransformUsageFlags.None);

            // Mirror shared geometry from GridConfigAuthoring into NavigationConfig
            // so all nav Burst jobs get everything from one struct.
            AddComponent(entity, new NavigationConfig
            {
                CellSize = g.cellSize,
                ChunkCellCount = g.chunkCellCount,
                ActiveRingRadius = g.activeRingRadius,
                GhostRingRadius = g.ghostRingRadius,
                AgentRadius = g.agentRadius,
                UnwalkablePhysicsLayer = g.unwalkableLayer,
                GroundPhysicsLayer = g.groundLayer,
                MaxSlopeAngle = g.maxSlopeAngle,
                BakeRaycastHeight = g.bakeRaycastHeight,
            });

            // FlowField registry singleton (navigation-internal)
            AddComponent(entity, new FlowFieldRegistry { NextId = 0 });
        }
    }
}
