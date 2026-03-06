using Unity.Entities;
using Unity.Collections;
using UnityEngine;

namespace Shek.ECSGrid
{
    /// <summary>
    /// Place ONE of these on an empty GameObject in every scene that uses the grid.
    /// It bakes the GridConfig singleton and the GridManager entity that holds the
    /// GridCellRequest buffer and TerrainCostTable.
    ///
    /// Navigation, buildings, fog-of-war, resource systems etc. all share this config.
    /// Navigation adds its own NavigationConfig singleton via NavigationConfigAuthoring
    /// (which is now a lightweight, nav-only authoring component).
    /// </summary>
    public class GridConfigAuthoring : MonoBehaviour
    {
        [Header("Grid")]
        [Tooltip("World units per grid cell. Smaller = more precise, more memory/bake time.")]
        public float cellSize = 1f;

        [Tooltip("Cells per chunk side (e.g. 64 → 64×64 = 4096 cells/chunk).")]
        public int chunkCellCount = 64;

        [Header("Streaming")]
        [Tooltip("Chunks kept fully simulated (Active state) around each streaming anchor.")]
        public int activeRingRadius = 2;

        [Tooltip("Chunks kept in Ghost state (walkability baked, no dynamic data) beyond the active ring.")]
        public int ghostRingRadius = 6;

        [Header("Baking — Physics")]
        [Tooltip("Agent capsule radius used for obstacle clearance during bake.")]
        public float agentRadius = 0.5f;

        [Tooltip("Physics layers treated as solid obstacles (walls, rocks, buildings).")]
        public LayerMask unwalkableLayer;

        [Tooltip("Physics layers treated as walkable ground surface.")]
        public LayerMask groundLayer;

        [Tooltip("Slopes steeper than this are impassable for ground units.")]
        [Range(0f, 90f)]
        public float maxSlopeAngle = 45f;

        [Tooltip("Height above each cell centre from which the downward bake raycast fires.")]
        public float bakeRaycastHeight = 5f;

        [Header("Terrain Cost Tiers")]
        [Tooltip("Movement cost for terrain tier 0 (normal ground). Default = 10.")]
        public int costTier0 = 10;
        [Tooltip("Movement cost for terrain tier 1 (grass / light). Default = 15.")]
        public int costTier1 = 15;
        [Tooltip("Movement cost for terrain tier 2 (mud / heavy). Default = 25.")]
        public int costTier2 = 25;
        [Tooltip("Movement cost for terrain tier 3 (road / paved — faster). Default = 5.")]
        public int costTier3 = 5;
    }

    public class GridConfigBaker : Baker<GridConfigAuthoring>
    {
        public override void Bake(GridConfigAuthoring auth)
        {
            // ── GridConfig singleton ──────────────────────────────────────────
            var configEntity = GetEntity(TransformUsageFlags.None);

            AddComponent(configEntity, new GridConfig
            {
                CellSize              = auth.cellSize,
                ChunkCellCount        = auth.chunkCellCount,
                ActiveRingRadius      = auth.activeRingRadius,
                GhostRingRadius       = auth.ghostRingRadius,
                AgentRadius           = auth.agentRadius,
                UnwalkablePhysicsLayer = auth.unwalkableLayer,
                GroundPhysicsLayer    = auth.groundLayer,
                MaxSlopeAngle         = auth.maxSlopeAngle,
                BakeRaycastHeight     = auth.bakeRaycastHeight,
            });

            // ── GridManager entity ────────────────────────────────────────────
            // Separate entity so other systems can append to its request buffer
            // without touching the config singleton.
            var managerEntity = CreateAdditionalEntity(TransformUsageFlags.None);

            AddComponent(managerEntity, new GridManagerTag());

            // Terrain cost table
            var builder = new BlobBuilder(Allocator.Temp);
            ref TerrainCostBlob blob = ref builder.ConstructRoot<TerrainCostBlob>();
            var costs = builder.Allocate(ref blob.Costs, 256);

            for (int i = 0; i < 256; i++) costs[i] = auth.costTier0; // default all tiers to tier0
            costs[0] = auth.costTier0;
            costs[1] = auth.costTier1;
            costs[2] = auth.costTier2;
            costs[3] = auth.costTier3;

            var blobRef = builder.CreateBlobAssetReference<TerrainCostBlob>(Allocator.Persistent);
            builder.Dispose();
            AddBlobAsset(ref blobRef, out _);

            AddComponent(managerEntity, new TerrainCostTable { Blob = blobRef });

            // GridCellRequest buffer — written to by buildings, units, gameplay code
            AddBuffer<GridCellRequest>(managerEntity);
        }
    }
}
