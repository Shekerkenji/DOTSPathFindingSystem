using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

// ─────────────────────────────────────────────────────────────────────────────
//  CombatComponents.cs
//
//  All data types for the combat framework.
//  Rules:
//    • No structural add/remove — IEnableableComponent toggles only.
//    • No physics — everything is grid-based (NavGridSingleton int2 coords).
//    • No AI — this is a pure framework; AttackOrder is issued via CombatAPI.
// ─────────────────────────────────────────────────────────────────────────────

// ─────────────────────────────────────────────────────────────────────────────
//  Attack Order  (enableable — issued by CombatAPI, consumed by CombatSystem)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Baked on every combat unit. Enable + set Target to issue an attack command.
/// CombatSystem disables it once the unit enters its combat state machine.
/// </summary>
public struct AttackOrder : IComponentData, IEnableableComponent
{
    /// <summary>Entity to attack. Must be of an opposing faction.</summary>
    public Entity Target;
}

// ─────────────────────────────────────────────────────────────────────────────
//  Combat State  (always present, tracks the unit's combat phase)
// ─────────────────────────────────────────────────────────────────────────────

public enum CombatPhase : byte
{
    Idle       = 0,  // not in combat
    Seeking    = 1,  // moving to attack position
    Windup     = 2,  // attack animation started, can't move
    Recovering = 3,  // post-attack recovery, can't move
}

/// <summary>
/// Tracks a unit's current combat phase and timing.
/// Always present on combat units (baked in). Never added/removed at runtime.
/// </summary>
public struct CombatState : IComponentData
{
    public CombatPhase Phase;

    /// <summary>Entity currently being attacked. Entity.Null when Idle.</summary>
    public Entity Target;

    /// <summary>
    /// Grid cell this unit has claimed as its attack position.
    /// Registered in CombatOccupancySingleton while Seeking/Windup/Recovering.
    /// int2.zero when unclaimed.
    /// </summary>
    public int2 ClaimedCell;

    /// <summary>Elapsed seconds in the current phase.</summary>
    public float PhaseTimer;

    /// <summary>
    /// Windup duration in seconds (= 1f / Unit.AttackSpeed).
    /// Cached when entering Windup to avoid recomputing each frame.
    /// </summary>
    public float WindupDuration;

    /// <summary>
    /// Recovery duration in seconds (= WindupDuration * RecoveryRatio).
    /// </summary>
    public float RecoveryDuration;

    /// <summary>Ratio of recovery time to windup time. Tune per unit type.</summary>
    public const float RecoveryRatio = 0.5f;
}

// ─────────────────────────────────────────────────────────────────────────────
//  Ranged-only components
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Additional data for ranged units. Baked alongside Ranged tag.
/// </summary>
public struct RangedStats : IComponentData
{
    /// <summary>Grid cells per second the projectile travels.</summary>
    public float ProjectileSpeed;
}

/// <summary>
/// One in-flight projectile. Stored in a DynamicBuffer on the
/// ProjectileSingleton entity — no GameObjects, no physics.
///
/// On fire:  travelTime  = AttackRange / ProjectileSpeed
///           targetCell  = grid coord of target at fire time
///           targetEntity= entity being shot (for validation)
///
/// Each frame: elapsed += deltaTime
/// On arrival (elapsed >= travelTime):
///   if target still exists AND still occupies targetCell → apply damage
///   else → miss (target moved)
/// </summary>
public struct ProjectileFlight : IBufferElementData
{
    /// <summary>Entity that fired this projectile.</summary>
    public Entity Attacker;

    /// <summary>Entity being targeted.</summary>
    public Entity Target;

    /// <summary>Grid cell the target occupied at fire time.</summary>
    public int2 TargetCellAtFire;

    /// <summary>Damage to deliver on arrival.</summary>
    public int Damage;

    /// <summary>Seconds until the projectile arrives.</summary>
    public float TravelTime;

    /// <summary>Seconds elapsed since firing.</summary>
    public float Elapsed;
}

// ─────────────────────────────────────────────────────────────────────────────
//  Combat Occupancy Singleton  (managed — tracks which cells are claimed)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Tracks which grid cells are claimed by attacking units as their attack slots.
/// Prevents two melee units from claiming the same cell around a target.
///
/// Key   = grid cell (int2)
/// Value = attacker entity occupying that slot
///
/// Separate from NavGridSingleton (movement occupancy) — combat slots are
/// temporary and cleared when a unit leaves combat.
/// </summary>
public class CombatOccupancySingleton : IComponentData
{
    public NativeHashMap<int2, Entity> ClaimedCells;

    public bool IsCreated => ClaimedCells.IsCreated;

    public void Init()
        => ClaimedCells = new NativeHashMap<int2, Entity>(256, Unity.Collections.Allocator.Persistent);

    public void Dispose()
    {
        if (ClaimedCells.IsCreated) ClaimedCells.Dispose();
    }

    /// <summary>
    /// Try to claim a cell for an attacker.
    /// Returns false if already claimed by another unit.
    /// </summary>
    public bool TryClaim(int2 cell, Entity attacker)
    {
        if (ClaimedCells.TryGetValue(cell, out var existing) && existing != attacker)
            return false;
        ClaimedCells[cell] = attacker;
        return true;
    }

    public void Release(int2 cell, Entity attacker)
    {
        if (ClaimedCells.TryGetValue(cell, out var existing) && existing == attacker)
            ClaimedCells.Remove(cell);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Projectile Singleton  (holds all in-flight projectile data)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Singleton entity that holds the ProjectileFlight buffer.
/// One entity, one buffer — no per-projectile entities.
/// </summary>
public struct ProjectileSingleton : IComponentData { }

// ─────────────────────────────────────────────────────────────────────────────
//  Death tag  (enableable — enabled when CurrentHealth <= 0)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Enabled by DamageSystem when a unit's health reaches zero.
/// Other systems (animation, cleanup, spawn) react to this being enabled.
/// Never structurally added/removed.
/// </summary>
public struct Dead : IComponentData, IEnableableComponent { }
