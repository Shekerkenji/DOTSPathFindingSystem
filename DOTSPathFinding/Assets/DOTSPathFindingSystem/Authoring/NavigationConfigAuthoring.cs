using Unity.Entities;
using Unity.Collections;
using UnityEngine;

namespace Shek.ECSNavigation
{
    /// <summary>
    /// Place on an empty GameObject — one per scene.
    /// Configures the entire navigation system.
    /// </summary>
    public class NavigationConfigAuthoring : MonoBehaviour
    {
        [Header("Grid")]
        [Tooltip("World units per grid cell. Smaller = more precise but more memory and bake time.")]
        public float cellSize = 1f;

        [Tooltip("Cells per chunk side. 64 → 64×64 = 4096 cells. Affects memory granularity.")]
        public int chunkCellCount = 64;

        [Header("Streaming")]
        [Tooltip("Chunks fully simulated around the streaming anchor (player).")]
        public int activeRingRadius = 2;

        [Tooltip("Chunks kept in ghost state (walkability only, no simulation) beyond active ring.")]
        public int ghostRingRadius = 6;

        [Header("Baking")]
        [Tooltip("Agent capsule radius — used for obstacle clearance checks during bake.")]
        public float agentRadius = 0.5f;

        [Tooltip("Physics layer(s) treated as solid obstacles (walls, rocks, buildings).")]
        public LayerMask unwalkableLayer;

        [Tooltip("Physics layer(s) treated as walkable ground surface.")]
        public LayerMask groundLayer;

        [Tooltip("Slopes steeper than this angle are impassable for ground units.")]
        [Range(0f, 90f)]
        public float maxSlopeAngle = 45f;

        [Tooltip("How high above each cell centre to start the downward bake raycast.")]
        public float bakeRaycastHeight = 5f;
    }

    public class NavigationConfigBaker : Baker<NavigationConfigAuthoring>
    {
        public override void Bake(NavigationConfigAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.None);

            AddComponent(entity, new NavigationConfig
            {
                CellSize = authoring.cellSize,
                ChunkCellCount = authoring.chunkCellCount,
                ActiveRingRadius = authoring.activeRingRadius,
                GhostRingRadius = authoring.ghostRingRadius,
                AgentRadius = authoring.agentRadius,
                UnwalkablePhysicsLayer = authoring.unwalkableLayer,
                GroundPhysicsLayer = authoring.groundLayer,
                MaxSlopeAngle = authoring.maxSlopeAngle,
                BakeRaycastHeight = authoring.bakeRaycastHeight
            });

            // FlowField registry singleton
            AddComponent(entity, new FlowFieldRegistry { NextId = 0 });

            // Terrain cost table — edit these values to tune terrain weights
            var builder = new BlobBuilder(Allocator.Temp);
            ref TerrainCostBlob blob = ref builder.ConstructRoot<TerrainCostBlob>();
            var costs = builder.Allocate(ref blob.Costs, 256);

            for (int i = 0; i < 256; i++) costs[i] = 10; // Default: all normal cost
            costs[0] = 10;  // Tier 0: normal ground
            costs[1] = 15;  // Tier 1: grass / light terrain
            costs[2] = 25;  // Tier 2: mud / heavy terrain
            costs[3] = 5;   // Tier 3: road / paved (faster)

            var blobRef = builder.CreateBlobAssetReference<TerrainCostBlob>(Allocator.Persistent);
            builder.Dispose();
            AddBlobAsset(ref blobRef, out _);
            AddComponent(entity, new TerrainCostTable { Blob = blobRef });
        }
    }
}