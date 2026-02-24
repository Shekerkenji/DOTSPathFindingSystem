using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Transforms;
using UnityEngine;

namespace Navigation.ECS
{
    /// <summary>
    /// Scene view debug visualization.
    /// Uses EntityManager directly — safe in MonoBehaviour context.
    /// SystemAPI is only valid inside systems; never call it from MonoBehaviour.
    /// </summary>
    public class NavigationDebugAuthoring : MonoBehaviour
    {
        [Header("Chunk Visualization")]
        public bool showChunks = true;
        public bool showGhostChunks = true;
        public bool showActiveChunks = true;
        public bool showWalkability = false;
        public byte walkabilityLayer = 0xFF;

        [Header("Agent Visualization")]
        public bool showAgentPaths = true;
        public bool showAgentMode = true;

        [Header("Colors")]
        public Color activeChunkColor = new Color(0f, 1f, 0f, 0.15f);
        public Color ghostChunkColor = new Color(1f, 1f, 0f, 0.08f);
        public Color walkableColor = new Color(0f, 1f, 0f, 0.3f);
        public Color unwalkableColor = new Color(1f, 0f, 0f, 0.3f);
        public Color astarPathColor = Color.cyan;
        public Color flowFieldColor = Color.magenta;
        public Color macroPathColor = Color.yellow;

        private void OnDrawGizmos()
        {
#if UNITY_EDITOR
            if (!Application.isPlaying) return;

            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated) return;
            var em = world.EntityManager;

            // Get NavigationConfig via EntityManager query (safe in MonoBehaviour)
            var configQuery = em.CreateEntityQuery(typeof(NavigationConfig));
            if (configQuery.IsEmpty) { configQuery.Dispose(); return; }
            var config = configQuery.GetSingleton<NavigationConfig>();
            configQuery.Dispose();

            float chunkWorldSize = config.ChunkCellCount * config.CellSize;

            // ── Chunks ──────────────────────────────────────────────────
            if (showChunks)
            {
                var chunkQuery = em.CreateEntityQuery(typeof(GridChunk));
                var chunks = chunkQuery.ToComponentDataArray<GridChunk>(Allocator.Temp);

                foreach (var chunk in chunks)
                {
                    float3 origin = ChunkManagerSystem.ChunkCoordToWorld(chunk.ChunkCoord, config);
                    var centre = new Vector3(origin.x + chunkWorldSize * 0.5f, 0.1f, origin.z + chunkWorldSize * 0.5f);
                    var size = new Vector3(chunkWorldSize, 0.05f, chunkWorldSize);

                    if (chunk.State == ChunkState.Active && showActiveChunks)
                    {
                        Gizmos.color = activeChunkColor; Gizmos.DrawCube(centre, size);
                        Gizmos.color = Color.green; Gizmos.DrawWireCube(centre, size);
                        UnityEditor.Handles.Label(centre + Vector3.up,
                            $"A {chunk.ChunkCoord.x},{chunk.ChunkCoord.y}");
                    }
                    else if (chunk.State == ChunkState.Ghost && showGhostChunks)
                    {
                        Gizmos.color = ghostChunkColor; Gizmos.DrawCube(centre, size);
                        Gizmos.color = Color.yellow; Gizmos.DrawWireCube(centre, size);
                    }
                }
                chunks.Dispose();
                chunkQuery.Dispose();
            }

            // ── Walkability ──────────────────────────────────────────────
            if (showWalkability)
            {
                var chunkQuery = em.CreateEntityQuery(typeof(GridChunk), typeof(ChunkStaticData));
                var chunkEnts = chunkQuery.ToEntityArray(Allocator.Temp);
                foreach (var entity in chunkEnts)
                {
                    var chunk = em.GetComponentData<GridChunk>(entity);
                    if (chunk.StaticDataReady == 0) continue;
                    var staticData = em.GetComponentData<ChunkStaticData>(entity);
                    if (!staticData.Blob.IsCreated) continue;
                    ref var blob = ref staticData.Blob.Value;
                    int cellCount = blob.CellCount;
                    float3 origin = ChunkManagerSystem.ChunkCoordToWorld(chunk.ChunkCoord, config);

                    for (int z = 0; z < cellCount; z++)
                        for (int x = 0; x < cellCount; x++)
                        {
                            var node = blob.Nodes[z * cellCount + x];
                            bool walkable = (node.WalkableLayerMask & walkabilityLayer) != 0;
                            var cellCentre = new Vector3(
                                origin.x + (x + 0.5f) * config.CellSize,
                                0.2f,
                                origin.z + (z + 0.5f) * config.CellSize);
                            Gizmos.color = walkable ? walkableColor : unwalkableColor;
                            Gizmos.DrawCube(cellCentre, Vector3.one * config.CellSize * 0.85f);
                        }
                }
                chunkEnts.Dispose();
                chunkQuery.Dispose();
            }

            // ── Agent paths & modes ──────────────────────────────────────
            if (showAgentPaths || showAgentMode)
            {
                var agentQuery = em.CreateEntityQuery(
                    typeof(AgentNavigation), typeof(LocalTransform), typeof(UnitMovement));
                var agentEnts = agentQuery.ToEntityArray(Allocator.Temp);

                foreach (var entity in agentEnts)
                {
                    var nav = em.GetComponentData<AgentNavigation>(entity);
                    var transform = em.GetComponentData<LocalTransform>(entity);
                    var movement = em.GetComponentData<UnitMovement>(entity);
                    Vector3 pos = new Vector3(transform.Position.x, transform.Position.y, transform.Position.z);

                    if (showAgentMode)
                    {
                        string label = nav.Mode switch
                        {
                            NavMode.AStar => "A*",
                            NavMode.FlowField => "FF",
                            NavMode.MacroOnly => "MAC",
                            _ => "IDLE"
                        };
                        UnityEditor.Handles.color = nav.Mode switch
                        {
                            NavMode.AStar => astarPathColor,
                            NavMode.FlowField => flowFieldColor,
                            NavMode.MacroOnly => macroPathColor,
                            _ => Color.white
                        };
                        UnityEditor.Handles.Label(pos + Vector3.up * 2f, label);
                    }

                    if (!showAgentPaths || nav.HasDestination == 0) continue;

                    if (nav.Mode == NavMode.AStar && em.HasBuffer<PathWaypoint>(entity))
                    {
                        var path = em.GetBuffer<PathWaypoint>(entity);
                        Gizmos.color = astarPathColor;
                        if (path.Length > 0)
                        {
                            Gizmos.DrawLine(pos, path[0].Position);
                            for (int i = 0; i < path.Length - 1; i++)
                                Gizmos.DrawLine(path[i].Position, path[i + 1].Position);
                            if (movement.CurrentWaypointIndex < path.Length)
                            {
                                Gizmos.color = Color.white;
                                Gizmos.DrawSphere(path[movement.CurrentWaypointIndex].Position, 0.3f);
                            }
                        }
                        Gizmos.color = Color.green;
                        Gizmos.DrawSphere(nav.Destination, 0.5f);
                    }

                    if (nav.Mode == NavMode.MacroOnly && em.HasBuffer<MacroWaypoint>(entity))
                    {
                        var macro = em.GetBuffer<MacroWaypoint>(entity);
                        Gizmos.color = macroPathColor;
                        if (macro.Length > 0)
                        {
                            Gizmos.DrawLine(pos, macro[0].WorldEntryPoint);
                            for (int i = 0; i < macro.Length - 1; i++)
                                Gizmos.DrawLine(macro[i].WorldEntryPoint, macro[i + 1].WorldEntryPoint);
                        }
                        Gizmos.color = Color.green;
                        Gizmos.DrawSphere(nav.Destination, 0.5f);
                    }

                    if (nav.Mode == NavMode.FlowField)
                    {
                        Gizmos.color = flowFieldColor;
                        Gizmos.DrawLine(pos, nav.Destination);
                        Gizmos.DrawSphere(nav.Destination, 0.5f);
                    }
                }
                agentEnts.Dispose();
                agentQuery.Dispose();
            }
#endif
        }
    }
}