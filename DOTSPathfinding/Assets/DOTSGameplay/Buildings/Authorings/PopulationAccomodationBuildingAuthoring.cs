using UnityEngine;
using Unity.Entities;
using Shek.ECSGamePlay;

namespace Shek.ECSGamePlay
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(BuildingAuthoring))]
    public class PopulationAcomodationBuildingAuthoring : MonoBehaviour
    {
        public int populationIncrement;
        public class Baker : Baker<PopulationAcomodationBuildingAuthoring>
        {
            public override void Bake(PopulationAcomodationBuildingAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.None);
                AddComponent(entity, new PopulationAccomodation
                {
                    PopulationIncrement = authoring.populationIncrement,
                });
            }
        }
    }
}