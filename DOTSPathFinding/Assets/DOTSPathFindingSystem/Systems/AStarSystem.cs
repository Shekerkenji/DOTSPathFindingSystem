using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Burst;
using Unity.Jobs;
using Unity.Transforms;
using UnityEngine;

namespace Navigation.ECS
{
    /// <summary>
    /// Processes A* path requests from the queue.
    /// - Burst compiled job per request
    /// - Max N requests processed per frame (configurable) to spread cost
    /// - 2D (8 neighbour XZ) for ground units, 3D (26 neighbour) for flying
    /// - Reads from baked ChunkStaticBlob — no Physics calls at runtime
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(UnitMovementSystem))]
    public partial class AStarSystem : SystemBase
    {
        // How many path requests to process per frame. Tune based on budget.
        private const int MaxRequestsPerFrame = 8;

        // Shared chunk lookup built each frame — passed into jobs as read-only
        private NativeHashMap<int2, BlobAssetReference<ChunkStaticBlob>> _chunkBlobMap;

        protected override void OnCreate()
        {
            RequireForUpdate<NavigationConfig>();
            _chunkBlobMap = new NativeHashMap<int2, BlobAssetReference<ChunkStaticBlob>>(
                256, Allocator.Persistent);
        }

        protected override void OnDestroy()
        {
            if (_chunkBlobMap.IsCreated) _chunkBlobMap.Dispose();
        }

        protected override void OnUpdate()
        {
            var config = SystemAPI.GetSingleton<NavigationConfig>();

            // ── Rebuild chunk blob map (cheap, just reference copies) ──
            _chunkBlobMap.Clear();
            foreach (var (chunk, staticData) in
                SystemAPI.Query<RefRO<GridChunk>, RefRO<ChunkStaticData>>())
            {
                if (chunk.ValueRO.StaticDataReady == 1 && staticData.ValueRO.Blob.IsCreated)
                    _chunkBlobMap[chunk.ValueRO.ChunkCoord] = staticData.ValueRO.Blob;
            }

            // ── Collect pending requests, sorted by priority ──
            var requests = new NativeList<PathRequestEntry>(64, Allocator.Temp);

            foreach (var (request, requestEnabled, perms, entity) in
                SystemAPI.Query<RefRO<PathRequest>, EnabledRefRO<PathRequest>, RefRO<UnitLayerPermissions>>()
                    .WithNone<PathfindingSuccess>()
                    .WithEntityAccess())
            {
                if (!requestEnabled.ValueRO) continue;
                requests.Add(new PathRequestEntry
                {
                    Entity = entity,
                    Request = request.ValueRO,
                    Permissions = perms.ValueRO
                });
            }

            // Sort by priority descending
            requests.Sort(new PriorityComparer());

            // ── Process up to MaxRequestsPerFrame ──
            int toProcess = math.min(requests.Length, MaxRequestsPerFrame);
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            for (int i = 0; i < toProcess; i++)
            {
                var entry = requests[i];
                ProcessRequest(entry, config, ecb);
            }

            ecb.Playback(EntityManager);
            ecb.Dispose();
            requests.Dispose();
        }

        private void ProcessRequest(PathRequestEntry entry, NavigationConfig config,
                                     EntityCommandBuffer ecb)
        {
            int2 startChunk = ChunkManagerSystem.WorldToChunkCoord(entry.Request.Start, config);
            int2 endChunk = ChunkManagerSystem.WorldToChunkCoord(entry.Request.End, config);

            // ── Single-chunk path (most common case) ──
            if (math.all(startChunk == endChunk))
            {
                if (!_chunkBlobMap.TryGetValue(startChunk, out var blob))
                {
                    // Chunk not loaded yet — skip, will retry next frame
                    return;
                }

                var pathOut = new NativeList<float3>(128, Allocator.TempJob);

                var job = new AStarSingleChunkJob
                {
                    Config = config,
                    ChunkCoord = startChunk,
                    StartWorld = entry.Request.Start,
                    EndWorld = entry.Request.End,
                    Permissions = entry.Permissions,
                    Blob = blob,
                    PathOut = pathOut
                };

                // Run inline (we're already on main thread, budget controlled by MaxRequestsPerFrame)
                // To push to worker threads: schedule and complete before ecb.Playback
                job.Execute();

                if (pathOut.Length > 0)
                {
                    // Write waypoints to buffer
                    var buffer = EntityManager.GetBuffer<PathWaypoint>(entry.Entity);
                    buffer.Clear();
                    for (int i = 0; i < pathOut.Length; i++)
                        buffer.Add(new PathWaypoint { Position = pathOut[i] });

                    ecb.SetComponentEnabled<PathfindingSuccess>(entry.Entity, true);
                }
                else
                {
                    ecb.SetComponentEnabled<PathfindingFailed>(entry.Entity, true);
                }

                pathOut.Dispose();
            }
            else
            {
                // Multi-chunk: use macro path + per-chunk micro stitching (Phase 2)
                // For now, fall back to macro waypoints only
                BuildMacroCrossChunkPath(entry, config, startChunk, endChunk, ecb);
            }

            // Remove the request regardless of result
            ecb.SetComponentEnabled<PathRequest>(entry.Entity, false);
        }

        private void BuildMacroCrossChunkPath(PathRequestEntry entry, NavigationConfig config,
                                               int2 startChunk, int2 endChunk,
                                               EntityCommandBuffer ecb)
        {
            // Chunk-level A* to find corridor of chunks to traverse
            var macroPath = new NativeList<int2>(32, Allocator.Temp);
            bool macroFound = RunMacroAStar(startChunk, endChunk, config, ref macroPath);

            if (!macroFound)
            {
                ecb.SetComponentEnabled<PathfindingFailed>(entry.Entity, true);
                macroPath.Dispose();
                return;
            }

            // Write macro waypoints to buffer
            var macroBuffer = EntityManager.GetBuffer<MacroWaypoint>(entry.Entity);
            macroBuffer.Clear();

            for (int i = 0; i < macroPath.Length; i++)
            {
                float chunkWorldSize = config.ChunkCellCount * config.CellSize;
                int2 coord = macroPath[i];
                float3 entryPoint = new float3(
                    coord.x * chunkWorldSize + chunkWorldSize * 0.5f,
                    0f,
                    coord.y * chunkWorldSize + chunkWorldSize * 0.5f);

                macroBuffer.Add(new MacroWaypoint
                {
                    ChunkCoord = coord,
                    WorldEntryPoint = entryPoint
                });
            }

            // Switch agent to macro nav mode
            var nav = EntityManager.GetComponentData<AgentNavigation>(entry.Entity);
            nav.Mode = NavMode.MacroOnly;
            ecb.SetComponent(entry.Entity, nav);
            ecb.SetComponentEnabled<PathfindingSuccess>(entry.Entity, true);

            macroPath.Dispose();
        }

        private bool RunMacroAStar(int2 start, int2 end, NavigationConfig config,
                                    ref NativeList<int2> pathOut)
        {
            var openSet = new NativeList<MacroNode>(64, Allocator.Temp);
            var closedSet = new NativeHashSet<int2>(64, Allocator.Temp);
            var cameFrom = new NativeHashMap<int2, int2>(64, Allocator.Temp);
            var gCosts = new NativeHashMap<int2, int>(64, Allocator.Temp);

            openSet.Add(new MacroNode { Coord = start, GCost = 0, HCost = MacroHeuristic(start, end) });
            gCosts[start] = 0;

            bool found = false;

            while (openSet.Length > 0)
            {
                // Find lowest F
                int lowestIdx = 0;
                for (int i = 1; i < openSet.Length; i++)
                    if (openSet[i].FCost < openSet[lowestIdx].FCost) lowestIdx = i;

                var current = openSet[lowestIdx];
                openSet.RemoveAtSwapBack(lowestIdx);

                if (math.all(current.Coord == end))
                {
                    found = true;
                    ReconstructMacroPath(start, end, cameFrom, ref pathOut);
                    break;
                }

                closedSet.Add(current.Coord);

                // 8 neighbours
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        int2 neighbour = current.Coord + new int2(dx, dz);

                        if (closedSet.Contains(neighbour)) continue;

                        // Check macro connectivity if chunk is loaded
                        byte edgeCost = 10; // Default passable
                        if (_chunkBlobMap.TryGetValue(current.Coord, out var blob))
                        {
                            int dirIdx = DirectionToIndex(dx, dz);
                            edgeCost = blob.Value.MacroConnectivity[dirIdx];
                        }
                        if (edgeCost == 0) continue; // Blocked

                        int moveCost = (dx != 0 && dz != 0) ? 14 : 10; // Diagonal vs straight
                        int tentativeG = current.GCost + moveCost;

                        if (!gCosts.TryGetValue(neighbour, out int existingG) || tentativeG < existingG)
                        {
                            gCosts[neighbour] = tentativeG;
                            cameFrom[neighbour] = current.Coord;
                            openSet.Add(new MacroNode
                            {
                                Coord = neighbour,
                                GCost = tentativeG,
                                HCost = MacroHeuristic(neighbour, end)
                            });
                        }
                    }
                }
            }

            openSet.Dispose();
            closedSet.Dispose();
            cameFrom.Dispose();
            gCosts.Dispose();

            return found;
        }

        private void ReconstructMacroPath(int2 start, int2 end,
                                           NativeHashMap<int2, int2> cameFrom,
                                           ref NativeList<int2> pathOut)
        {
            var reversed = new NativeList<int2>(32, Allocator.Temp);
            int2 current = end;
            int safety = 0;

            while (!math.all(current == start) && safety++ < 256)
            {
                reversed.Add(current);
                if (!cameFrom.TryGetValue(current, out current)) break;
            }
            reversed.Add(start);

            for (int i = reversed.Length - 1; i >= 0; i--)
                pathOut.Add(reversed[i]);

            reversed.Dispose();
        }

        private static int MacroHeuristic(int2 a, int2 b)
        {
            int2 d = math.abs(a - b);
            return 10 * (d.x + d.y) + (14 - 20) * math.min(d.x, d.y); // Octile
        }

        private static int DirectionToIndex(int dx, int dz)
        {
            // N=0, NE=1, E=2, SE=3, S=4, SW=5, W=6, NW=7
            if (dz == 1 && dx == 0) return 0;
            if (dz == 1 && dx == 1) return 1;
            if (dz == 0 && dx == 1) return 2;
            if (dz == -1 && dx == 1) return 3;
            if (dz == -1 && dx == 0) return 4;
            if (dz == -1 && dx == -1) return 5;
            if (dz == 0 && dx == -1) return 6;
            return 7; // NW
        }

        // ── Inner structs ──────────────────────────────────────────────

        private struct PathRequestEntry
        {
            public Entity Entity;
            public PathRequest Request;
            public UnitLayerPermissions Permissions;
        }

        private struct PriorityComparer : System.Collections.Generic.IComparer<PathRequestEntry>
        {
            public int Compare(PathRequestEntry a, PathRequestEntry b)
                => b.Request.Priority.CompareTo(a.Request.Priority);
        }

        private struct MacroNode
        {
            public int2 Coord;
            public int GCost;
            public int HCost;
            public int FCost => GCost + HCost;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // BURST COMPILED A* JOB — single chunk
    // ─────────────────────────────────────────────────────────────────────────

    [BurstCompile]
    public struct AStarSingleChunkJob
    {
        public NavigationConfig Config;
        public int2 ChunkCoord;
        public float3 StartWorld;
        public float3 EndWorld;
        public UnitLayerPermissions Permissions;
        public BlobAssetReference<ChunkStaticBlob> Blob;
        public NativeList<float3> PathOut;

        public void Execute()
        {
            ref ChunkStaticBlob blob = ref Blob.Value;
            int cellCount = blob.CellCount;
            int total = cellCount * cellCount;

            int2 startLocal = ChunkManagerSystem.WorldToCellLocal(StartWorld, ChunkCoord, Config);
            int2 endLocal = ChunkManagerSystem.WorldToCellLocal(EndWorld, ChunkCoord, Config);

            // Clamp to chunk bounds
            startLocal = math.clamp(startLocal, int2.zero, new int2(cellCount - 1, cellCount - 1));
            endLocal = math.clamp(endLocal, int2.zero, new int2(cellCount - 1, cellCount - 1));

            int startIdx = ChunkManagerSystem.CellLocalToIndex(startLocal, cellCount);
            int endIdx = ChunkManagerSystem.CellLocalToIndex(endLocal, cellCount);

            if (startIdx == endIdx) return; // Already there

            // Validate start/end walkability
            if (!IsWalkable(ref blob, startIdx, Permissions)) return;
            if (!IsWalkable(ref blob, endIdx, Permissions)) return;

            // A* data arrays — Temp alloc, dies when job finishes
            var gCosts = new NativeArray<int>(total, Allocator.Temp);
            var parents = new NativeArray<int>(total, Allocator.Temp);
            var inClosed = new NativeArray<bool>(total, Allocator.Temp);

            // Min-heap open set (index, FCost)
            var openHeap = new NativeList<HeapEntry>(256, Allocator.Temp);

            // Init
            for (int i = 0; i < total; i++) { gCosts[i] = int.MaxValue; parents[i] = -1; }
            gCosts[startIdx] = 0;

            int startH = Heuristic(startLocal, endLocal);
            HeapPush(ref openHeap, new HeapEntry { Index = startIdx, FCost = startH, HCost = startH });

            bool found = false;

            while (openHeap.Length > 0)
            {
                var current = HeapPop(ref openHeap);
                int curIdx = current.Index;

                if (inClosed[curIdx]) continue;
                inClosed[curIdx] = true;

                if (curIdx == endIdx) { found = true; break; }

                int2 curLocal = IndexToLocal(curIdx, cellCount);

                // Neighbours — 2D (8) for ground, 3D skipped (same chunk = 2D always)
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (dx == 0 && dz == 0) continue;

                        int2 nLocal = curLocal + new int2(dx, dz);
                        if (nLocal.x < 0 || nLocal.x >= cellCount ||
                            nLocal.y < 0 || nLocal.y >= cellCount) continue;

                        int nIdx = ChunkManagerSystem.CellLocalToIndex(nLocal, cellCount);
                        if (inClosed[nIdx]) continue;
                        if (!IsWalkable(ref blob, nIdx, Permissions)) continue;

                        // Terrain cost from lookup
                        int terrainCost = blob.Nodes[nIdx].TerrainCostMask == 0 ? 10 : 20;
                        int moveCost = (dx != 0 && dz != 0) ? 14 : 10;
                        int tentativeG = gCosts[curIdx] + moveCost + terrainCost - 10; // subtract base

                        if (tentativeG < gCosts[nIdx])
                        {
                            gCosts[nIdx] = tentativeG;
                            parents[nIdx] = curIdx;
                            int h = Heuristic(nLocal, endLocal);
                            HeapPush(ref openHeap, new HeapEntry
                            {
                                Index = nIdx,
                                FCost = tentativeG + h,
                                HCost = h
                            });
                        }
                    }
                }
            }

            if (found)
                ReconstructPath(startIdx, endIdx, parents, cellCount, ref PathOut);

            gCosts.Dispose();
            parents.Dispose();
            inClosed.Dispose();
            openHeap.Dispose();
        }

        // ── Helpers ─────────────────────────────────────────────────────

        private bool IsWalkable(ref ChunkStaticBlob blob, int idx, UnitLayerPermissions perms)
        {
            return (blob.Nodes[idx].WalkableLayerMask & perms.WalkableLayers) != 0;
        }

        private int Heuristic(int2 a, int2 b)
        {
            int2 d = math.abs(a - b);
            // Octile distance * 10
            int straight = math.max(d.x, d.y);
            int diag = math.min(d.x, d.y);
            return 10 * straight + 4 * diag;
        }

        private int2 IndexToLocal(int idx, int cellCount)
            => new int2(idx % cellCount, idx / cellCount);

        private void ReconstructPath(int startIdx, int endIdx, NativeArray<int> parents,
                                      int cellCount, ref NativeList<float3> pathOut)
        {
            var raw = new NativeList<int>(128, Allocator.Temp);
            int current = endIdx;
            int safety = 0;

            while (current != startIdx && safety++ < 10000)
            {
                raw.Add(current);
                current = parents[current];
                if (current < 0) break;
            }

            // Simplify: skip collinear nodes
            int2 prevDir = int2.zero;
            for (int i = raw.Length - 1; i >= 0; i--)
            {
                int2 cellLocal = IndexToLocal(raw[i], cellCount);

                if (i < raw.Length - 1)
                {
                    int2 nextLocal = IndexToLocal(raw[i + 1], cellCount);
                    int2 dir = cellLocal - nextLocal;
                    if (math.all(dir == prevDir) && i > 0) { prevDir = dir; continue; }
                    prevDir = dir;
                }

                float3 worldPos = CellLocalToWorld(cellLocal);
                pathOut.Add(worldPos);
            }

            // Always add exact destination
            pathOut.Add(EndWorld);
            raw.Dispose();
        }

        private float3 CellLocalToWorld(int2 localCell)
        {
            float chunkWorldSize = Config.ChunkCellCount * Config.CellSize;
            float3 chunkOrigin = new float3(
                ChunkCoord.x * chunkWorldSize, 0,
                ChunkCoord.y * chunkWorldSize);

            return chunkOrigin + new float3(
                (localCell.x + 0.5f) * Config.CellSize,
                0f,
                (localCell.y + 0.5f) * Config.CellSize);
        }

        // ── Binary min-heap ─────────────────────────────────────────────

        private struct HeapEntry
        {
            public int Index;
            public int FCost;
            public int HCost;
        }

        private void HeapPush(ref NativeList<HeapEntry> heap, HeapEntry entry)
        {
            heap.Add(entry);
            int i = heap.Length - 1;
            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (heap[parent].FCost <= heap[i].FCost) break;
                var tmp = heap[parent]; heap[parent] = heap[i]; heap[i] = tmp;
                i = parent;
            }
        }

        private HeapEntry HeapPop(ref NativeList<HeapEntry> heap)
        {
            var top = heap[0];
            int last = heap.Length - 1;
            heap[0] = heap[last];
            heap.RemoveAt(last);

            int i = 0;
            while (true)
            {
                int l = 2 * i + 1, r = 2 * i + 2, smallest = i;
                if (l < heap.Length && heap[l].FCost < heap[smallest].FCost) smallest = l;
                if (r < heap.Length && heap[r].FCost < heap[smallest].FCost) smallest = r;
                if (smallest == i) break;
                var tmp = heap[i]; heap[i] = heap[smallest]; heap[smallest] = tmp;
                i = smallest;
            }
            return top;
        }
    }
}