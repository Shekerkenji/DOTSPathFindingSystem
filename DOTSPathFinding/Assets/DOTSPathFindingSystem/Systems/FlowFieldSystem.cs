using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Burst;

namespace Shek.ECSNavigation
{
    /// <summary>
    /// Manages flow fields: one field per destination cluster.
    /// Fields are built once and shared across all agents moving to that destination.
    /// O(1) per agent per frame for movement — cost is in build, not sampling.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(UnitMovementSystem))]
    [UpdateAfter(typeof(AStarSystem))]
    public partial class FlowFieldSystem : SystemBase
    {
        private const float FieldExpiry = 5f; // Reduced so fields rebuild quickly after bake changes
        private NativeHashMap<ulong, Entity> _fieldRegistry;

        protected override void OnCreate()
        {
            RequireForUpdate<NavigationConfig>();
            RequireForUpdate<FlowFieldRegistry>();
            _fieldRegistry = new NativeHashMap<ulong, Entity>(64, Allocator.Persistent);
        }

        protected override void OnDestroy()
        {
            foreach (var field in SystemAPI.Query<RefRW<FlowFieldData>>())
            {
                if (field.ValueRO.Vectors.IsCreated) field.ValueRW.Vectors.Dispose();
                if (field.ValueRO.Integration.IsCreated) field.ValueRW.Integration.Dispose();
            }
            if (_fieldRegistry.IsCreated) _fieldRegistry.Dispose();
        }

        protected override void OnUpdate()
        {
            var config = SystemAPI.GetSingleton<NavigationConfig>();
            float time = (float)SystemAPI.Time.ElapsedTime;

            // 1. Collect unique destinations from FlowField-mode agents
            var destinationSet = new NativeHashMap<ulong, float3>(32, Allocator.Temp);
            foreach (var (nav, followerEnabled) in SystemAPI.Query<RefRO<AgentNavigation>, EnabledRefRO<FlowFieldFollower>>())
            {
                if (nav.ValueRO.HasDestination == 0) continue;
                ulong key = DestinationHash(nav.ValueRO.Destination, config);
                if (!destinationSet.ContainsKey(key))
                    destinationSet[key] = nav.ValueRO.Destination;
            }

            // 2. Build or refresh fields
            foreach (var kvp in destinationSet)
            {
                ulong destHash = kvp.Key;
                float3 destWorld = kvp.Value;
                int2 destChunk = ChunkManagerSystem.WorldToChunkCoord(destWorld, config);

                BuildOrRefreshField(destHash, destWorld, destChunk, config, time);
                for (int dx = -1; dx <= 1; dx++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        BuildOrRefreshField(destHash, destWorld, destChunk + new int2(dx, dz), config, time);
                    }
            }

            // 3. Expire old fields
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (field, entity) in SystemAPI.Query<RefRW<FlowFieldData>>().WithEntityAccess())
            {
                if (time - field.ValueRO.BuildTime > FieldExpiry)
                {
                    _fieldRegistry.Remove(FieldKey(field.ValueRO.DestinationHash, field.ValueRO.ChunkCoord));
                    if (field.ValueRO.Vectors.IsCreated) field.ValueRW.Vectors.Dispose();
                    if (field.ValueRO.Integration.IsCreated) field.ValueRW.Integration.Dispose();
                    ecb.DestroyEntity(entity);
                }
            }
            ecb.Playback(EntityManager);
            ecb.Dispose();
            destinationSet.Dispose();
        }

        private void BuildOrRefreshField(ulong destHash, float3 destWorld, int2 chunkCoord,
                                          NavigationConfig config, float time)
        {
            ulong key = FieldKey(destHash, chunkCoord);

            // Still fresh?
            if (_fieldRegistry.TryGetValue(key, out Entity existing))
            {
                var data = EntityManager.GetComponentData<FlowFieldData>(existing);
                if (data.IsReady == 1 && (time - data.BuildTime) < FieldExpiry) return;
            }

            // Find chunk blob
            BlobAssetReference<ChunkStaticBlob> blob = default;
            bool found = false;
            foreach (var (chunk, staticData) in SystemAPI.Query<RefRO<GridChunk>, RefRO<ChunkStaticData>>())
            {
                if (math.all(chunk.ValueRO.ChunkCoord == chunkCoord) && chunk.ValueRO.StaticDataReady == 1)
                { blob = staticData.ValueRO.Blob; found = true; break; }
            }
            if (!found) return;

            int cellCount = config.ChunkCellCount;
            int total = cellCount * cellCount;

            // Create or reuse field entity
            Entity fieldEntity;
            if (!_fieldRegistry.TryGetValue(key, out fieldEntity))
            {
                fieldEntity = EntityManager.CreateEntity();
                EntityManager.AddComponentData(fieldEntity, new FlowFieldData
                {
                    DestinationHash = destHash,
                    ChunkCoord = chunkCoord,
                    Destination = destWorld,
                    Vectors = new NativeArray<float2>(total, Allocator.Persistent),
                    Integration = new NativeArray<int>(total, Allocator.Persistent),
                    IsReady = 0,
                    BuildTime = 0f
                });
                _fieldRegistry[key] = fieldEntity;
            }

            var field = EntityManager.GetComponentData<FlowFieldData>(fieldEntity);
            var buildJob = new FlowFieldBuildJob
            {
                Config = config,
                ChunkCoord = chunkCoord,
                DestWorld = destWorld,
                Blob = blob,
                Integration = field.Integration,
                Vectors = field.Vectors
            };
            buildJob.Execute();

            field.IsReady = 1;
            field.BuildTime = time;
            EntityManager.SetComponentData(fieldEntity, field);
        }

        /// <summary>
        /// Sample a flow field vector for an agent. Returns false if no field available.
        /// Call from UnitMovementSystem or a dedicated sampler system.
        /// </summary>
        public bool TrySampleField(ulong destHash, float3 worldPos, NavigationConfig config,
                                    out float2 direction)
        {
            direction = float2.zero;
            int2 chunkCoord = ChunkManagerSystem.WorldToChunkCoord(worldPos, config);
            ulong key = FieldKey(destHash, chunkCoord);

            if (!_fieldRegistry.TryGetValue(key, out Entity fieldEntity)) return false;
            if (!EntityManager.Exists(fieldEntity)) return false;

            var field = EntityManager.GetComponentData<FlowFieldData>(fieldEntity);
            if (field.IsReady == 0 || !field.Vectors.IsCreated) return false;

            int2 localCell = ChunkManagerSystem.WorldToCellLocal(worldPos, chunkCoord, config);
            int cellCount = config.ChunkCellCount;
            localCell = math.clamp(localCell, int2.zero, new int2(cellCount - 1, cellCount - 1));
            int idx = ChunkManagerSystem.CellLocalToIndex(localCell, cellCount);

            direction = field.Vectors[idx];
            return math.lengthsq(direction) > 0.001f;
        }

        public static ulong DestinationHash(float3 pos, NavigationConfig config)
        {
            int2 cell = new int2((int)math.floor(pos.x / config.CellSize), (int)math.floor(pos.z / config.CellSize));
            return (ulong)((long)cell.x << 32 | (uint)cell.y);
        }

        private static ulong FieldKey(ulong destHash, int2 chunkCoord)
        {
            ulong chunkKey = ((ulong)(uint)chunkCoord.x << 32) | (uint)chunkCoord.y;
            return destHash ^ (chunkKey * 2654435761UL);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // BURST COMPILED FLOW FIELD BUILD JOB
    // ─────────────────────────────────────────────────────────────────────────

    [BurstCompile]
    public struct FlowFieldBuildJob
    {
        public NavigationConfig Config;
        public int2 ChunkCoord;
        public float3 DestWorld;
        public BlobAssetReference<ChunkStaticBlob> Blob;
        public NativeArray<int> Integration;
        public NativeArray<float2> Vectors;

        public void Execute()
        {
            ref ChunkStaticBlob blob = ref Blob.Value;
            int cellCount = blob.CellCount;
            int total = cellCount * cellCount;

            // Phase 1: Reset
            for (int i = 0; i < total; i++) Integration[i] = int.MaxValue;

            // Phase 2: Dijkstra wavefront from goal
            int2 goalLocal = ChunkManagerSystem.WorldToCellLocal(DestWorld, ChunkCoord, Config);
            goalLocal = math.clamp(goalLocal, int2.zero, new int2(cellCount - 1, cellCount - 1));
            int goalIdx = ChunkManagerSystem.CellLocalToIndex(goalLocal, cellCount);

            Integration[goalIdx] = 0;
            var queue = new NativeList<int>(total, Allocator.Temp);
            queue.Add(goalIdx);
            int head = 0;

            while (head < queue.Length)
            {
                int curIdx = queue[head++];
                int curCost = Integration[curIdx];
                int2 cur = new int2(curIdx % cellCount, curIdx / cellCount);

                for (int dx = -1; dx <= 1; dx++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        int2 n = cur + new int2(dx, dz);
                        if (n.x < 0 || n.x >= cellCount || n.y < 0 || n.y >= cellCount) continue;
                        int nIdx = ChunkManagerSystem.CellLocalToIndex(n, cellCount);
                        if (blob.Nodes[nIdx].WalkableLayerMask == 0) continue;

                        int terrainCost = blob.Nodes[nIdx].TerrainCostMask == 0 ? 10 : 20;
                        int moveCost = (dx != 0 && dz != 0) ? 14 : 10;
                        int newCost = curCost + moveCost + (terrainCost - 10);

                        if (newCost < Integration[nIdx])
                        { Integration[nIdx] = newCost; queue.Add(nIdx); }
                    }
            }
            queue.Dispose();

            // Phase 3: Gradient → direction vectors
            // FIX: Only consider walkable neighbours when computing gradient direction.
            // Previously unwalkable neighbours could have lower integration costs
            // (from being adjacent to the goal) causing vectors to point into walls.
            for (int idx = 0; idx < total; idx++)
            {
                if (Integration[idx] == int.MaxValue) { Vectors[idx] = float2.zero; continue; }
                int2 local = new int2(idx % cellCount, idx / cellCount);
                float2 bestDir = float2.zero;
                int bestCost = Integration[idx];

                for (int dx = -1; dx <= 1; dx++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        int2 n = local + new int2(dx, dz);
                        if (n.x < 0 || n.x >= cellCount || n.y < 0 || n.y >= cellCount) continue;
                        int nIdx = ChunkManagerSystem.CellLocalToIndex(n, cellCount);
                        // FIX: skip unwalkable neighbours — vectors must never point into walls
                        if (blob.Nodes[nIdx].WalkableLayerMask == 0) continue;
                        if (Integration[nIdx] < bestCost)
                        { bestCost = Integration[nIdx]; bestDir = math.normalize(new float2(dx, dz)); }
                    }
                Vectors[idx] = bestDir;
            }
        }
    }
}