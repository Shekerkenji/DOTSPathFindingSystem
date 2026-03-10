using UnityEngine;
using Unity.Entities;
using Shek.ECSGamePlay;


namespace Shek.ECSGamePlay
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(BuildingAuthoring))]
    public class ResourceGatheringBuildingAuthoring : MonoBehaviour
    {
        public ResourceType resourceType;
        public int resourceCapacity;

        public class Baker : Baker<ResourceGatheringBuildingAuthoring>
        {
            public override void Bake(ResourceGatheringBuildingAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.None);
                AddComponent(entity, new ResourceStorageBuilding
                {
                    StorageCapacity = authoring.resourceCapacity,
                    ResourceType = authoring.resourceType,
                });

            }
        }
    }
}