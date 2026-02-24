using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Transforms;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Navigation.ECS.Testing
{
    /// <summary>
    /// Navigation test component.
    /// Add to the same GameObject as UnitAuthoring.
    /// Use the Inspector buttons or right-click context menu in Play Mode.
    /// </summary>
    [AddComponentMenu("Navigation/Testing/Navigation Tester")]
    public class NavigationTester : MonoBehaviour
    {
        [Header("Target")]
        public Vector3 destination = new Vector3(0, 0, 10);
        public int priority = 1;

        [Header("Gizmos")]
        public bool showDestinationGizmo = true;
        public Color destinationColor = Color.green;

        // ── Public API ────────────────────────────────────────────────────

        [ContextMenu("Send Move Command")]
        public void SendMoveCommand()
        {
            var (em, agent) = GetEM_Entity();
            if (agent == Entity.Null) return;

            // Write DIRECTLY to AgentNavigation + PathRequest
            // Bypasses NavigationCommandSystem — reliable from outside ECS loop
            var nav = em.GetComponentData<AgentNavigation>(agent);
            var tr = em.GetComponentData<LocalTransform>(agent);

            nav.Destination = (float3)destination;
            nav.HasDestination = 1;
            nav.Mode = NavMode.AStar;
            nav.RepathCooldown = 0f;
            em.SetComponentData(agent, nav);

            em.SetComponentEnabled<PathRequest>(agent, true);
            em.SetComponentData(agent, new PathRequest
            {
                Start = tr.Position,
                End = (float3)destination,
                Priority = priority,
                RequestTime = Time.time
            });

            em.SetComponentEnabled<FlowFieldFollower>(agent, false);

            Debug.Log($"[NavTester] ✓ Move issued → {destination}  entity({agent.Index}:{agent.Version})\n" +
                      $"  Agent position: {tr.Position}\n" +
                      $"  PathRequest enabled: {em.IsComponentEnabled<PathRequest>(agent)}");
        }

        [ContextMenu("Stop Agent")]
        public void StopAgent()
        {
            var (em, agent) = GetEM_Entity();
            if (agent == Entity.Null) return;

            var nav = em.GetComponentData<AgentNavigation>(agent);
            nav.HasDestination = 0;
            nav.Mode = NavMode.Idle;
            em.SetComponentData(agent, nav);

            var mov = em.GetComponentData<UnitMovement>(agent);
            mov.IsFollowingPath = 0;
            mov.CurrentWaypointIndex = 0;
            em.SetComponentData(agent, mov);

            em.SetComponentEnabled<FlowFieldFollower>(agent, false);
            em.SetComponentEnabled<PathRequest>(agent, false);

            Debug.Log($"[NavTester] ⏹ Agent stopped  entity({agent.Index}:{agent.Version})");
        }

        [ContextMenu("Print Agent State")]
        public void PrintAgentState()
        {
            var (em, agent) = GetEM_Entity();
            if (agent == Entity.Null) return;

            var nav = em.GetComponentData<AgentNavigation>(agent);
            var mov = em.GetComponentData<UnitMovement>(agent);
            var tr = em.GetComponentData<LocalTransform>(agent);

            bool pathReqEnabled = em.IsComponentEnabled<PathRequest>(agent);
            bool ffEnabled = em.IsComponentEnabled<FlowFieldFollower>(agent);
            int pathLen = em.HasBuffer<PathWaypoint>(agent) ? em.GetBuffer<PathWaypoint>(agent).Length : 0;
            int macroLen = em.HasBuffer<MacroWaypoint>(agent) ? em.GetBuffer<MacroWaypoint>(agent).Length : 0;

            // Check chunk state at agent position
            string chunkInfo = GetChunkInfo(em, tr.Position);

            Debug.Log(
                $"[NavTester] ── Agent State ──  entity({agent.Index}:{agent.Version})\n" +
                $"  Position        : {tr.Position}\n" +
                $"  Destination     : {nav.Destination}\n" +
                $"  HasDestination  : {nav.HasDestination}\n" +
                $"  NavMode         : {nav.Mode}\n" +
                $"  IsFollowingPath : {mov.IsFollowingPath}\n" +
                $"  WaypointIndex   : {mov.CurrentWaypointIndex} / {pathLen}\n" +
                $"  MacroWaypoints  : {macroLen}\n" +
                $"  PathReq enabled : {pathReqEnabled}\n" +
                $"  FlowField active: {ffEnabled}\n" +
                $"  RepathCooldown  : {nav.RepathCooldown:F2}s\n" +
                $"  {chunkInfo}"
            );
        }

        [ContextMenu("Print Navigation Config")]
        public void PrintConfig()
        {
            var world = GetWorld();
            if (world == null) return;
            var em = world.EntityManager;

            var q = em.CreateEntityQuery(typeof(NavigationConfig));
            if (q.IsEmpty) { Debug.LogError("[NavTester] No NavigationConfig found — is NavigationConfigAuthoring in the scene?"); q.Dispose(); return; }
            var cfg = q.GetSingleton<NavigationConfig>();
            q.Dispose();

            Debug.Log(
                $"[NavTester] ── NavigationConfig ──\n" +
                $"  CellSize         : {cfg.CellSize}\n" +
                $"  ChunkCellCount   : {cfg.ChunkCellCount}\n" +
                $"  ActiveRingRadius : {cfg.ActiveRingRadius}\n" +
                $"  GhostRingRadius  : {cfg.GhostRingRadius}\n" +
                $"  MaxSlopeAngle    : {cfg.MaxSlopeAngle}\n" +
                $"  AgentRadius      : {cfg.AgentRadius}"
            );
        }

        [ContextMenu("Move To This Object's Position")]
        public void MoveToSelf()
        {
            destination = transform.position;
            SendMoveCommand();
        }

        // ── Helpers ───────────────────────────────────────────────────────

        private World GetWorld()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[NavTester] Enter Play Mode first.");
                return null;
            }
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated)
            {
                Debug.LogError("[NavTester] No ECS world found.");
                return null;
            }
            return world;
        }

        private (EntityManager, Entity) GetEM_Entity()
        {
            var world = GetWorld();
            if (world == null) return (default, Entity.Null);

            var em = world.EntityManager;
            var query = em.CreateEntityQuery(typeof(AgentNavigation), typeof(LocalTransform));
            var ents = query.ToEntityArray(Allocator.Temp);
            query.Dispose();

            if (ents.Length == 0)
            {
                Debug.LogError("[NavTester] No AgentNavigation entities found. Is UnitAuthoring baked?");
                ents.Dispose();
                return (em, Entity.Null);
            }

            // Pick entity closest to this GameObject
            Entity best = Entity.Null;
            float bestDist = float.MaxValue;
            float3 myPos = transform.position;

            foreach (var e in ents)
            {
                float d = math.distancesq(em.GetComponentData<LocalTransform>(e).Position, myPos);
                if (d < bestDist) { bestDist = d; best = e; }
            }
            ents.Dispose();
            return (em, best);
        }

        private string GetChunkInfo(EntityManager em, float3 agentPos)
        {
            var cfgQ = em.CreateEntityQuery(typeof(NavigationConfig));
            if (cfgQ.IsEmpty) { cfgQ.Dispose(); return "Chunk: no config"; }
            var cfg = cfgQ.GetSingleton<NavigationConfig>();
            cfgQ.Dispose();

            float chunkSize = cfg.ChunkCellCount * cfg.CellSize;
            int2 coord = new int2(
                (int)math.floor(agentPos.x / chunkSize),
                (int)math.floor(agentPos.z / chunkSize));

            // Find chunk state
            var chunkQ = em.CreateEntityQuery(typeof(GridChunk));
            var chunks = chunkQ.ToComponentDataArray<GridChunk>(Allocator.Temp);
            chunkQ.Dispose();

            foreach (var chunk in chunks)
                if (math.all(chunk.ChunkCoord == coord))
                {
                    chunks.Dispose();
                    return $"Chunk coord : {coord}  State: {chunk.State}  StaticReady: {chunk.StaticDataReady}";
                }

            chunks.Dispose();
            return $"Chunk coord : {coord}  State: NOT LOADED";
        }

        // ── Gizmos ────────────────────────────────────────────────────────

        private void OnDrawGizmosSelected()
        {
            if (!showDestinationGizmo) return;
            Gizmos.color = destinationColor;
            Gizmos.DrawSphere(destination, 0.4f);
            Gizmos.DrawLine(transform.position, destination);
#if UNITY_EDITOR
            Handles.color = destinationColor;
            Handles.Label(destination + Vector3.up, "Destination");
#endif
        }
    }

#if UNITY_EDITOR
    [CustomEditor(typeof(NavigationTester))]
    public class NavigationTesterEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var t = (NavigationTester)target;

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Commands", EditorStyles.boldLabel);

            bool playing = Application.isPlaying;
            using (new EditorGUI.DisabledScope(!playing))
            {
                if (GUILayout.Button("▶  Send Move Command", GUILayout.Height(32))) t.SendMoveCommand();
                if (GUILayout.Button("⏹  Stop Agent", GUILayout.Height(28))) t.StopAgent();

                EditorGUILayout.Space(4);
                if (GUILayout.Button("📋  Print Agent State", GUILayout.Height(26))) t.PrintAgentState();
                if (GUILayout.Button("⚙  Print Navigation Config", GUILayout.Height(26))) t.PrintConfig();
                if (GUILayout.Button("📍  Move To This Object's Position", GUILayout.Height(26))) t.MoveToSelf();
            }

            if (!playing)
                EditorGUILayout.HelpBox("Enter Play Mode to send commands.", MessageType.Info);
        }
    }
#endif
}