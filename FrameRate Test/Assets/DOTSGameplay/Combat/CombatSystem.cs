using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
//  CombatSystem.cs
// ─────────────────────────────────────────────────────────────────────────────

[WorldSystemFilter(WorldSystemFilterFlags.Default)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(NavAgentMoveSystem))]
public partial struct CombatSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<NavGridConfig>();

        if (!SystemAPI.ManagedAPI.HasSingleton<CombatOccupancySingleton>())
        {
            var e = state.EntityManager.CreateEntity();
            state.EntityManager.SetName(e, "CombatOccupancySingleton");
            var occ = new CombatOccupancySingleton();
            occ.Init();
            state.EntityManager.AddComponentObject(e, occ);
        }

        var pq = state.EntityManager.CreateEntityQuery(
            ComponentType.ReadOnly<ProjectileSingleton>());
        if (pq.IsEmpty)
        {
            var pe = state.EntityManager.CreateEntity();
            state.EntityManager.SetName(pe, "ProjectileSingleton");
            state.EntityManager.AddComponent<ProjectileSingleton>(pe);
            state.EntityManager.AddBuffer<ProjectileFlight>(pe);
        }
        pq.Dispose();
    }

    public void OnDestroy(ref SystemState state)
    {
        if (SystemAPI.ManagedAPI.HasSingleton<CombatOccupancySingleton>())
            SystemAPI.ManagedAPI.GetSingleton<CombatOccupancySingleton>().Dispose();
    }

    public void OnUpdate(ref SystemState state)
    {
        NavGridSingleton grid = null;
        foreach (var g in SystemAPI.Query<NavGridSingleton>()) { grid = g; break; }
        if (grid == null || !grid.IsCreated) return;

        var occ = SystemAPI.ManagedAPI.GetSingleton<CombatOccupancySingleton>();
        float dt = SystemAPI.Time.DeltaTime;
        var em = state.EntityManager;

        Entity projEntity = Entity.Null;
        foreach (var (_, e) in SystemAPI.Query<RefRO<ProjectileSingleton>>().WithEntityAccess())
        { projEntity = e; break; }

        // ── Consume AttackOrders ──────────────────────────────────────────────
        foreach (var (order, cs, unit, ai, entity) in
            SystemAPI.Query<
                RefRO<AttackOrder>,
                RefRW<CombatState>,
                RefRO<Unit>,
                RefRO<UnitAI>>()
                     .WithAll<AttackOrder>()
                     .WithEntityAccess())
        {
            Entity target = order.ValueRO.Target;
            if (!em.Exists(target)) continue;
            if (!em.HasComponent<HealthComponent>(target)) continue;
            if (em.IsComponentEnabled<Dead>(target)) continue;

            if (!cs.ValueRO.ClaimedCell.Equals(default(int2)))
                occ.Release(cs.ValueRO.ClaimedCell, entity);

            bool isMelee = em.HasComponent<Meele>(entity);
            int range = ai.ValueRO.AttackRange;
            float3 targetPos = em.GetComponentData<LocalTransform>(target).Position;
            byte2 targetSize = em.GetComponentData<Unit>(target).Size;
            int2 targetCell = grid.WorldToGrid(targetPos);

            int2 attackCell = FindAttackCell(grid, occ, targetCell, targetSize,
                                             entity, range, isMelee);

            cs.ValueRW.Phase = CombatPhase.Seeking;
            cs.ValueRW.Target = target;
            cs.ValueRW.ClaimedCell = attackCell;
            cs.ValueRW.PhaseTimer = 0f;
            cs.ValueRW.WindupDuration = 1f / math.max(0.01f, unit.ValueRO.AttackSpeed);
            cs.ValueRW.RecoveryDuration = cs.ValueRW.WindupDuration * CombatState.RecoveryRatio;

            occ.TryClaim(attackCell, entity);
            RequestPathToCell(em, entity, grid.GridToWorld(attackCell));

            SystemAPI.SetComponentEnabled<AttackOrder>(entity, false);
        }

        // ── State machine ─────────────────────────────────────────────────────
        foreach (var (cs, unit, ai, transform, entity) in
            SystemAPI.Query<
                RefRW<CombatState>,
                RefRO<Unit>,
                RefRO<UnitAI>,
                RefRO<LocalTransform>>()
                     .WithEntityAccess())
        {
            if (cs.ValueRO.Phase == CombatPhase.Idle) continue;

            Entity target = cs.ValueRO.Target;

            if (!em.Exists(target) || em.IsComponentEnabled<Dead>(target))
            {
                ExitCombat(em, entity, ref cs.ValueRW, occ);
                continue;
            }

            float3 myPos = transform.ValueRO.Position;
            float3 targetPos = em.GetComponentData<LocalTransform>(target).Position;
            int2 myCell = grid.WorldToGrid(myPos);
            int2 targetCell = grid.WorldToGrid(targetPos);
            float distCells = math.length((float2)(myCell - targetCell));

            // ── Seeking ───────────────────────────────────────────────────────
            if (cs.ValueRO.Phase == CombatPhase.Seeking)
            {
                byte2 targetSize = em.GetComponentData<Unit>(target).Size;
                bool isMelee = em.HasComponent<Meele>(entity);

                if (!IsCellValidForTarget(cs.ValueRO.ClaimedCell, targetCell,
                                          targetSize, ai.ValueRO.AttackRange))
                {
                    occ.Release(cs.ValueRO.ClaimedCell, entity);
                    int2 newCell = FindAttackCell(grid, occ, targetCell, targetSize,
                                                  entity, ai.ValueRO.AttackRange, isMelee);
                    cs.ValueRW.ClaimedCell = newCell;
                    occ.TryClaim(newCell, entity);
                    RequestPathToCell(em, entity, grid.GridToWorld(newCell));
                }

                bool arrived = myCell.Equals(cs.ValueRO.ClaimedCell) ||
                               math.lengthsq(myPos - grid.GridToWorld(cs.ValueRO.ClaimedCell)) < 0.5f;
                bool inRange = distCells <= ai.ValueRO.AttackRange + 0.5f;

                if (arrived || (!isMelee && inRange))
                {
                    LockMovement(em, entity);
                    cs.ValueRW.Phase = CombatPhase.Windup;
                    cs.ValueRW.PhaseTimer = 0f;
                }
            }

            // ── Windup ────────────────────────────────────────────────────────
            else if (cs.ValueRO.Phase == CombatPhase.Windup)
            {
                LockMovement(em, entity);
                cs.ValueRW.PhaseTimer += dt;

                if (cs.ValueRW.PhaseTimer >= cs.ValueRO.WindupDuration)
                {
                    bool isMelee = em.HasComponent<Meele>(entity);
                    int damage = unit.ValueRO.AttackDamage;

                    if (isMelee)
                    {
                        ApplyDamage(em, target, damage);
                    }
                    else if (projEntity != Entity.Null &&
                             em.HasComponent<RangedStats>(entity))
                    {
                        var rs = em.GetComponentData<RangedStats>(entity);
                        float travelTime = ai.ValueRO.AttackRange /
                                           math.max(0.01f, rs.ProjectileSpeed);
                        var buffer = SystemAPI.GetBuffer<ProjectileFlight>(projEntity);
                        buffer.Add(new ProjectileFlight
                        {
                            Attacker = entity,
                            Target = target,
                            TargetCellAtFire = targetCell,
                            Damage = damage,
                            TravelTime = travelTime,
                            Elapsed = 0f,
                        });
                    }

                    cs.ValueRW.Phase = CombatPhase.Recovering;
                    cs.ValueRW.PhaseTimer = 0f;
                }
            }

            // ── Recovering ────────────────────────────────────────────────────
            else if (cs.ValueRO.Phase == CombatPhase.Recovering)
            {
                LockMovement(em, entity);
                cs.ValueRW.PhaseTimer += dt;

                if (cs.ValueRW.PhaseTimer >= cs.ValueRO.RecoveryDuration)
                {
                    bool isMelee = em.HasComponent<Meele>(entity);
                    bool inRange = distCells <= ai.ValueRO.AttackRange + 0.5f;

                    if (inRange)
                    {
                        cs.ValueRW.Phase = CombatPhase.Windup;
                        cs.ValueRW.PhaseTimer = 0f;
                    }
                    else
                    {
                        byte2 targetSize = em.GetComponentData<Unit>(target).Size;
                        occ.Release(cs.ValueRO.ClaimedCell, entity);
                        int2 newCell = FindAttackCell(grid, occ, targetCell, targetSize,
                                                      entity, ai.ValueRO.AttackRange, isMelee);
                        cs.ValueRW.ClaimedCell = newCell;
                        occ.TryClaim(newCell, entity);
                        RequestPathToCell(em, entity, grid.GridToWorld(newCell));
                        cs.ValueRW.Phase = CombatPhase.Seeking;
                        cs.ValueRW.PhaseTimer = 0f;
                    }
                }
            }
        }
    }

    // ── Helpers (pass EntityManager — no SystemAPI in static methods) ─────────

    private static int2 FindAttackCell(
        NavGridSingleton grid, CombatOccupancySingleton occ,
        int2 targetCell, byte2 targetSize,
        Entity attacker, int range, bool isMelee)
    {
        int maxSearch = range + (int)math.max((int)targetSize.x, (int)targetSize.y) + 4;

        for (int r = 1; r <= maxSearch; r++)
            for (int dx = -r; dx <= r; dx++)
                for (int dz = -r; dz <= r; dz++)
                {
                    if (math.abs(dx) != r && math.abs(dz) != r) continue;

                    int2 candidate = targetCell + new int2(dx, dz);
                    int2 closest = ClampToFootprint(candidate, targetCell, targetSize);
                    int dist = math.max(math.abs(candidate.x - closest.x),
                                              math.abs(candidate.y - closest.y));

                    if (dist > range) continue;
                    if (isMelee && dist > 1) continue;
                    if (!grid.IsWalkable(candidate)) continue;
                    if (occ.ClaimedCells.TryGetValue(candidate, out var owner) &&
                        owner != attacker) continue;

                    return candidate;
                }

        return targetCell + new int2(1, 0); // fallback
    }

    private static bool IsCellValidForTarget(
        int2 claimedCell, int2 targetCell, byte2 targetSize, int range)
    {
        int2 closest = ClampToFootprint(claimedCell, targetCell, targetSize);
        int dist = math.max(math.abs(claimedCell.x - closest.x),
                            math.abs(claimedCell.y - closest.y));
        return dist <= range + 2;
    }

    private static int2 ClampToFootprint(int2 coord, int2 targetCell, byte2 targetSize)
        => new int2(
            math.clamp(coord.x, targetCell.x, targetCell.x + targetSize.x - 1),
            math.clamp(coord.y, targetCell.y, targetCell.y + targetSize.y - 1));

    private static void LockMovement(EntityManager em, Entity e)
    {
        if (!em.HasComponent<NavAgent>(e)) return;
        var nav = em.GetComponentData<NavAgent>(e);
        if (nav.Status == NavAgentStatus.Moving ||
            nav.Status == NavAgentStatus.Requesting)
        {
            nav.Status = NavAgentStatus.Idle;
            em.SetComponentData(e, nav);
        }
        if (em.HasComponent<PathRequest>(e))
            em.SetComponentEnabled<PathRequest>(e, false);
    }

    private static void ExitCombat(
        EntityManager em, Entity entity,
        ref CombatState cs, CombatOccupancySingleton occ)
    {
        occ.Release(cs.ClaimedCell, entity);
        cs.Phase = CombatPhase.Idle;
        cs.Target = Entity.Null;
        cs.ClaimedCell = default;
        cs.PhaseTimer = 0f;
    }

    private static void ApplyDamage(EntityManager em, Entity target, int damage)
    {
        if (!em.HasComponent<HealthComponent>(target)) return;
        var hp = em.GetComponentData<HealthComponent>(target);
        hp.CurrentHealth = math.max(0, hp.CurrentHealth - damage);
        em.SetComponentData(target, hp);

        if (hp.CurrentHealth <= 0 && em.HasComponent<Dead>(target))
            em.SetComponentEnabled<Dead>(target, true);
    }

    private static void RequestPathToCell(EntityManager em, Entity entity, float3 dest)
    {
        if (!em.HasComponent<PathRequest>(entity)) return;
        if (!em.HasComponent<LocalTransform>(entity)) return;

        float3 start = em.GetComponentData<LocalTransform>(entity).Position;
        var pr = em.GetComponentData<PathRequest>(entity);
        pr.Start = start;
        pr.End = dest;
        pr.RequestId = Time.frameCount;
        em.SetComponentData(entity, pr);
        em.SetComponentEnabled<PathRequest>(entity, true);

        if (em.HasComponent<NavAgent>(entity))
        {
            var nav = em.GetComponentData<NavAgent>(entity);
            nav.Destination = dest;
            nav.Status = NavAgentStatus.Requesting;
            nav.CurrentPathIndex = 0;
            em.SetComponentData(entity, nav);
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  ProjectileSystem
// ─────────────────────────────────────────────────────────────────────────────

[WorldSystemFilter(WorldSystemFilterFlags.Default)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(CombatSystem))]
public partial struct ProjectileSystem : ISystem
{
    public void OnCreate(ref SystemState state) { }
    public void OnDestroy(ref SystemState state) { }

    public void OnUpdate(ref SystemState state)
    {
        Entity projEntity = Entity.Null;
        foreach (var (_, e) in SystemAPI.Query<RefRO<ProjectileSingleton>>().WithEntityAccess())
        { projEntity = e; break; }
        if (projEntity == Entity.Null) return;

        NavGridSingleton grid = null;
        foreach (var g in SystemAPI.Query<NavGridSingleton>()) { grid = g; break; }
        if (grid == null) return;

        var em = state.EntityManager;
        float dt = SystemAPI.Time.DeltaTime;
        var buffer = SystemAPI.GetBuffer<ProjectileFlight>(projEntity);

        for (int i = buffer.Length - 1; i >= 0; i--)
        {
            var p = buffer[i];
            p.Elapsed += dt;

            if (p.Elapsed >= p.TravelTime)
            {
                buffer.RemoveAt(i);

                if (!em.Exists(p.Target)) continue;
                if (em.IsComponentEnabled<Dead>(p.Target)) continue;
                if (!em.HasComponent<LocalTransform>(p.Target)) continue;

                float3 targetPos = em.GetComponentData<LocalTransform>(p.Target).Position;
                int2 currentCell = grid.WorldToGrid(targetPos);

                if (currentCell.Equals(p.TargetCellAtFire))
                {
                    if (em.HasComponent<HealthComponent>(p.Target))
                    {
                        var hp = em.GetComponentData<HealthComponent>(p.Target);
                        hp.CurrentHealth = math.max(0, hp.CurrentHealth - p.Damage);
                        em.SetComponentData(p.Target, hp);

                        if (hp.CurrentHealth <= 0 && em.HasComponent<Dead>(p.Target))
                            em.SetComponentEnabled<Dead>(p.Target, true);
                    }
                }
            }
            else
            {
                buffer[i] = p;
            }
        }
    }
}