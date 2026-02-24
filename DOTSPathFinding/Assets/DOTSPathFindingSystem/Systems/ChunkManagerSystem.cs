using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Burst;
using UnityEngine;

namespace Navigation.ECS
{
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [UpdateAfter(typeof(StreamingAnchorSystem))]
    public partial class ChunkManagerSystem : SystemBase
    {
        private NativeHashMap<int2, Entity> _chunkEntityMap;
        private EntityArchetype _chunkArchetype;

        protected override void OnCreate()
        {
            RequireForUpdate<NavigationConfig>();
            RequireForUpdate<StreamingAnchor>();
            _chunkEntityMap = new NativeHashMap<int2, Entity>(512, Allocator.Persistent);
            _chunkArchetype = EntityManager.CreateArchetype(
                typeof(GridChunk), typeof(ChunkStaticData), typeof(ChunkTransitionRequest));
        }

        protected override void OnDestroy()
        {
            foreach (var (dyn, _) in SystemAPI.Query<RefRW<ChunkDynamicData>>().WithEntityAccess())
                if (dyn.ValueRO.IsAllocated == 1 && dyn.ValueRO.Nodes.IsCreated)
                    dyn.ValueRW.Nodes.Dispose();
            if (_chunkEntityMap.IsCreated) _chunkEntityMap.Dispose();
        }

        protected override void OnUpdate()
        {
            var config = SystemAPI.GetSingleton<NavigationConfig>();
            int ghostR = config.GhostRingRadius;

            // Union desired states from ALL streaming anchors (player, squads, cameras, POIs...)
            var desiredStates = new NativeHashMap<int2, ChunkState>(
                (ghostR * 2 + 1) * (ghostR * 2 + 1) * 4, Allocator.Temp);

            foreach (var anchor in SystemAPI.Query<RefRO<StreamingAnchor>>())
            {
                int2 centre = anchor.ValueRO.CurrentChunkCoord;
                // Priority scales the active ring — priority 2 anchor gets double active radius
                int activeR = config.ActiveRingRadius * math.max(1, anchor.ValueRO.Priority);
                int anchorGhostR = math.max(ghostR, activeR + 2);

                for (int x = -anchorGhostR; x <= anchorGhostR; x++)
                    for (int z = -anchorGhostR; z <= anchorGhostR; z++)
                    {
                        int2 coord = centre + new int2(x, z);
                        int dist = math.max(math.abs(x), math.abs(z));
                        ChunkState desired = dist <= activeR ? ChunkState.Active : ChunkState.Ghost;

                        // Take the higher state if this coord is already in the map
                        // (another anchor may already want it Active)
                        if (!desiredStates.TryGetValue(coord, out ChunkState existing) ||
                            desired > existing)
                            desiredStates[coord] = desired;
                    }
            }

            // Create new chunk entities
            foreach (var kvp in desiredStates)
                if (!_chunkEntityMap.ContainsKey(kvp.Key))
                {
                    Entity e = EntityManager.CreateEntity(_chunkArchetype);
                    EntityManager.SetComponentData(e, new GridChunk { ChunkCoord = kvp.Key, State = ChunkState.Unloaded, StaticDataReady = 0 });
                    EntityManager.SetComponentEnabled<ChunkTransitionRequest>(e, true);
                    EntityManager.SetComponentData(e, new ChunkTransitionRequest { ChunkCoord = kvp.Key, TargetState = kvp.Value });
                    _chunkEntityMap[kvp.Key] = e;
                }

            // Queue state changes
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (chunk, entity) in SystemAPI.Query<RefRO<GridChunk>>().WithEntityAccess())
            {
                int2 coord = chunk.ValueRO.ChunkCoord;
                if (desiredStates.TryGetValue(coord, out ChunkState desired))
                {
                    if (chunk.ValueRO.State != desired)
                    {
                        ecb.SetComponentEnabled<ChunkTransitionRequest>(entity, true);
                        ecb.SetComponent(entity, new ChunkTransitionRequest { ChunkCoord = coord, TargetState = desired });
                    }
                }
                else if (chunk.ValueRO.State != ChunkState.Unloaded)
                {
                    ecb.SetComponentEnabled<ChunkTransitionRequest>(entity, true);
                    ecb.SetComponent(entity, new ChunkTransitionRequest { ChunkCoord = coord, TargetState = ChunkState.Unloaded });
                }
            }
            ecb.Playback(EntityManager);
            ecb.Dispose();

            ProcessTransitions(config);
            desiredStates.Dispose();
        }

        private void ProcessTransitions(NavigationConfig config)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (chunk, request, staticData, entity) in
                SystemAPI.Query<RefRW<GridChunk>, RefRO<ChunkTransitionRequest>, RefRW<ChunkStaticData>>()
                    .WithAll<ChunkTransitionRequest>().WithEntityAccess())
            {
                ChunkState from = chunk.ValueRO.State;
                ChunkState to = request.ValueRO.TargetState;
                if (from == to) { ecb.SetComponentEnabled<ChunkTransitionRequest>(entity, false); continue; }

                if (from == ChunkState.Unloaded && to >= ChunkState.Ghost)
                {
                    var sd = staticData.ValueRW;
                    BakeChunkStatic(chunk.ValueRO.ChunkCoord, config, ref sd);
                    staticData.ValueRW = sd;
                    chunk.ValueRW.State = ChunkState.Ghost;
                    chunk.ValueRW.StaticDataReady = 1;
                    if (to == ChunkState.Active)
                    {
                        AllocateDynamicData(entity, config, ecb);
                        chunk.ValueRW.State = ChunkState.Active;
                    }
                }
                else if (from == ChunkState.Ghost && to == ChunkState.Active)
                { AllocateDynamicData(entity, config, ecb); chunk.ValueRW.State = ChunkState.Active; }
                else if (from == ChunkState.Active && to == ChunkState.Ghost)
                { DisposeDynamicData(entity); ecb.RemoveComponent<ChunkDynamicData>(entity); chunk.ValueRW.State = ChunkState.Ghost; }
                else if (to == ChunkState.Unloaded)
                {
                    if (from == ChunkState.Active) { DisposeDynamicData(entity); ecb.RemoveComponent<ChunkDynamicData>(entity); }
                    var sd = staticData.ValueRW; DisposeStaticData(ref sd); staticData.ValueRW = sd;
                    chunk.ValueRW.State = ChunkState.Unloaded;
                    chunk.ValueRW.StaticDataReady = 0;
                    _chunkEntityMap.Remove(chunk.ValueRO.ChunkCoord);
                    ecb.DestroyEntity(entity);
                }
                ecb.SetComponentEnabled<ChunkTransitionRequest>(entity, false);
            }
            ecb.Playback(EntityManager);
            ecb.Dispose();
        }

        private void BakeChunkStatic(int2 chunkCoord, NavigationConfig config, ref ChunkStaticData staticData)
        {
            int cellCount = config.ChunkCellCount;
            int total = cellCount * cellCount;
            float3 origin = ChunkCoordToWorld(chunkCoord, config);
            var builder = new BlobBuilder(Allocator.Temp);
            ref ChunkStaticBlob blob = ref builder.ConstructRoot<ChunkStaticBlob>();
            blob.ChunkCoord = chunkCoord;
            blob.CellCount = cellCount;
            var nodesArr = builder.Allocate(ref blob.Nodes, total);
            var macroArr = builder.Allocate(ref blob.MacroConnectivity, 8);
            for (int z = 0; z < cellCount; z++)
                for (int x = 0; x < cellCount; x++)
                {
                    // All float3 arithmetic — no Vector3 mixing here
                    float3 cellCentre = origin + new float3((x + 0.5f) * config.CellSize, 0f, (z + 0.5f) * config.CellSize);
                    nodesArr[z * cellCount + x] = BakeNode(cellCentre, config);
                }
            BakeMacroConnectivity(ref macroArr, origin, config);
            staticData.Blob = builder.CreateBlobAssetReference<ChunkStaticBlob>(Allocator.Persistent);
            builder.Dispose();
        }

        private NodeStatic BakeNode(float3 cellCentre, NavigationConfig config)
        {
            var node = new NodeStatic();
            // Explicit Vector3 for Physics API to avoid CS0034 ambiguity
            var rayOrigin = new Vector3(cellCentre.x, cellCentre.y + config.BakeRaycastHeight, cellCentre.z);
            if (!Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, config.BakeRaycastHeight + 2f, config.GroundPhysicsLayer))
            { node.WalkableLayerMask = 0; node.SlopeFlags = 1; return node; }

            float slope = Vector3.Angle(hit.normal, Vector3.up);
            if (slope > config.MaxSlopeAngle)
            { node.SlopeFlags = 1; node.WalkableLayerMask = 0b00000010; }
            else
            { node.SlopeFlags = 0; node.WalkableLayerMask = 0b11111111; }

            // Explicit Vector3 for Physics.CheckSphere
            var overlapCentre = new Vector3(hit.point.x, hit.point.y + config.AgentRadius, hit.point.z);
            if (Physics.CheckSphere(overlapCentre, config.AgentRadius * 0.9f, config.UnwalkablePhysicsLayer))
                node.WalkableLayerMask = 0;

            node.TerrainCostMask = 0;
            return node;
        }

        private void BakeMacroConnectivity(ref BlobBuilderArray<byte> macroArr, float3 origin, NavigationConfig config)
        {
            float half = config.ChunkCellCount * config.CellSize * 0.5f;
            float3 centre = origin + new float3(half, 0, half);
            int2[] dirs = { new int2(0, 1), new int2(1, 1), new int2(1, 0), new int2(1, -1), new int2(0, -1), new int2(-1, -1), new int2(-1, 0), new int2(-1, 1) };
            for (int i = 0; i < 8; i++)
            {
                float3 edge = centre + new float3(dirs[i].x * half, 0, dirs[i].y * half);
                var rayOrigin = new Vector3(edge.x, edge.y + config.BakeRaycastHeight, edge.z);
                bool hit = Physics.Raycast(rayOrigin, Vector3.down, config.BakeRaycastHeight + 2f, config.GroundPhysicsLayer);
                macroArr[i] = hit ? (byte)10 : (byte)0;
            }
        }

        private void AllocateDynamicData(Entity entity, NavigationConfig config, EntityCommandBuffer ecb)
        {
            int total = config.ChunkCellCount * config.ChunkCellCount;
            ecb.AddComponent(entity, new ChunkDynamicData
            { Nodes = new NativeArray<NodeDynamic>(total, Allocator.Persistent), IsAllocated = 1 });
        }

        private void DisposeDynamicData(Entity entity)
        {
            if (EntityManager.HasComponent<ChunkDynamicData>(entity))
            {
                var dyn = EntityManager.GetComponentData<ChunkDynamicData>(entity);
                if (dyn.IsAllocated == 1 && dyn.Nodes.IsCreated) dyn.Nodes.Dispose();
            }
        }

        private void DisposeStaticData(ref ChunkStaticData sd)
        { if (sd.Blob.IsCreated) sd.Blob.Dispose(); }

        // ── Public utilities ─────────────────────────────────────────────

        public static float3 ChunkCoordToWorld(int2 coord, NavigationConfig config)
        {
            float s = config.ChunkCellCount * config.CellSize;
            return new float3(coord.x * s, 0, coord.y * s);
        }

        public static int2 WorldToChunkCoord(float3 worldPos, NavigationConfig config)
        {
            float s = config.ChunkCellCount * config.CellSize;
            return new int2((int)math.floor(worldPos.x / s), (int)math.floor(worldPos.z / s));
        }

        public static int2 WorldToCellLocal(float3 worldPos, int2 chunkCoord, NavigationConfig config)
        {
            float3 local = worldPos - ChunkCoordToWorld(chunkCoord, config);
            return new int2((int)math.floor(local.x / config.CellSize), (int)math.floor(local.z / config.CellSize));
        }

        public static int CellLocalToIndex(int2 localCell, int chunkCellCount)
            => localCell.y * chunkCellCount + localCell.x;

        public bool TryGetChunkEntity(int2 coord, out Entity entity)
            => _chunkEntityMap.TryGetValue(coord, out entity);
    }

    /// <summary>
    /// Updates CurrentChunkCoord on every StreamingAnchor entity each frame.
    /// Any entity with StreamingAnchor is valid — player, squad, camera, POI, etc.
    /// Fully Burst compiled.
    /// </summary>
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [BurstCompile]
    public partial struct StreamingAnchorSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NavigationConfig>();
            // Does NOT require StreamingAnchor singleton — there can be many
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var config = SystemAPI.GetSingleton<NavigationConfig>();
            var job = new UpdateAnchorCoordsJob { Config = config };
            state.Dependency = job.ScheduleParallel(state.Dependency);
        }

        [BurstCompile]
        partial struct UpdateAnchorCoordsJob : IJobEntity
        {
            public NavigationConfig Config;
            void Execute(ref StreamingAnchor anchor)
            {
                int2 newCoord = ChunkManagerSystem.WorldToChunkCoord(anchor.WorldPosition, Config);
                if (math.any(newCoord != anchor.CurrentChunkCoord))
                    anchor.CurrentChunkCoord = newCoord;
            }
        }
    }
}