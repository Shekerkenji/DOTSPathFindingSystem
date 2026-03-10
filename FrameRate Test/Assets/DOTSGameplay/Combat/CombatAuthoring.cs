using Unity.Entities;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
//  CombatAuthoring.cs
//
//  Extend UnitAuthoring's baker — add combat components to every unit.
//  Attach this alongside UnitAuthoring on the same GameObject.
//
//  Bakes:
//    AttackOrder   (disabled — enabled by CombatAPI to issue attack commands)
//    CombatState   (Idle, no target)
//    RangedStats   (ranged units only — requires Ranged component also baked)
//    Dead          (disabled)
// ─────────────────────────────────────────────────────────────────────────────

[DisallowMultipleComponent]
public class CombatAuthoring : MonoBehaviour
{
    [Header("Ranged Only (ignored for melee)")]
    [Tooltip("Grid cells per second the projectile travels. 0 = melee unit.")]
    public float projectileSpeed = 0f;

    public class Baker : Baker<CombatAuthoring>
    {
        public override void Bake(CombatAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);

            // ── Always baked ──────────────────────────────────────────────────
            AddComponent<AttackOrder>(entity);
            SetComponentEnabled<AttackOrder>(entity, false);

            AddComponent(entity, new CombatState
            {
                Phase           = CombatPhase.Idle,
                Target          = Entity.Null,
                ClaimedCell     = default,
                PhaseTimer      = 0f,
                WindupDuration  = 0f,
                RecoveryDuration = 0f,
            });

            AddComponent<Dead>(entity);
            SetComponentEnabled<Dead>(entity, false);

            // ── Ranged only ───────────────────────────────────────────────────
            if (authoring.projectileSpeed > 0f)
            {
                AddComponent(entity, new RangedStats
                {
                    ProjectileSpeed = authoring.projectileSpeed,
                });
            }
        }
    }
}
