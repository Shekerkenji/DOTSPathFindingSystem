using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Burst;
using UnityEngine;

namespace Shek.ECSGrid
{
    // =========================================================================
    // STREAMING ANCHOR COORD UPDATER
    // Runs first so chunk decisions use current frame positions.
    // =========================================================================

    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [BurstCompile]
    public partial struct StreamingAnchorSystem : ISystem
    {
        public void OnCreate(ref SystemState state) =>
            state.RequireForUpdate<GridConfig>();

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var config = SystemAPI.GetSingleton<GridConfig>();
            new UpdateAnchorCoordsJob { Config = config }
                .ScheduleParallel(state.Dependency)
                .Complete();
        }

        [BurstCompile]
        partial struct UpdateAnchorCoordsJob : IJobEntity
        {
            public GridConfig Config;
            void Execute(ref StreamingAnchor anchor)
            {
                int2 newCoord = GridManagerSystem.WorldToChunkCoord(anchor.WorldPosition, Config);
                if (math.any(newCoord != anchor.CurrentChunkCoord))
                    anchor.CurrentChunkCoord = newCoord;
            }
        }
    }

    // =========================================================================
    // GRID MANAGER SYSTEM
    // Handles: chunk lifecycle, static baking, dynamic allocation, streaming.
    // Completely independent of navigation — any system can query GridChunk /
    // ChunkStaticData / ChunkDynamicData directly.
    // =========================================================================

    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [UpdateAfter(typeof(StreamingAnchorSystem))]
    public partial class GridManagerSystem : SystemBase
    {
        // ── Internal state ────────────────────────────────────────────────────

        private NativeHashMap<int2, Entity> _chunkEntityMap;
        private EntityArchetype             _chunkArchetype;

        // ── Lifecycle ─────────────────────────────────────────────────────────

        protected override void OnCreate()
        {
            RequireForUpdate<GridConfig>();
            RequireForUpdate<StreamingAnchor>();

            _chunkEntityMap = new NativeHashMap<int2, Entity>(512, Allocator.Persistent);
            _chunkArchetype = EntityManager.CreateArchetype(
                typeof(GridChunk),
                typeof(ChunkStaticData),
                typeof(ChunkTransitionRequest));
        }

        protected override void OnDestroy()
        {
            // Dispose all live dynamic arrays
            foreach (var (dyn, _) in SystemAPI.Query<RefRW<ChunkDynamicData>>().WithEntityAccess())
                if (dyn.ValueRO.IsAllocated == 1 && dyn.ValueRO.Nodes.IsCreated)
                    dyn.ValueRW.Nodes.Dispose();

            if (_chunkEntityMap.IsCreated) _chunkEntityMap.Dispose();
        }

        protected override void OnUpdate()
        {
            var config = SystemAPI.GetSingleton<GridConfig>();

            // ── 1. Union desired states from all streaming anchors ────────────
            int ghostR = config.GhostRingRadius;
            var desiredStates = new NativeHashMap<int2, ChunkState>(
                (ghostR * 2 + 1) * (ghostR * 2 + 1) * 4, Allocator.Temp);

            foreach (var anchor in SystemAPI.Query<RefRO<StreamingAnchor>>())
            {
                int2 centre   = anchor.ValueRO.CurrentChunkCoord;
                int  activeR  = config.ActiveRingRadius * math.max(1, anchor.ValueRO.Priority);
                int  anchorGR = math.max(ghostR, activeR + 2);

                for (int x = -anchorGR; x <= anchorGR; x++)
                for (int z = -anchorGR; z <= anchorGR; z++)
                {
                    int2       coord   = centre + new int2(x, z);
                    int        dist    = math.max(math.abs(x), math.abs(z));
                    ChunkState desired = dist <= activeR ? ChunkState.Active : ChunkState.Ghost;

                    if (!desiredStates.TryGetValue(coord, out ChunkState existing) || desired > existing)
                        desiredStates[coord] = desired;
                }
            }

            // ── 2. Create chunk entities for newly desired coords ─────────────
            foreach (var kvp in desiredStates)
            {
                if (_chunkEntityMap.ContainsKey(kvp.Key)) continue;

                Entity e = EntityManager.CreateEntity(_chunkArchetype);
                EntityManager.SetComponentData(e, new GridChunk
                {
                    ChunkCoord      = kvp.Key,
                    State           = ChunkState.Unloaded,
                    StaticDataReady = 0
                });
                EntityManager.SetComponentEnabled<ChunkTransitionRequest>(e, true);
                EntityManager.SetComponentData(e, new ChunkTransitionRequest
                {
                    ChunkCoord  = kvp.Key,
                    TargetState = kvp.Value
                });
                _chunkEntityMap[kvp.Key] = e;
            }

            // ── 3. Queue state transitions ────────────────────────────────────
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (chunk, entity) in SystemAPI.Query<RefRO<GridChunk>>().WithEntityAccess())
            {
                int2 coord = chunk.ValueRO.ChunkCoord;

                if (desiredStates.TryGetValue(coord, out ChunkState desired))
                {
                    if (chunk.ValueRO.State != desired)
                    {
                        ecb.SetComponentEnabled<ChunkTransitionRequest>(entity, true);
                        ecb.SetComponent(entity, new ChunkTransitionRequest
                        { ChunkCoord = coord, TargetState = desired });
                    }
                }
                else if (chunk.ValueRO.State != ChunkState.Unloaded)
                {
                    ecb.SetComponentEnabled<ChunkTransitionRequest>(entity, true);
                    ecb.SetComponent(entity, new ChunkTransitionRequest
                    { ChunkCoord = coord, TargetState = ChunkState.Unloaded });
                }
            }

            ecb.Playback(EntityManager);
            ecb.Dispose();

            // ── 4. Execute transitions ────────────────────────────────────────
            ProcessTransitions(config);

            desiredStates.Dispose();
        }

        // ── Transition processor ──────────────────────────────────────────────

        private void ProcessTransitions(GridConfig config)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (chunk, request, staticData, entity) in
                SystemAPI.Query<RefRW<GridChunk>, RefRO<ChunkTransitionRequest>,
                                RefRW<ChunkStaticData>>()
                    .WithAll<ChunkTransitionRequest>()
                    .WithEntityAccess())
            {
                ChunkState from = chunk.ValueRO.State;
                ChunkState to   = request.ValueRO.TargetState;

                if (from == to) { ecb.SetComponentEnabled<ChunkTransitionRequest>(entity, false); continue; }

                if (from == ChunkState.Unloaded && to >= ChunkState.Ghost)
                {
                    var sd = staticData.ValueRW;
                    BakeChunkStatic(chunk.ValueRO.ChunkCoord, config, ref sd);
                    staticData.ValueRW = sd;

                    chunk.ValueRW.State           = ChunkState.Ghost;
                    chunk.ValueRW.StaticDataReady = 1;

                    if (to == ChunkState.Active)
                    {
                        AllocateDynamicData(entity, config, ecb);
                        chunk.ValueRW.State = ChunkState.Active;
                    }
                }
                else if (from == ChunkState.Ghost && to == ChunkState.Active)
                {
                    AllocateDynamicData(entity, config, ecb);
                    chunk.ValueRW.State = ChunkState.Active;
                }
                else if (from == ChunkState.Active && to == ChunkState.Ghost)
                {
                    DisposeDynamicData(entity);
                    ecb.RemoveComponent<ChunkDynamicData>(entity);
                    chunk.ValueRW.State = ChunkState.Ghost;
                }
                else if (to == ChunkState.Unloaded)
                {
                    if (from == ChunkState.Active)
                    {
                        DisposeDynamicData(entity);
                        ecb.RemoveComponent<ChunkDynamicData>(entity);
                    }
                    var sd = staticData.ValueRW;
                    DisposeStaticData(ref sd);
                    staticData.ValueRW = sd;

                    chunk.ValueRW.State           = ChunkState.Unloaded;
                    chunk.ValueRW.StaticDataReady = 0;
                    _chunkEntityMap.Remove(chunk.ValueRO.ChunkCoord);
                    ecb.DestroyEntity(entity);
                }

                ecb.SetComponentEnabled<ChunkTransitionRequest>(entity, false);
            }

            ecb.Playback(EntityManager);
            ecb.Dispose();
        }

        // ── Static bake ───────────────────────────────────────────────────────

        private void BakeChunkStatic(int2 chunkCoord, GridConfig config, ref ChunkStaticData staticData)
        {
            int    cellCount = config.ChunkCellCount;
            int    total     = cellCount * cellCount;
            float3 origin    = ChunkCoordToWorld(chunkCoord, config);

            var builder = new BlobBuilder(Allocator.Temp);
            ref ChunkStaticBlob blob = ref builder.ConstructRoot<ChunkStaticBlob>();
            blob.ChunkCoord = chunkCoord;
            blob.CellCount  = cellCount;

            var nodesArr = builder.Allocate(ref blob.Nodes, total);
            var macroArr = builder.Allocate(ref blob.MacroConnectivity, 8);

            for (int z = 0; z < cellCount; z++)
            for (int x = 0; x < cellCount; x++)
            {
                float3 cellCentre = origin + new float3(
                    (x + 0.5f) * config.CellSize,
                    0f,
                    (z + 0.5f) * config.CellSize);
                nodesArr[z * cellCount + x] = BakeNode(cellCentre, config);
            }

            BakeMacroConnectivity(ref macroArr, origin, config);
            staticData.Blob = builder.CreateBlobAssetReference<ChunkStaticBlob>(Allocator.Persistent);
            builder.Dispose();
        }

        private NodeStatic BakeNode(float3 cellCentre, GridConfig config)
        {
            var node      = new NodeStatic();
            var rayOrigin = new Vector3(
                cellCentre.x,
                cellCentre.y + config.BakeRaycastHeight,
                cellCentre.z);

            if (!Physics.Raycast(rayOrigin, Vector3.down,
                    out RaycastHit hit,
                    config.BakeRaycastHeight + 2f,
                    config.GroundPhysicsLayer))
            {
                node.WalkableLayerMask = 0;
                node.SlopeFlags        = 1;
                return node;
            }

            float slope = Vector3.Angle(hit.normal, Vector3.up);
            if (slope > config.MaxSlopeAngle)
            {
                node.SlopeFlags        = 1;
                node.WalkableLayerMask = 0b00000010;
            }
            else
            {
                node.SlopeFlags        = 0;
                node.WalkableLayerMask = 0b11111111;
            }

            // Eroded box check — catches obstacles touching cell boundary at any height.
            float  cellHalf       = config.CellSize * 0.5f;
            float  boxHalf        = cellHalf + config.AgentRadius;
            float  boxCheckHeight = 1f;
            var    boxCenter      = new Vector3(hit.point.x, hit.point.y + boxCheckHeight, hit.point.z);
            var    halfExtents    = new Vector3(boxHalf, boxCheckHeight, boxHalf);

            if (Physics.CheckBox(boxCenter, halfExtents, Quaternion.identity, config.UnwalkablePhysicsLayer))
                node.WalkableLayerMask = 0;

            node.TerrainCostMask = 0;
            return node;
        }

        private void BakeMacroConnectivity(ref BlobBuilderArray<byte> macroArr,
                                            float3 origin, GridConfig config)
        {
            float  half   = config.ChunkCellCount * config.CellSize * 0.5f;
            float3 centre = origin + new float3(half, 0, half);

            int2[] dirs =
            {
                new int2( 0,  1), new int2( 1,  1), new int2( 1,  0), new int2( 1, -1),
                new int2( 0, -1), new int2(-1, -1), new int2(-1,  0), new int2(-1,  1)
            };

            for (int i = 0; i < 8; i++)
            {
                float3 edge      = centre + new float3(dirs[i].x * half, 0, dirs[i].y * half);
                var    rayOrigin = new Vector3(edge.x, edge.y + config.BakeRaycastHeight, edge.z);
                bool   hit       = Physics.Raycast(rayOrigin, Vector3.down,
                                       config.BakeRaycastHeight + 2f, config.GroundPhysicsLayer);
                macroArr[i] = hit ? (byte)10 : (byte)0;
            }
        }

        // ── Dynamic data helpers ──────────────────────────────────────────────

        private void AllocateDynamicData(Entity entity, GridConfig config,
                                          EntityCommandBuffer ecb)
        {
            int total = config.ChunkCellCount * config.ChunkCellCount;
            ecb.AddComponent(entity, new ChunkDynamicData
            {
                Nodes       = new NativeArray<NodeDynamic>(total, Allocator.Persistent),
                IsAllocated = 1
            });
        }

        private void DisposeDynamicData(Entity entity)
        {
            if (!EntityManager.HasComponent<ChunkDynamicData>(entity)) return;
            var dyn = EntityManager.GetComponentData<ChunkDynamicData>(entity);
            if (dyn.IsAllocated == 1 && dyn.Nodes.IsCreated) dyn.Nodes.Dispose();
        }

        private static void DisposeStaticData(ref ChunkStaticData sd)
        {
            if (sd.Blob.IsCreated) sd.Blob.Dispose();
        }

        // =========================================================================
        // PUBLIC COORDINATE UTILITIES
        // (static so A*, FlowField, buildings, fog-of-war etc. can all call them)
        // =========================================================================

        public static float3 ChunkCoordToWorld(int2 coord, GridConfig config)
        {
            float s = config.ChunkCellCount * config.CellSize;
            return new float3(coord.x * s, 0, coord.y * s);
        }

        public static int2 WorldToChunkCoord(float3 worldPos, GridConfig config)
        {
            float s = config.ChunkCellCount * config.CellSize;
            return new int2(
                (int)math.floor(worldPos.x / s),
                (int)math.floor(worldPos.z / s));
        }

        public static int2 WorldToCellLocal(float3 worldPos, int2 chunkCoord, GridConfig config)
        {
            float3 local = worldPos - ChunkCoordToWorld(chunkCoord, config);
            return new int2(
                (int)math.floor(local.x / config.CellSize),
                (int)math.floor(local.z / config.CellSize));
        }

        public static int CellLocalToIndex(int2 localCell, int chunkCellCount)
            => localCell.y * chunkCellCount + localCell.x;

        /// <summary>World-space centre of a local cell inside a chunk.</summary>
        public static float3 CellLocalToWorld(int2 localCell, int2 chunkCoord, GridConfig config)
        {
            float3 origin = ChunkCoordToWorld(chunkCoord, config);
            return origin + new float3(
                (localCell.x + 0.5f) * config.CellSize,
                0f,
                (localCell.y + 0.5f) * config.CellSize);
        }

        // ── Chunk entity lookup ───────────────────────────────────────────────

        public bool TryGetChunkEntity(int2 coord, out Entity entity)
            => _chunkEntityMap.TryGetValue(coord, out entity);
    }

    // =========================================================================
    // GRID CELL WRITE SYSTEM
    // Processes GridCellRequest buffer → mutates ChunkDynamicData.
    // Runs in InitializationSystemGroup after GridManagerSystem so dynamic data
    // is up to date before Simulation (A*, FlowField, movement) reads it.
    //
    // HOW TO USE FROM A BUILDING SYSTEM:
    //
    //   var buf = SystemAPI.GetSingletonBuffer<GridCellRequest>(isReadOnly: false);
    //
    //   // Block cells under a new building (bit 0 = structure)
    //   foreach (float3 cellWorld in buildingCells)
    //       buf.Add(new GridCellRequest
    //       {
    //           WorldPosition = cellWorld,
    //           Type          = CellRequestType.SetBlock,
    //           Arg           = 0
    //       });
    //
    //   // When building is demolished, clear the same cells:
    //       buf.Add(new GridCellRequest
    //       {
    //           WorldPosition = cellWorld,
    //           Type          = CellRequestType.ClearBlock,
    //           Arg           = 0
    //       });
    // =========================================================================

    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [UpdateAfter(typeof(GridManagerSystem))]
    public partial class GridCellWriteSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<GridConfig>();
            RequireForUpdate<GridManagerTag>();
        }

        protected override void OnUpdate()
        {
            var config = SystemAPI.GetSingleton<GridConfig>();

            // Grab the request buffer from the GridManager singleton entity
            Entity managerEntity = SystemAPI.GetSingletonEntity<GridManagerTag>();
            var    requests      = EntityManager.GetBuffer<GridCellRequest>(managerEntity);

            if (requests.Length == 0) return;

            // Build a lookup: chunk coord → dynamic data component + entity
            var chunkDynMap = new NativeHashMap<int2, Entity>(64, Allocator.Temp);
            foreach (var (chunk, entity) in
                SystemAPI.Query<RefRO<GridChunk>>().WithAll<ChunkDynamicData>().WithEntityAccess())
            {
                chunkDynMap[chunk.ValueRO.ChunkCoord] = entity;
            }

            foreach (var req in requests)
            {
                int2 chunkCoord = GridManagerSystem.WorldToChunkCoord(req.WorldPosition, config);
                if (!chunkDynMap.TryGetValue(chunkCoord, out Entity chunkEntity)) continue;

                var dynData  = EntityManager.GetComponentData<ChunkDynamicData>(chunkEntity);
                if (dynData.IsAllocated == 0 || !dynData.Nodes.IsCreated) continue;

                int2 localCell = GridManagerSystem.WorldToCellLocal(req.WorldPosition, chunkCoord, config);
                localCell = math.clamp(localCell,
                    int2.zero,
                    new int2(config.ChunkCellCount - 1, config.ChunkCellCount - 1));

                int idx  = GridManagerSystem.CellLocalToIndex(localCell, config.ChunkCellCount);
                var node = dynData.Nodes[idx];

                switch (req.Type)
                {
                    case CellRequestType.Occupy:
                        node.OccupancyCount = (byte)math.min(255, node.OccupancyCount + 1);
                        break;
                    case CellRequestType.Vacate:
                        node.OccupancyCount = (byte)math.max(0, node.OccupancyCount - 1);
                        break;
                    case CellRequestType.SetBlock:
                        node.BlockFlags = (byte)(node.BlockFlags | (1 << req.Arg));
                        break;
                    case CellRequestType.ClearBlock:
                        node.BlockFlags = (byte)(node.BlockFlags & ~(1 << req.Arg));
                        break;
                    case CellRequestType.Reset:
                        node.OccupancyCount = 0;
                        node.BlockFlags     = 0;
                        break;
                }

                dynData.Nodes[idx] = node;
                // NativeArray is a reference type inside the component, so we must
                // write the component back to make ECS dirty tracking aware.
                EntityManager.SetComponentData(chunkEntity, dynData);
            }

            requests.Clear();
            chunkDynMap.Dispose();
        }
    }
}
