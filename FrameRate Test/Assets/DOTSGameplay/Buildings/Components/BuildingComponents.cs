using Unity.Entities;
using Shek.ECSGamePlay;
using UnityEngine;

namespace Shek.ECSGamePlay
{
    public struct Building : IComponentData
    {
        public int Id;
        public Faction Faction;
        public byte width;
        public byte height;
        public int UnitCapacity;
        public Vector3 OutLocation; //where the spawned, or sheltered untits go after exiting the building
    }

    public struct ShelteredUnit : IBufferElementData
    {
        public Entity Unit;
    }

    public struct PopulationAccomodation : IComponentData
    {
        public int PopulationIncrement;
    }

    public struct AttackBuilding : IComponentData
    {
        public int AttackDamage;
        public float AttackSpeed;
        public int AttackRange;
    }

    public struct UnitGenerator : IComponentData
    {

        public int QueueSize;
        public int QueueCapacity;
    }

    public struct SpawnableUnitId : IBufferElementData
    {
        public ResourceType ResourceType;
        public int Cost;
        public int UnitId;
    }

    public struct ResourceGatheringBuilding : IComponentData
    {
        public ResourceType ResourceType;
        public int GatheringRate; //how many per second
    }

    public struct ResourceStorageBuilding : IComponentData
    {
        public ResourceType ResourceType;
        public int StorageCapacity;
    }

    public struct HealingBuilding : IComponentData 
    {
        public int HealingRate; //how many per second
    }

    public struct WallTag : IComponentData { }
    public struct  GateTag : IComponentData { }

    public struct BuildingUpgrade : IComponentData, IEnableableComponent
    {
        public int UpgradeId;
        public int TimeRemaining;
        public int TotalUpgradeTime;
        public int UpgradeCost;
    }

    public enum ResourceType : byte
    {
        None = 0,
        Gold = 1,
        Wood = 2,
        Stone = 3,
        Food = 4,
    }

    



}