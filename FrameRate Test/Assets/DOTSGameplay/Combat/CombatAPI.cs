using Unity.Entities;

// ─────────────────────────────────────────────────────────────────────────────
//  CombatAPI.cs  —  static helper for issuing combat commands
// ─────────────────────────────────────────────────────────────────────────────

public static class CombatAPI
{
    /// <summary>
    /// Issue an attack order from a MonoBehaviour or any non-system context.
    /// </summary>
    public static void IssueAttackOrder(EntityManager em, Entity attacker, Entity target)
    {
        if (!em.Exists(attacker) || !em.Exists(target)) return;
        if (!em.HasComponent<AttackOrder>(attacker)) return;

        var order = em.GetComponentData<AttackOrder>(attacker);
        order.Target = target;
        em.SetComponentData(attacker, order);
        em.SetComponentEnabled<AttackOrder>(attacker, true);
    }

    /// <summary>
    /// Issue an attack order from inside an ISystem.
    /// Uses EntityManager from SystemState — no SystemAPI.
    /// </summary>
    public static void IssueAttackOrder(ref SystemState state, Entity attacker, Entity target)
    {
        var em = state.EntityManager;
        IssueAttackOrder(em, attacker, target);
    }

    /// <summary>
    /// Cancel combat and return the unit to Idle immediately.
    /// </summary>
    public static void CancelCombat(EntityManager em, Entity attacker)
    {
        if (!em.Exists(attacker)) return;
        if (!em.HasComponent<CombatState>(attacker)) return;

        var cs = em.GetComponentData<CombatState>(attacker);
        if (cs.Phase == CombatPhase.Idle) return;

        // Release claimed cell
        using var query = em.CreateEntityQuery(
            ComponentType.ReadOnly<CombatOccupancySingleton>());
        if (!query.IsEmpty)
        {
            var occ = query.GetSingleton<CombatOccupancySingleton>();
            occ.Release(cs.ClaimedCell, attacker);
        }

        cs.Phase = CombatPhase.Idle;
        cs.Target = Entity.Null;
        cs.ClaimedCell = default;
        cs.PhaseTimer = 0f;
        em.SetComponentData(attacker, cs);

        if (em.HasComponent<AttackOrder>(attacker))
            em.SetComponentEnabled<AttackOrder>(attacker, false);
    }

    /// <summary>Returns true if the unit is in any combat phase other than Idle.</summary>
    public static bool IsInCombat(EntityManager em, Entity attacker)
    {
        if (!em.Exists(attacker) || !em.HasComponent<CombatState>(attacker))
            return false;
        return em.GetComponentData<CombatState>(attacker).Phase != CombatPhase.Idle;
    }

    /// <summary>Returns true if the unit is locked (Windup or Recovering).</summary>
    public static bool IsLocked(EntityManager em, Entity attacker)
    {
        if (!em.Exists(attacker) || !em.HasComponent<CombatState>(attacker))
            return false;
        var phase = em.GetComponentData<CombatState>(attacker).Phase;
        return phase == CombatPhase.Windup || phase == CombatPhase.Recovering;
    }
}