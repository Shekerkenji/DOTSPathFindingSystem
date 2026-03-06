using UnityEngine;
using Unity.Entities;
using Shek.ECSGamePlay;

namespace Shek.ECSGamePlay
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(BuildingAuthoring))]
    public class HealingBuildingAuthoring : MonoBehaviour
    {
        public int healingRate;

        public class Baker : Baker<HealingBuildingAuthoring>
        {
            public override void Bake(HealingBuildingAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.None);
                AddComponent(entity, new HealingBuilding
                {
                    HealingRate = authoring.healingRate,
                });

            }
        }
    }
}