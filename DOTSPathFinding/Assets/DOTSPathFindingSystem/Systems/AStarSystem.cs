using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace Shek.ECSNavigation
{
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(UnitMovementSystem))]
    public partial class AStarSystem : SystemBase
    {
        private const int MaxRequestsPerFrame = 32;
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

            _chunkBlobMap.Clear();
            foreach (var (chunk, staticData) in
                SystemAPI.Query<RefRO<GridChunk>, RefRO<ChunkStaticData>>())
            {
                if (chunk.ValueRO.StaticDataReady == 1 && staticData.ValueRO.Blob.IsCreated)
                    _chunkBlobMap[chunk.ValueRO.ChunkCoord] = staticData.ValueRO.Blob;
            }

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

            requests.Sort(new PriorityComparer());

            int toProcess = math.min(requests.Length, MaxRequestsPerFrame);
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            for (int i = 0; i < toProcess; i++)
                ProcessRequest(requests[i], config, ecb);

            ecb.Playback(EntityManager);
            ecb.Dispose();
            requests.Dispose();
        }

        private void ProcessRequest(PathRequestEntry entry, NavigationConfig config,
                                     EntityCommandBuffer ecb)
        {
            int2 startChunk = ChunkManagerSystem.WorldToChunkCoord(entry.Request.Start, config);
            int2 endChunk = ChunkManagerSystem.WorldToChunkCoord(entry.Request.End, config);

            bool startLoaded = _chunkBlobMap.TryGetValue(startChunk, out var startBlob);
            bool endLoaded = _chunkBlobMap.TryGetValue(endChunk, out var endBlob);

            if (math.all(startChunk == endChunk))
            {
                if (!startLoaded) return;
                RunAStarInChunk(entry, config, startChunk, startBlob,
                                entry.Request.Start, entry.Request.End, ecb);
            }
            else if (startLoaded && endLoaded)
            {
                // Both chunks loaded — run multi-chunk A* so walls in ALL
                // traversed chunks are respected, not just the destination chunk.
                RunAStarMultiChunk(entry, config, ecb);
            }
            else if (endLoaded)
            {
                // Start chunk not loaded yet — clamp start into end chunk as fallback
                float3 effectiveStart = ClampWorldPosToChunk(entry.Request.Start, endChunk, config);
                RunAStarInChunk(entry, config, endChunk, endBlob,
                                effectiveStart, entry.Request.End, ecb);
            }
            else
            {
                BuildMacroCrossChunkPath(entry, config, startChunk, endChunk, ecb);
            }

            ecb.SetComponentEnabled<PathRequest>(entry.Entity, false);
        }

        /// <summary>
        /// A* across multiple loaded chunks. Converts all world positions to a single
        /// flat cell index space covering all loaded chunks, then runs A* once.
        /// Each cell index encodes (chunkCoord, localCell) so walls in every chunk are checked.
        /// </summary>
        private void RunAStarMultiChunk(PathRequestEntry entry, NavigationConfig config,
                                         EntityCommandBuffer ecb)
        {
            // Build a sorted list of loaded chunk coords for stable indexing
            var chunkCoords = new NativeList<int2>(_chunkBlobMap.Count, Allocator.Temp);
            var chunkIndexMap = new NativeHashMap<int2, int>(_chunkBlobMap.Count, Allocator.Temp);
            foreach (var kvp in _chunkBlobMap)
            {
                chunkIndexMap[kvp.Key] = chunkCoords.Length;
                chunkCoords.Add(kvp.Key);
            }

            int cellCount = config.ChunkCellCount;
            int cellsPerChunk = cellCount * cellCount;

            int2 startChunk = ChunkManagerSystem.WorldToChunkCoord(entry.Request.Start, config);
            int2 endChunk = ChunkManagerSystem.WorldToChunkCoord(entry.Request.End, config);

            if (!chunkIndexMap.TryGetValue(startChunk, out int startChunkIdx) ||
                !chunkIndexMap.TryGetValue(endChunk, out int endChunkIdx))
            {
                ecb.SetComponentEnabled<PathfindingFailed>(entry.Entity, true);
                chunkCoords.Dispose();
                chunkIndexMap.Dispose();
                return;
            }

            int2 startLocal = ChunkManagerSystem.WorldToCellLocal(entry.Request.Start, startChunk, config);
            int2 endLocal = ChunkManagerSystem.WorldToCellLocal(entry.Request.End, endChunk, config);
            startLocal = math.clamp(startLocal, int2.zero, new int2(cellCount - 1, cellCount - 1));
            endLocal = math.clamp(endLocal, int2.zero, new int2(cellCount - 1, cellCount - 1));

            int startGlobal = startChunkIdx * cellsPerChunk + ChunkManagerSystem.CellLocalToIndex(startLocal, cellCount);
            int endGlobal = endChunkIdx * cellsPerChunk + ChunkManagerSystem.CellLocalToIndex(endLocal, cellCount);

            int totalCells = chunkCoords.Length * cellsPerChunk;

            var gCosts = new NativeArray<int>(totalCells, Allocator.Temp);
            var parents = new NativeArray<int>(totalCells, Allocator.Temp);
            var inClosed = new NativeArray<bool>(totalCells, Allocator.Temp);
            var openHeap = new NativeList<MultiChunkHeapEntry>(512, Allocator.Temp);

            for (int i = 0; i < totalCells; i++) { gCosts[i] = int.MaxValue; parents[i] = -1; }

            // Snap start/end to walkable
            int snappedStart = SnapToWalkableGlobal(startGlobal, startChunkIdx, startLocal,
                cellCount, cellsPerChunk, chunkCoords, chunkIndexMap);
            int snappedEnd = SnapToWalkableGlobal(endGlobal, endChunkIdx, endLocal,
                cellCount, cellsPerChunk, chunkCoords, chunkIndexMap);

            if (snappedStart < 0 || snappedEnd < 0 || snappedStart == snappedEnd)
            {
                ecb.SetComponentEnabled<PathfindingFailed>(entry.Entity, true);
                goto Cleanup;
            }

            gCosts[snappedStart] = 0;
            int2 startLocalSnapped = new int2((snappedStart % cellsPerChunk) % cellCount,
                                              (snappedStart % cellsPerChunk) / cellCount);
            int2 endLocalSnapped = new int2((snappedEnd % cellsPerChunk) % cellCount,
                                              (snappedEnd % cellsPerChunk) / cellCount);
            int2 endChunkSnapped = chunkCoords[snappedEnd / cellsPerChunk];

            int startH = MultiChunkHeuristic(snappedStart, snappedEnd, cellCount, cellsPerChunk, chunkCoords, config);
            MultiHeapPush(ref openHeap, new MultiChunkHeapEntry { GlobalIdx = snappedStart, FCost = startH, HCost = startH });

            bool found = false;

            while (openHeap.Length > 0)
            {
                var current = MultiHeapPop(ref openHeap);
                int curGlobal = current.GlobalIdx;
                if (inClosed[curGlobal]) continue;
                inClosed[curGlobal] = true;
                if (curGlobal == snappedEnd) { found = true; break; }

                int curChunkIdx = curGlobal / cellsPerChunk;
                int curLocalIdx = curGlobal % cellsPerChunk;
                int2 curLocal = new int2(curLocalIdx % cellCount, curLocalIdx / cellCount);
                int2 curChunk = chunkCoords[curChunkIdx];
                ref ChunkStaticBlob curBlob = ref _chunkBlobMap[curChunk].Value;

                for (int dx = -1; dx <= 1; dx++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (dx == 0 && dz == 0) continue;

                        int2 nLocal = curLocal + new int2(dx, dz);
                        int2 nChunk = curChunk;
                        int nChunkIdx = curChunkIdx;

                        // Cross chunk boundary
                        if (nLocal.x < 0) { nLocal.x += cellCount; nChunk.x--; }
                        else if (nLocal.x >= cellCount) { nLocal.x -= cellCount; nChunk.x++; }
                        if (nLocal.y < 0) { nLocal.y += cellCount; nChunk.y--; }
                        else if (nLocal.y >= cellCount) { nLocal.y -= cellCount; nChunk.y++; }

                        if (!math.all(nChunk == curChunk))
                        {
                            if (!chunkIndexMap.TryGetValue(nChunk, out nChunkIdx)) continue;
                        }

                        int nLocalIdx = ChunkManagerSystem.CellLocalToIndex(nLocal, cellCount);
                        int nGlobal = nChunkIdx * cellsPerChunk + nLocalIdx;
                        if (inClosed[nGlobal]) continue;

                        ref ChunkStaticBlob nBlob = ref _chunkBlobMap[nChunk].Value;
                        if (nBlob.Nodes[nLocalIdx].WalkableLayerMask == 0) continue;
                        if (nBlob.Nodes[nLocalIdx].SlopeFlags == 1 && entry.Permissions.IsFlying == 0) continue;
                        if ((nBlob.Nodes[nLocalIdx].WalkableLayerMask & entry.Permissions.WalkableLayers) == 0) continue;

                        int terrainCost = nBlob.Nodes[nLocalIdx].TerrainCostMask == 0 ? 10 : 20;
                        int moveCost = (dx != 0 && dz != 0) ? 14 : 10;
                        int tentativeG = gCosts[curGlobal] + moveCost + terrainCost - 10;

                        if (tentativeG < gCosts[nGlobal])
                        {
                            gCosts[nGlobal] = tentativeG;
                            parents[nGlobal] = curGlobal;
                            int h = MultiChunkHeuristic(nGlobal, snappedEnd, cellCount, cellsPerChunk, chunkCoords, config);
                            MultiHeapPush(ref openHeap, new MultiChunkHeapEntry
                            { GlobalIdx = nGlobal, FCost = tentativeG + h, HCost = h });
                        }
                    }
            }

            if (found)
            {
                // Reconstruct path
                var raw = new NativeList<int>(256, Allocator.Temp);
                int cur = snappedEnd;
                int safety = 0;
                while (cur != snappedStart && safety++ < 50000)
                {
                    raw.Add(cur);
                    cur = parents[cur];
                    if (cur < 0) break;
                }

                var buffer = EntityManager.GetBuffer<PathWaypoint>(entry.Entity);
                buffer.Clear();

                for (int i = raw.Length - 1; i >= 0; i--)
                {
                    int g = raw[i];
                    int ci = g / cellsPerChunk;
                    int li = g % cellsPerChunk;
                    int2 lc = new int2(li % cellCount, li / cellCount);
                    int2 cc = chunkCoords[ci];
                    float chunkSize = cellCount * config.CellSize;
                    float3 wp = new float3(
                        cc.x * chunkSize + (lc.x + 0.5f) * config.CellSize,
                        0f,
                        cc.y * chunkSize + (lc.y + 0.5f) * config.CellSize);
                    buffer.Add(new PathWaypoint { Position = wp });
                }

                // Final destination — validate walkability
                int2 endWC = new int2(
                    (int)math.floor((entry.Request.End.x - endChunkSnapped.x * (float)(cellCount * config.CellSize)) / config.CellSize),
                    (int)math.floor((entry.Request.End.z - endChunkSnapped.y * (float)(cellCount * config.CellSize)) / config.CellSize));
                endWC = math.clamp(endWC, int2.zero, new int2(cellCount - 1, cellCount - 1));
                int endWIdx = ChunkManagerSystem.CellLocalToIndex(endWC, cellCount);
                ref ChunkStaticBlob endBlob2 = ref _chunkBlobMap[endChunkSnapped].Value;
                bool endWalkable = endBlob2.Nodes[endWIdx].WalkableLayerMask != 0;

                int2 snappedEndLocal = new int2((snappedEnd % cellsPerChunk) % cellCount,
                                                (snappedEnd % cellsPerChunk) / cellCount);
                int2 snappedEndChunk = chunkCoords[snappedEnd / cellsPerChunk];
                float snappedChunkSize = cellCount * config.CellSize;
                float3 snappedEndWorld = new float3(
                    snappedEndChunk.x * snappedChunkSize + (snappedEndLocal.x + 0.5f) * config.CellSize,
                    0f,
                    snappedEndChunk.y * snappedChunkSize + (snappedEndLocal.y + 0.5f) * config.CellSize);

                buffer.Add(new PathWaypoint { Position = endWalkable ? entry.Request.End : snappedEndWorld });
                ecb.SetComponentEnabled<PathfindingSuccess>(entry.Entity, true);
                raw.Dispose();
            }
            else
            {
                var buffer = EntityManager.GetBuffer<PathWaypoint>(entry.Entity);
                buffer.Clear();
                ecb.SetComponentEnabled<PathfindingFailed>(entry.Entity, true);
            }

        Cleanup:
            gCosts.Dispose();
            parents.Dispose();
            inClosed.Dispose();
            openHeap.Dispose();
            chunkCoords.Dispose();
            chunkIndexMap.Dispose();
        }

        private int SnapToWalkableGlobal(int globalIdx, int chunkIdx, int2 localCell,
            int cellCount, int cellsPerChunk,
            NativeList<int2> chunkCoords, NativeHashMap<int2, int> chunkIndexMap)
        {
            int2 chunkCoord = chunkCoords[chunkIdx];
            if (!_chunkBlobMap.TryGetValue(chunkCoord, out var blob)) return -1;
            ref ChunkStaticBlob b = ref blob.Value;

            if (b.Nodes[globalIdx % cellsPerChunk].WalkableLayerMask != 0) return globalIdx;

            // BFS outward
            var queue = new NativeList<int2>(32, Allocator.Temp);
            var visited = new NativeHashSet<int>(64, Allocator.Temp);
            queue.Add(localCell);
            visited.Add(globalIdx);
            int head = 0, result = -1;

            while (head < queue.Length)
            {
                int2 cur = queue[head++];
                int2 d = math.abs(cur - localCell);
                if (math.max(d.x, d.y) > 4) break;

                for (int dx = -1; dx <= 1; dx++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        int2 n = cur + new int2(dx, dz);
                        int2 nc = chunkCoord;
                        int nci = chunkIdx;
                        if (n.x < 0) { n.x += cellCount; nc.x--; }
                        else if (n.x >= cellCount) { n.x -= cellCount; nc.x++; }
                        if (n.y < 0) { n.y += cellCount; nc.y--; }
                        else if (n.y >= cellCount) { n.y -= cellCount; nc.y++; }
                        if (!math.all(nc == chunkCoord) && !chunkIndexMap.TryGetValue(nc, out nci)) continue;
                        int nli = ChunkManagerSystem.CellLocalToIndex(n, cellCount);
                        int ng = nci * cellsPerChunk + nli;
                        if (visited.Contains(ng)) continue;
                        visited.Add(ng);
                        if (!_chunkBlobMap.TryGetValue(nc, out var nb)) continue;
                        if (nb.Value.Nodes[nli].WalkableLayerMask != 0) { result = ng; goto BFSDone; }
                        queue.Add(n);
                    }
            }
        BFSDone:
            queue.Dispose();
            visited.Dispose();
            return result;
        }

        private int MultiChunkHeuristic(int aGlobal, int bGlobal, int cellCount, int cellsPerChunk,
            NativeList<int2> chunkCoords, NavigationConfig config)
        {
            int aci = aGlobal / cellsPerChunk, ali = aGlobal % cellsPerChunk;
            int bci = bGlobal / cellsPerChunk, bli = bGlobal % cellsPerChunk;
            int2 ac = chunkCoords[aci], bc = chunkCoords[bci];
            int2 al = new int2(ali % cellCount, ali / cellCount);
            int2 bl = new int2(bli % cellCount, bli / cellCount);
            // Convert to global cell coords for heuristic
            int2 ag = ac * cellCount + al;
            int2 bg = bc * cellCount + bl;
            int2 d = math.abs(ag - bg);
            return 10 * math.max(d.x, d.y) + 4 * math.min(d.x, d.y);
        }

        private struct MultiChunkHeapEntry { public int GlobalIdx; public int FCost; public int HCost; }

        private void MultiHeapPush(ref NativeList<MultiChunkHeapEntry> heap, MultiChunkHeapEntry e)
        {
            heap.Add(e);
            int i = heap.Length - 1;
            while (i > 0)
            {
                int p = (i - 1) / 2;
                if (heap[p].FCost <= heap[i].FCost) break;
                var tmp = heap[p]; heap[p] = heap[i]; heap[i] = tmp;
                i = p;
            }
        }

        private MultiChunkHeapEntry MultiHeapPop(ref NativeList<MultiChunkHeapEntry> heap)
        {
            var top = heap[0]; int last = heap.Length - 1;
            heap[0] = heap[last]; heap.RemoveAt(last);
            int i = 0;
            while (true)
            {
                int l = 2 * i + 1, r = 2 * i + 2, s = i;
                if (l < heap.Length && heap[l].FCost < heap[s].FCost) s = l;
                if (r < heap.Length && heap[r].FCost < heap[s].FCost) s = r;
                if (s == i) break;
                var tmp = heap[i]; heap[i] = heap[s]; heap[s] = tmp; i = s;
            }
            return top;
        }

        private static float3 ClampWorldPosToChunk(float3 worldPos, int2 chunkCoord,
                                                    NavigationConfig config)
        {
            float3 origin = ChunkManagerSystem.ChunkCoordToWorld(chunkCoord, config);
            float size = config.ChunkCellCount * config.CellSize;
            float half = config.CellSize * 0.5f;
            return new float3(
                math.clamp(worldPos.x, origin.x + half, origin.x + size - half),
                worldPos.y,
                math.clamp(worldPos.z, origin.z + half, origin.z + size - half));
        }

        private void RunAStarInChunk(PathRequestEntry entry, NavigationConfig config,
                                      int2 chunkCoord, BlobAssetReference<ChunkStaticBlob> blob,
                                      float3 startWorld, float3 endWorld,
                                      EntityCommandBuffer ecb)
        {
            var pathOut = new NativeList<float3>(128, Allocator.TempJob);
            var job = new AStarSingleChunkJob
            {
                Config = config,
                ChunkCoord = chunkCoord,
                StartWorld = startWorld,
                EndWorld = endWorld,
                Permissions = entry.Permissions,
                Blob = blob,
                PathOut = pathOut
            };
            job.Execute();

            if (pathOut.Length > 0)
            {
                var buffer = EntityManager.GetBuffer<PathWaypoint>(entry.Entity);
                buffer.Clear();
                for (int i = 0; i < pathOut.Length; i++)
                    buffer.Add(new PathWaypoint { Position = pathOut[i] });
                ecb.SetComponentEnabled<PathfindingSuccess>(entry.Entity, true);
            }
            else
            {
                // Clear stale waypoints so the unit does not keep following the old
                // path (which may cross walls) after a failed repath.
                var buffer = EntityManager.GetBuffer<PathWaypoint>(entry.Entity);
                buffer.Clear();
                ecb.SetComponentEnabled<PathfindingFailed>(entry.Entity, true);
            }
            pathOut.Dispose();
        }

        private void BuildMacroCrossChunkPath(PathRequestEntry entry, NavigationConfig config,
                                               int2 startChunk, int2 endChunk,
                                               EntityCommandBuffer ecb)
        {
            var macroPath = new NativeList<int2>(32, Allocator.Temp);
            bool macroFound = RunMacroAStar(startChunk, endChunk, config, ref macroPath);

            if (!macroFound)
            {
                ecb.SetComponentEnabled<PathfindingFailed>(entry.Entity, true);
                macroPath.Dispose();
                return;
            }

            var macroBuffer = EntityManager.GetBuffer<MacroWaypoint>(entry.Entity);
            macroBuffer.Clear();

            float chunkWorldSize = config.ChunkCellCount * config.CellSize;

            for (int i = 0; i < macroPath.Length; i++)
            {
                int2 coord = macroPath[i];
                if (math.all(coord == startChunk)) continue;

                float3 entryPoint = new float3(
                    coord.x * chunkWorldSize + chunkWorldSize * 0.5f,
                    0f,
                    coord.y * chunkWorldSize + chunkWorldSize * 0.5f);

                macroBuffer.Add(new MacroWaypoint { ChunkCoord = coord, WorldEntryPoint = entryPoint });
            }

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

                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        int2 neighbour = current.Coord + new int2(dx, dz);
                        if (closedSet.Contains(neighbour)) continue;

                        byte edgeCost = 10;
                        if (_chunkBlobMap.TryGetValue(current.Coord, out var blob))
                        {
                            int dirIdx = DirectionToIndex(dx, dz);
                            edgeCost = blob.Value.MacroConnectivity[dirIdx];
                        }
                        if (edgeCost == 0) continue;

                        int moveCost = (dx != 0 && dz != 0) ? 14 : 10;
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
            return 10 * (d.x + d.y) + (14 - 20) * math.min(d.x, d.y);
        }

        private static int DirectionToIndex(int dx, int dz)
        {
            if (dz == 1 && dx == 0) return 0;
            if (dz == 1 && dx == 1) return 1;
            if (dz == 0 && dx == 1) return 2;
            if (dz == -1 && dx == 1) return 3;
            if (dz == -1 && dx == 0) return 4;
            if (dz == -1 && dx == -1) return 5;
            if (dz == 0 && dx == -1) return 6;
            return 7;
        }

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

            startLocal = math.clamp(startLocal, int2.zero, new int2(cellCount - 1, cellCount - 1));
            endLocal = math.clamp(endLocal, int2.zero, new int2(cellCount - 1, cellCount - 1));

            int startIdx = ChunkManagerSystem.CellLocalToIndex(startLocal, cellCount);
            int endIdx = ChunkManagerSystem.CellLocalToIndex(endLocal, cellCount);

            if (startIdx == endIdx) return;

            // FIX: If start or end is unwalkable, snap to nearest walkable cell.
            // Previously returning early here caused agents in/near wall cells
            // to never get a path at all, then DispatchSystem re-issued endlessly.
            startIdx = FindNearestWalkable(ref blob, startIdx, startLocal, cellCount, Permissions);
            endIdx = FindNearestWalkable(ref blob, endIdx, endLocal, cellCount, Permissions);
            if (startIdx < 0 || endIdx < 0 || startIdx == endIdx) return;

            var gCosts = new NativeArray<int>(total, Allocator.Temp);
            var parents = new NativeArray<int>(total, Allocator.Temp);
            var inClosed = new NativeArray<bool>(total, Allocator.Temp);
            var openHeap = new NativeList<HeapEntry>(256, Allocator.Temp);

            for (int i = 0; i < total; i++) { gCosts[i] = int.MaxValue; parents[i] = -1; }
            gCosts[startIdx] = 0;

            int2 startLocalSnapped = IndexToLocal(startIdx, cellCount);
            int2 endLocalSnapped = IndexToLocal(endIdx, cellCount);

            int startH = Heuristic(startLocalSnapped, endLocalSnapped);
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

                        int terrainCost = blob.Nodes[nIdx].TerrainCostMask == 0 ? 10 : 20;
                        int moveCost = (dx != 0 && dz != 0) ? 14 : 10;
                        int tentativeG = gCosts[curIdx] + moveCost + terrainCost - 10;

                        if (tentativeG < gCosts[nIdx])
                        {
                            gCosts[nIdx] = tentativeG;
                            parents[nIdx] = curIdx;
                            int h = Heuristic(nLocal, endLocalSnapped);
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
            {
                // Compute final destination here where blob is in scope.
                // If EndWorld falls in a walkable cell use it exactly;
                // otherwise use the snapped endIdx cell centre so the unit
                // stops at the wall edge instead of walking through it.
                ref ChunkStaticBlob blobRef = ref Blob.Value;
                int2 endWorldCell = new int2(
                    (int)math.floor((EndWorld.x - ChunkCoord.x * (float)(Config.ChunkCellCount * Config.CellSize)) / Config.CellSize),
                    (int)math.floor((EndWorld.z - ChunkCoord.y * (float)(Config.ChunkCellCount * Config.CellSize)) / Config.CellSize));
                endWorldCell = math.clamp(endWorldCell, int2.zero, new int2(cellCount - 1, cellCount - 1));
                int endWorldIdx = ChunkManagerSystem.CellLocalToIndex(endWorldCell, cellCount);
                bool endWorldWalkable = IsWalkable(ref blobRef, endWorldIdx, Permissions);
                float3 finalDest = endWorldWalkable ? EndWorld : CellLocalToWorld(IndexToLocal(endIdx, cellCount));
                ReconstructPath(startIdx, endIdx, parents, cellCount, finalDest, ref PathOut);
            }

            gCosts.Dispose();
            parents.Dispose();
            inClosed.Dispose();
            openHeap.Dispose();
        }

        /// <summary>
        /// If the given cell is unwalkable, BFS outward to find the nearest walkable one.
        /// Returns -1 if none found within a reasonable radius.
        /// This prevents A* from silently failing when agents are nudged into wall cells
        /// by formation offsets or physics.
        /// </summary>
        private int FindNearestWalkable(ref ChunkStaticBlob blob, int idx, int2 local,
                                         int cellCount, UnitLayerPermissions perms)
        {
            if (IsWalkable(ref blob, idx, perms)) return idx;

            // BFS up to radius 4
            var queue = new NativeList<int2>(32, Allocator.Temp);
            var visited = new NativeArray<bool>(cellCount * cellCount, Allocator.Temp);
            queue.Add(local);
            visited[idx] = true;
            int head = 0;
            int result = -1;

            while (head < queue.Length)
            {
                int2 cur = queue[head++];
                int2 d = math.abs(cur - local);
                if (math.max(d.x, d.y) > 4) break; // radius limit

                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        int2 n = cur + new int2(dx, dz);
                        if (n.x < 0 || n.x >= cellCount || n.y < 0 || n.y >= cellCount) continue;
                        int nIdx = ChunkManagerSystem.CellLocalToIndex(n, cellCount);
                        if (visited[nIdx]) continue;
                        visited[nIdx] = true;
                        if (IsWalkable(ref blob, nIdx, perms)) { result = nIdx; goto Done; }
                        queue.Add(n);
                    }
                }
            }
        Done:
            queue.Dispose();
            visited.Dispose();
            return result;
        }

        private bool IsWalkable(ref ChunkStaticBlob blob, int idx, UnitLayerPermissions perms)
        {
            // FIX: WalkableLayerMask == 0 means blocked (wall or no ground).
            // Any non-zero value masked against unit permissions determines access.
            // Previously slope-blocked nodes (WalkableLayerMask = 0b00000010) were
            // correctly handled here but only if perms.WalkableLayers included bit 1.
            // Ground units have WalkableLayers = 0xFF so slopes still block them
            // via SlopeFlags check below.
            byte mask = blob.Nodes[idx].WalkableLayerMask;
            if (mask == 0) return false;
            // Slope-blocked nodes have SlopeFlags = 1 — ground units cannot enter them
            if (blob.Nodes[idx].SlopeFlags == 1 && perms.IsFlying == 0) return false;
            return (mask & perms.WalkableLayers) != 0;
        }

        private int Heuristic(int2 a, int2 b)
        {
            int2 d = math.abs(a - b);
            int straight = math.max(d.x, d.y);
            int diag = math.min(d.x, d.y);
            return 10 * straight + 4 * diag;
        }

        private int2 IndexToLocal(int idx, int cellCount)
            => new int2(idx % cellCount, idx / cellCount);

        private void ReconstructPath(int startIdx, int endIdx, NativeArray<int> parents,
                                      int cellCount, float3 finalDestination,
                                      ref NativeList<float3> pathOut)
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

            // FIX: Removed collinear-node simplification. That optimisation deleted
            // waypoints at wall corners, causing the unit to walk the straight line
            // from the previous kept waypoint to the next, which cuts through walls.
            // All cell-centre waypoints are now kept — the path is already optimal.
            for (int i = raw.Length - 1; i >= 0; i--)
            {
                int2 cellLocal = IndexToLocal(raw[i], cellCount);
                pathOut.Add(CellLocalToWorld(cellLocal));
            }

            // finalDestination is pre-validated in Execute() to be walkable.
            pathOut.Add(finalDestination);
            raw.Dispose();
        }

        private float3 CellLocalToWorld(int2 localCell)
        {
            float chunkWorldSize = Config.ChunkCellCount * Config.CellSize;
            float3 chunkOrigin = new float3(ChunkCoord.x * chunkWorldSize, 0, ChunkCoord.y * chunkWorldSize);
            return chunkOrigin + new float3((localCell.x + 0.5f) * Config.CellSize, 0f, (localCell.y + 0.5f) * Config.CellSize);
        }

        private struct HeapEntry { public int Index; public int FCost; public int HCost; }

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