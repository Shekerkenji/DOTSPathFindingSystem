using UnityEngine;
using Unity.Entities;
using Shek.ECSGamePlay;


namespace Shek.ECSGamePlay
{
    [DisallowMultipleComponent]
    public class BuildingAuthoring : MonoBehaviour
    {
        public int id;
        public Faction faction;
        public byte width;
        public byte height;
        public int unitCapacity;
        public Vector3 outLocation;
        public int maxHealth;


        public class Baker : Baker<BuildingAuthoring>
        {
            public override void Bake(BuildingAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.None);

                AddComponent(entity, new Building
                {
                    Id = authoring.id,
                    Faction = authoring.faction,
                    width = authoring.width,
                    height = authoring.height,
                    UnitCapacity = authoring.unitCapacity,
                    OutLocation = authoring.outLocation,
                });

                if (authoring.unitCapacity > 0)
                    AddBuffer<ShelteredUnit>(entity);
                AddComponent<BuildingUpgrade>(entity);

                AddComponent(entity, new HealthComponent
                {
                    MaxHealth = authoring.maxHealth,
                    CurrentHealth = authoring.maxHealth,
                });

            }
        }
    }
}