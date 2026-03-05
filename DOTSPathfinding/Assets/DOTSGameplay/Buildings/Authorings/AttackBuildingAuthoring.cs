using UnityEngine;
using Unity.Entities;
using Shek.ECSGamePlay;

namespace Shek.ECSGamePlay
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(BuildingAuthoring))]
    public class AttackBuildingAuthoring : MonoBehaviour
    {
        public int attackDamage;
        public float attackSpeed;
        public int attackRange;

        public class Baker : Baker<AttackBuildingAuthoring>
        {
            public override void Bake(AttackBuildingAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.None);

                AddComponent(entity, new AttackBuilding
                {
                    AttackDamage = authoring.attackDamage,
                    AttackSpeed = authoring.attackSpeed,
                    AttackRange = authoring.attackRange,
                });

            }
        }
    }
}