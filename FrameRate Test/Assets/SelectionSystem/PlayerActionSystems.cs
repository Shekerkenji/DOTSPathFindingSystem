using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using Unity.Collections;

namespace PlayerActions
{
    // ── System group ──────────────────────────────────────────────────────────

    [WorldSystemFilter(WorldSystemFilterFlags.Default)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(UnitSelectionSystem))]
    [UpdateBefore(typeof(GroupMoveOrderSystem))]
    public partial class PlayerActionSystemGroup : ComponentSystemGroup { }

    // ─────────────────────────────────────────────────────────────────────────
    //  MoveCommandSystem
    //
    //  Long press on empty ground → move all selected units.
    //
    //  FORMATION (solo units only)
    //  ───────────────────────────
    //  Units are assigned unique GRID CELLS centred on the tapped destination.
    //  The grid faces the destination (rotated toward it from the group centre).
    //  Each unit paths to its own cell — no overlap at arrival.
    //
    //  KEY FIX: slot destinations are snapped to NavGrid cell centres so that
    //  each slot maps to a distinct int2 coordinate. Duplicate cells are
    //  resolved by a BFS outward spiral until a free walkable cell is found.
    //
    //    [0][1][2]      ← front row (closest to destination)
    //    [3][4][5]      ← second row
    //    [6][ ][ ]      ← partial row
    //         ↑ destination
    //
    //  Group / BigGroup entities are sent to the exact destination unchanged
    //  (they handle their own internal formation via GroupMoveOrderSystem).
    // ─────────────────────────────────────────────────────────────────────────

    [WorldSystemFilter(WorldSystemFilterFlags.Default)]
    [UpdateInGroup(typeof(PlayerActionSystemGroup))]
    public partial struct MoveCommandSystem : ISystem
    {
        /// <summary>
        /// Number of grid cells between adjacent formation slots.
        /// 1 = touching, 2 = one-cell gap. Keep at 1 for tight formations.
        /// </summary>
        private const int SlotStride = 1;

        public void OnCreate(ref SystemState state)
            => state.RequireForUpdate<NavGridConfig>();

        public void OnDestroy(ref SystemState state) { }

        public void OnUpdate(ref SystemState state)
        {
            var input = SystemAPI.ManagedAPI.GetSingleton<PlayerInputSingleton>();
            if (!input.LongPressThisFrame) return;
            if (input.ClickConsumedBySelection) return;

#if UNITY_EDITOR
            if (!input.HitGround)
            {
                Debug.LogWarning("[MoveCommand] HitGround=false — is ground at y=0?");
                return;
            }
#else
            if (!input.HitGround) return;
#endif

            var sel = SystemAPI.ManagedAPI.GetSingleton<SelectionSingleton>();

#if UNITY_EDITOR
            if (!sel.HasSelection) { Debug.LogWarning("[MoveCommand] No selection."); return; }
#else
            if (!sel.HasSelection) return;
#endif

            // ── Get the nav grid ──────────────────────────────────────────────
            NavGridSingleton grid = null;
            foreach (var g in SystemAPI.Query<NavGridSingleton>()) { grid = g; break; }
            if (grid == null || !grid.IsCreated) return;

            float3 destination = input.GroundHitPoint;
            int total = sel.SelectedEntities.Length;

#if UNITY_EDITOR
            Debug.Log($"[MoveCommand] → {destination} for {total} unit(s)");
#endif

            // ── Count solo units to size the formation ────────────────────────
            int soloCount = 0;
            for (int i = 0; i < total; i++)
            {
                Entity e = sel.SelectedEntities[i];
                if (!SystemAPI.Exists(e)) continue;
                if (!SystemAPI.IsComponentEnabled<Selected>(e)) continue;
                if (!IsGroupEntity(ref state, e)) soloCount++;
            }

            // ── Compute formation orientation ─────────────────────────────────
            // Forward = direction from group average position to destination (XZ).
            float3 forward = new float3(0, 0, 1);

            if (soloCount >= 1)
            {
                float3 sum = float3.zero;
                int n = 0;
                for (int i = 0; i < total; i++)
                {
                    Entity e = sel.SelectedEntities[i];
                    if (!SystemAPI.Exists(e) || IsGroupEntity(ref state, e)) continue;
                    if (!SystemAPI.HasComponent<LocalTransform>(e)) continue;
                    sum += SystemAPI.GetComponent<LocalTransform>(e).Position;
                    n++;
                }
                if (n > 0)
                {
                    float3 dir = destination - (sum / n);
                    dir.y = 0f;
                    float len = math.length(dir);
                    if (len > 0.01f) forward = dir / len;
                }
            }

            float3 right = new float3(forward.z, 0f, -forward.x);
            int cols = (int)math.ceil(math.sqrt(math.max(1, soloCount)));

            // ── Assign unique grid-cell slots ─────────────────────────────────
            // We snap each slot to a cell centre then deduplicate via BFS.
            // usedCells tracks which int2 coords are already claimed this dispatch.
            var usedCells = new NativeHashSet<int2>(soloCount * 2, Allocator.Temp);

            // Snap destination itself to a walkable cell centre
            int2 destCell = grid.WorldToGrid(destination);
            destCell = NearestWalkableCell(grid, destCell);

            int slotIdx = 0;

            // ── Dispatch ──────────────────────────────────────────────────────
            for (int i = 0; i < total; i++)
            {
                Entity e = sel.SelectedEntities[i];
                if (!SystemAPI.Exists(e)) continue;
                if (!SystemAPI.IsComponentEnabled<Selected>(e)) continue;

                if (IsGroupEntity(ref state, e))
                {
                    DispatchGroupOrder(ref state, e, destination);
                }
                else
                {
                    // 1. Compute the ideal offset position for this slot
                    int col = slotIdx % cols;
                    int row = slotIdx / cols;
                    float colOff = (col - (cols - 1) * 0.5f) * SlotStride;
                    float rowOff = row * SlotStride;
                    float3 idealWorld = grid.GridToWorld(destCell)
                                      + right * (colOff * grid.CellSize)
                                      - forward * (rowOff * grid.CellSize);
                    slotIdx++;

                    // 2. Snap to the nearest unique walkable cell
                    int2 idealCell = grid.WorldToGrid(idealWorld);
                    int2 cell = UniqueWalkableCell(grid, idealCell, ref usedCells);
                    float3 slotWorld = grid.GridToWorld(cell);

                    DispatchSoloUnit(ref state, e, slotWorld);
                }
            }

            usedCells.Dispose();
        }

        // ── Slot helpers ──────────────────────────────────────────────────────

        /// <summary>
        /// Starting from <paramref name="ideal"/>, spiral outward until a walkable
        /// cell is found that is not already in <paramref name="used"/>.
        /// Adds the chosen cell to <paramref name="used"/> before returning.
        /// </summary>
        private static int2 UniqueWalkableCell(
            NavGridSingleton grid,
            int2 ideal,
            ref NativeHashSet<int2> used)
        {
            // BFS-style outward spiral
            for (int radius = 0; radius <= 16; radius++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                    for (int dz = -radius; dz <= radius; dz++)
                    {
                        // Only visit the ring boundary at this radius
                        if (math.abs(dx) != radius && math.abs(dz) != radius) continue;

                        int2 candidate = ideal + new int2(dx, dz);
                        if (!grid.IsWalkable(candidate)) continue;
                        if (used.Contains(candidate)) continue;

                        used.Add(candidate);
                        return candidate;
                    }
            }

            // Fallback: return ideal even if occupied/duplicate (very crowded map)
            used.Add(ideal);
            return ideal;
        }

        /// <summary>
        /// Snap a coord to the nearest walkable cell (ignores uniqueness).
        /// Used for the destination cell itself.
        /// </summary>
        private static int2 NearestWalkableCell(NavGridSingleton grid, int2 coord)
        {
            if (grid.IsWalkable(coord)) return coord;
            for (int r = 1; r <= 8; r++)
                for (int dx = -r; dx <= r; dx++)
                    for (int dz = -r; dz <= r; dz++)
                    {
                        if (math.abs(dx) != r && math.abs(dz) != r) continue;
                        var c = coord + new int2(dx, dz);
                        if (grid.IsWalkable(c)) return c;
                    }
            return coord;
        }

        // ── Routing helpers ───────────────────────────────────────────────────

        private bool IsGroupEntity(ref SystemState state, Entity e)
            => SystemAPI.HasComponent<BigGroupMoveOrder>(e) ||
               SystemAPI.HasComponent<GroupMoveOrder>(e);

        private void DispatchGroupOrder(ref SystemState state, Entity e, float3 dest)
        {
            if (SystemAPI.HasComponent<BigGroupMoveOrder>(e))
            {
                var o = SystemAPI.GetComponent<BigGroupMoveOrder>(e);
                o.Destination = dest;
                SystemAPI.SetComponent(e, o);
                SystemAPI.SetComponentEnabled<BigGroupMoveOrder>(e, true);
                return;
            }
            if (SystemAPI.HasComponent<GroupMoveOrder>(e))
            {
                var o = SystemAPI.GetComponent<GroupMoveOrder>(e);
                o.Destination = dest;
                SystemAPI.SetComponent(e, o);
                SystemAPI.SetComponentEnabled<GroupMoveOrder>(e, true);
            }
        }

        private void DispatchSoloUnit(ref SystemState state, Entity e, float3 dest)
        {
            if (!SystemAPI.HasComponent<PathRequest>(e) ||
                !SystemAPI.HasComponent<LocalTransform>(e))
            {
#if UNITY_EDITOR
                Debug.LogWarning($"[MoveCommand] {e} missing PathRequest or LocalTransform.");
#endif
                return;
            }

            float3 start = SystemAPI.GetComponent<LocalTransform>(e).Position;

            var pr = SystemAPI.GetComponent<PathRequest>(e);
            pr.Start = start;
            pr.End = dest;
            pr.RequestId = Time.frameCount;
            SystemAPI.SetComponent(e, pr);
            SystemAPI.SetComponentEnabled<PathRequest>(e, true);

            if (SystemAPI.HasComponent<NavAgent>(e))
            {
                var agent = SystemAPI.GetComponent<NavAgent>(e);
                agent.Destination = dest;
                agent.Status = NavAgentStatus.Requesting;
                agent.CurrentPathIndex = 0;
                agent.FormationOffset = float3.zero; // offset baked into dest cell
                SystemAPI.SetComponent(e, agent);
            }

#if UNITY_EDITOR
            Debug.Log($"[MoveCommand] Solo {e} → cell slot {dest}");
#endif
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  HOW TO ADD A NEW ACTION
    //  ───────────────────────
    //  [WorldSystemFilter(WorldSystemFilterFlags.Default)]
    //  [UpdateInGroup(typeof(PlayerActionSystemGroup))]
    //  public partial struct AttackCommandSystem : ISystem { ... }
    // ─────────────────────────────────────────────────────────────────────────
}