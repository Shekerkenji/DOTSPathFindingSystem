using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Shek.ECSGameplay
{
    /// <summary>
    /// Add to any GameObject that is a combat-capable unit.
    /// Works alongside DotsNavAgentAuthoring — both can live on the same prefab.
    /// </summary>
    public class CombatAgentAuthoring : MonoBehaviour
    {
        [Header("Identity")]
        public string unitName = "Unit";
        public float radius = 0.5f;
        public int factionId = 0;

        [Header("Health")]
        public int maxHealth = 100;
        public float regenRate = 2f;
        public float outOfCombatDelay = 5f;

        [Header("Weapon")]
        public WeaponType weaponType = WeaponType.Melee;
        public string weaponName = "Fists";
        public float weaponRange = 1.5f;
        public float damageMult = 1f;
        public float speedMult = 1f;
        public float detectionRange = 20f;

        [Header("Attack")]
        public int baseDamage = 10;
        public float baseAttackSpeed = 1f;  // Attacks per second

        [Header("Detection")]
        public float detectionRadius = 15f;
        public float chaseRange = 20f;
        public float pingRadius = 10f;
        public LayerMask obstacleLayers;
        public float scanInterval = 0.25f;

        [Header("Melee Slots")]
        public int maxMeleeSlots = 4;
    }

    public class CombatAgentBaker : Baker<CombatAgentAuthoring>
    {
        public override void Bake(CombatAgentAuthoring a)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);

            AddComponent(entity, new UnitData
            {
                Name = a.unitName,
                Radius = a.radius,
                FactionId = a.factionId
            });

            AddComponent(entity, new FactionTag { FactionId = a.factionId });

            AddComponent(entity, new HealthComponent
            {
                Current = a.maxHealth,
                Max = a.maxHealth,
                RegenRate = a.regenRate,
                TimeSinceLastDamage = 9999f,
                OutOfCombatDelay = a.outOfCombatDelay
            });

            AddComponent(entity, new MovementComponent
            {
                CurrentSpeed = 0f,
                BaseSpeed = 0f  // filled at runtime by NavigationSystem speed
            });

            AddComponent(entity, new Weapon
            {
                Name = a.weaponName,
                Type = a.weaponType,
                DamageMult = a.damageMult,
                Range = a.weaponRange,
                SpeedMult = a.speedMult,
                DetectionRange = a.detectionRange > 0 ? a.detectionRange : a.weaponRange
            });
            SetComponentEnabled<Weapon>(entity, true);

            float cooldown = 1f / math.max(0.01f, a.baseAttackSpeed * a.speedMult);
            AddComponent(entity, new AttackComponent
            {
                BaseDamage = a.baseDamage,
                BaseAttackSpeed = a.baseAttackSpeed,
                AttackCooldown = cooldown,
                LastAttackTime = -cooldown  // Ready from spawn
            });

            AddComponent(entity, new AIState { State = UnitState.Idle, StateTimer = 0f });

            AddComponent(entity, new DetectionComponent
            {
                DetectionRadius = a.detectionRadius,
                ChaseRange = a.chaseRange,
                PingRadius = a.pingRadius,
                ObstacleLayers = a.obstacleLayers,
                ScanInterval = a.scanInterval,
                NextScanTime = 0f
            });

            AddComponent(entity, new CurrentTarget
            {
                TargetEntity = Entity.Null,
                LastKnownPosition = float3.zero,
                HasTarget = 0
            });

            AddComponent(entity, new MeleeSlotComponent
            {
                CurrentMeleeAttackers = 0,
                CurrentRangedAttackers = 0,
                MaxMeleeSlots = a.maxMeleeSlots
            });

            AddComponent(entity, new MeleeSlotAssignment());
            SetComponentEnabled<MeleeSlotAssignment>(entity, false);

            // One-shot event components — disabled at spawn
            AddComponent(entity, new AttackHitEvent());
            SetComponentEnabled<AttackHitEvent>(entity, false);

            AddComponent(entity, new DamageReceivedEvent());
            SetComponentEnabled<DamageReceivedEvent>(entity, false);

            AddComponent(entity, new DeadTag());
            SetComponentEnabled<DeadTag>(entity, false);
        }
    }
}