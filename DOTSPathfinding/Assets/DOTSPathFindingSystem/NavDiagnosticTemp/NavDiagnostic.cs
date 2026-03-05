using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Transforms;
using UnityEngine;

namespace Shek.ECSNavigation
{
    /// <summary>
    /// Crash-proof diagnostic v2. Uses EntityManager raw queries + try/catch
    /// so exceptions never silently swallow output lines.
    /// Replace the old NavDiagnosticSystem.cs with this file.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial class NavDiagnosticSystem : SystemBase
    {
        private float _nextLog = 1f;
        private const float Interval = 3f;

        protected override void OnCreate() { }   // no RequireForUpdate — always runs

        protected override void OnUpdate()
        {
            float time = (float)SystemAPI.Time.ElapsedTime;
            if (time < _nextLog) return;
            _nextLog = time + Interval;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"===== NavDiag t={time:F1} =====");

            try
            {
                var gcQuery = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Shek.ECSGrid.GridConfig>());
                int gcCount = gcQuery.CalculateEntityCount();
                sb.AppendLine($"GridConfig entities: {gcCount}");
                if (gcCount > 0)
                {
                    var gc = gcQuery.GetSingleton<Shek.ECSGrid.GridConfig>();
                    sb.AppendLine($"  CellSize={gc.CellSize} ChunkCellCount={gc.ChunkCellCount} ActiveRing={gc.ActiveRingRadius} GhostRing={gc.GhostRingRadius}");
                }
                gcQuery.Dispose();
            }
            catch (System.Exception e) { sb.AppendLine($"GridConfig EXCEPTION: {e.Message}"); }

            try
            {
                var ncQuery = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NavigationConfig>());
                int ncCount = ncQuery.CalculateEntityCount();
                sb.AppendLine($"NavigationConfig entities: {ncCount}");
                if (ncCount > 0)
                {
                    var nc = ncQuery.GetSingleton<NavigationConfig>();
                    sb.AppendLine($"  CellSize={nc.CellSize} ChunkCellCount={nc.ChunkCellCount}");
                }
                ncQuery.Dispose();
            }
            catch (System.Exception e) { sb.AppendLine($"NavigationConfig EXCEPTION: {e.Message}"); }

            try
            {
                var saQuery = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Shek.ECSGrid.StreamingAnchor>());
                int saCount = saQuery.CalculateEntityCount();
                sb.AppendLine($"StreamingAnchor entities: {saCount}");
                if (saCount == 0)
                    sb.AppendLine("  !! WARNING: No StreamingAnchors found — GridManagerSystem requires one to run!!");
                var saArr = saQuery.ToComponentDataArray<Shek.ECSGrid.StreamingAnchor>(Allocator.Temp);
                foreach (var a in saArr)
                    sb.AppendLine($"  Anchor pos={a.WorldPosition:F1} chunk={a.CurrentChunkCoord} priority={a.Priority}");
                saArr.Dispose();
                saQuery.Dispose();
            }
            catch (System.Exception e) { sb.AppendLine($"StreamingAnchor EXCEPTION: {e.Message}"); }

            try
            {
                var chunkQuery = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Shek.ECSGrid.GridChunk>());
                int totalChunks = chunkQuery.CalculateEntityCount();
                int readyChunks = 0, activeChunks = 0;
                if (totalChunks > 0)
                {
                    var chunks = chunkQuery.ToComponentDataArray<Shek.ECSGrid.GridChunk>(Allocator.Temp);
                    foreach (var c in chunks)
                    {
                        if (c.StaticDataReady == 1) readyChunks++;
                        if (c.State == Shek.ECSGrid.ChunkState.Active) activeChunks++;
                    }
                    chunks.Dispose();
                }
                sb.AppendLine($"GridChunks: total={totalChunks} staticReady={readyChunks} active={activeChunks}");
                if (totalChunks == 0)
                    sb.AppendLine("  !! WARNING: Zero chunk entities — GridManagerSystem is not creating any chunks!!");
                chunkQuery.Dispose();
            }
            catch (System.Exception e) { sb.AppendLine($"GridChunk EXCEPTION: {e.Message}"); }

            try
            {
                var agentQuery = EntityManager.CreateEntityQuery(
                    ComponentType.ReadOnly<AgentNavigation>(),
                    ComponentType.ReadOnly<UnitMovement>(),
                    ComponentType.ReadOnly<LocalTransform>());

                int agentCount = agentQuery.CalculateEntityCount();
                sb.AppendLine($"Agents (AgentNavigation): {agentCount}");

                var entities = agentQuery.ToEntityArray(Allocator.Temp);
                var navArr = agentQuery.ToComponentDataArray<AgentNavigation>(Allocator.Temp);
                var movArr = agentQuery.ToComponentDataArray<UnitMovement>(Allocator.Temp);
                var tfArr = agentQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);

                for (int i = 0; i < entities.Length; i++)
                {
                    Entity ent = entities[i];
                    var nav = navArr[i];
                    var mov = movArr[i];
                    var tf = tfArr[i];

                    bool hasPathReq = EntityManager.HasComponent<PathRequest>(ent);
                    bool pathReqOn = hasPathReq && EntityManager.IsComponentEnabled<PathRequest>(ent);
                    bool successOn = EntityManager.HasComponent<PathfindingSuccess>(ent) && EntityManager.IsComponentEnabled<PathfindingSuccess>(ent);
                    bool failedOn = EntityManager.HasComponent<PathfindingFailed>(ent) && EntityManager.IsComponentEnabled<PathfindingFailed>(ent);
                    bool movCmdOn = EntityManager.HasComponent<NavigationMoveCommand>(ent) && EntityManager.IsComponentEnabled<NavigationMoveCommand>(ent);
                    bool flowOn = EntityManager.HasComponent<FlowFieldFollower>(ent) && EntityManager.IsComponentEnabled<FlowFieldFollower>(ent);
                    int wpCount = EntityManager.HasBuffer<PathWaypoint>(ent)
                                      ? EntityManager.GetBuffer<PathWaypoint>(ent, true).Length : -1;

                    sb.AppendLine(
                        $"  [{ent.Index}] pos={tf.Position:F1} | " +
                        $"HasDest={nav.HasDestination} Dest={nav.Destination:F1} | " +
                        $"Mode={nav.Mode} Following={mov.IsFollowingPath} " +
                        $"WP={mov.CurrentWaypointIndex}/{wpCount} | " +
                        $"PathReqComp={hasPathReq} PathReqOn={pathReqOn} | " +
                        $"Success={successOn} Failed={failedOn} | " +
                        $"MoveCmd={movCmdOn} Flow={flowOn} | " +
                        $"Cooldown={nav.RepathCooldown:F2}");

                    if (!hasPathReq)
                        sb.AppendLine($"    !! MISSING PathRequest component — DotsNavAgentAuthoring baker did not add it!!");
                }

                entities.Dispose(); navArr.Dispose(); movArr.Dispose(); tfArr.Dispose();
                agentQuery.Dispose();
            }
            catch (System.Exception e) { sb.AppendLine($"Agent query EXCEPTION: {e.Message}"); }

            sb.AppendLine("===== end =====");
            Debug.Log(sb.ToString());
        }
    }
}