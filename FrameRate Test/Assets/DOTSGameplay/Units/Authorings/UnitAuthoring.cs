
using Unity.Entities;
using UnityEngine;
using static UnityEngine.EventSystems.EventTrigger;



[DisallowMultipleComponent]
public class UnitAuthoring : MonoBehaviour
{
    public int id;
    public Faction faction;
    public UnitStance state;
    public byte populationCost;
    public byte size; //cell size;
    public int health;
    public int attackDamage;
    public float attackSpeed;
    public float movementSpeed;
    public int attackRange;
    public int detectionRange;
    public int chaseRange;

    [Header("Visuals")]
    public GameObject selected;


    public class Baker : Baker<UnitAuthoring>
    {
        public override void Bake(UnitAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);

            AddComponent(entity, new Unit
            {
                Id = authoring.id,
                Faction = authoring.faction,
                State = UnitStance.Aggressive,
                PopulationCost = authoring.populationCost,
                Size = authoring.size,
                AttackDamage = authoring.attackDamage,
                AttackSpeed = authoring.attackSpeed,
                MovementSpeed = authoring.movementSpeed,
            });

            AddComponent(entity, new HealthComponent
            {
                MaxHealth = authoring.health,
                CurrentHealth = authoring.health,
            });

            AddComponent(entity, new UnitAI
            {
                AttackRange = authoring.attackRange,
                DetectionRange = authoring.detectionRange,
                ChaseRange = authoring.chaseRange,
            });

            AddComponent<Attackers>(entity);
        }
    }




}
