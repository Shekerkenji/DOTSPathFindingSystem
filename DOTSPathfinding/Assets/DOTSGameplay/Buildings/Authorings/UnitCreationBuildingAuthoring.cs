using UnityEngine;
using Unity.Entities;
using Shek.ECSGamePlay;

namespace Shek.ECSGamePlay
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(BuildingAuthoring))]
    public class UnitCreationBuildingAuthoring : MonoBehaviour
    {
        public int queueSize;
        public int queueCapacity;

        public SpawnUnitID[] unitList;

        public class Baker : Baker<UnitCreationBuildingAuthoring>
        {
            public override void Bake(UnitCreationBuildingAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);

                AddComponent(entity, new UnitGenerator
                {
                    QueueSize = authoring.queueSize,
                    QueueCapacity = authoring.queueCapacity,
                });

                var unit = AddBuffer<SpawnableUnitId>(entity);
                foreach (var spawnUnit in authoring.unitList) 
                {
                    unit.Add(new SpawnableUnitId
                    {
                        ResourceType = spawnUnit.resourceType,
                        Cost = spawnUnit.cost,
                        UnitId = spawnUnit.unitId,
                    });
                }

            }
        }
    }

    public struct SpawnUnitID
    {
        public ResourceType resourceType;
        public int cost;
        public int unitId;
    }
}