using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;
using UnityEngine;

namespace Navigation.ECS
{
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial class BakeDebugSystem : SystemBase
    {
        private BakeDebugBridge _bridge;

        protected override void OnCreate() => RequireForUpdate<NavigationConfig>();
        protected override void OnUpdate()
        {
            // Push current config and chunk data to the bridge every frame
            if (_bridge == null) return;
            _bridge.Config = SystemAPI.GetSingleton<NavigationConfig>();
            _bridge.ConfigReady = true;
        }

        protected override void OnStartRunning()
        {
            var go = new GameObject("[Bake Debug]") { hideFlags = HideFlags.DontSave };
            _bridge = go.AddComponent<BakeDebugBridge>();
            _bridge.World = World;
        }

        protected override void OnStopRunning()
        {
            if (_bridge != null && _bridge.gameObject != null)
                Object.Destroy(_bridge.gameObject);
        }
    }

    public class BakeDebugBridge : MonoBehaviour
    {
        public World World;
        public NavigationConfig Config;
        public bool ConfigReady;

        private const int DrawRadius = 20;

        private void OnDrawGizmos()
        {
#if UNITY_EDITOR
            if (!ConfigReady || World == null || !World.IsCreated || !Application.isPlaying) return;

            var em = World.EntityManager;
            var q = em.CreateEntityQuery(typeof(GridChunk), typeof(ChunkStaticData));
            var chunks = q.ToComponentDataArray<GridChunk>(Allocator.Temp);
            var statics = q.ToComponentDataArray<ChunkStaticData>(Allocator.Temp);

            Vector3 camPos = Camera.main ? Camera.main.transform.position : Vector3.zero;

            for (int c = 0; c < chunks.Length; c++)
            {
                if (chunks[c].StaticDataReady == 0) continue;
                ref ChunkStaticBlob blob = ref statics[c].Blob.Value;
                int cellCount = blob.CellCount;
                float3 origin = ChunkManagerSystem.ChunkCoordToWorld(chunks[c].ChunkCoord, Config);

                for (int z = 0; z < cellCount; z++)
                    for (int x = 0; x < cellCount; x++)
                    {
                        float3 cc = origin + new float3((x + 0.5f) * Config.CellSize, 0f, (z + 0.5f) * Config.CellSize);
                        if (math.distance(new float2(cc.x, cc.z), new float2(camPos.x, camPos.z)) > DrawRadius) continue;

                        bool walkable = blob.Nodes[z * cellCount + x].WalkableLayerMask != 0;
                        Gizmos.color = walkable ? new Color(0f, 1f, 0f, 0.15f) : new Color(1f, 0f, 0f, 0.55f);
                        Gizmos.DrawCube(
                            new Vector3(cc.x, cc.y + 0.05f, cc.z),
                            new Vector3(Config.CellSize * 0.9f, 0.1f, Config.CellSize * 0.9f));
                    }
            }

            chunks.Dispose();
            statics.Dispose();
            q.Dispose();
#endif
        }

        [ContextMenu("Log CheckBox Parameters")]
        public void LogCheckBoxParams()
        {
            if (!ConfigReady) { Debug.LogWarning("[BakeDebug] Config not ready — enter play mode first."); return; }

            float cellHalf = Config.CellSize * 0.5f;
            float boxHalf = cellHalf + Config.AgentRadius;

            Debug.Log($"[BakeDebug] CellSize={Config.CellSize}  AgentRadius={Config.AgentRadius}\n" +
                      $"  CheckBox halfExtents = ({boxHalf:F2}, {Config.AgentRadius:F2}, {boxHalf:F2})\n" +
                      $"  UnwalkablePhysicsLayer mask = {Config.UnwalkablePhysicsLayer}  " +
                      $"(layer index = {Mathf.RoundToInt(Mathf.Log(Config.UnwalkablePhysicsLayer, 2))})\n" +
                      $"  GroundPhysicsLayer mask = {Config.GroundPhysicsLayer}  " +
                      $"(layer index = {Mathf.RoundToInt(Mathf.Log(Config.GroundPhysicsLayer, 2))})");

            // Test CheckBox at world origin — move your camera near a wall first
            bool hit = Physics.CheckBox(
                Camera.main ? Camera.main.transform.position : Vector3.zero,
                new Vector3(boxHalf, Config.AgentRadius, boxHalf),
                Quaternion.identity,
                Config.UnwalkablePhysicsLayer);
            Debug.Log($"[BakeDebug] Live CheckBox at camera position hit unwalkable layer: {hit}");
        }
    }
}