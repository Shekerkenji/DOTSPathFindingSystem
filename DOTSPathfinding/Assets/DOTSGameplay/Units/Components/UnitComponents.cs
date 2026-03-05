using System;
using Unity.Collections;
using Unity.Entities;

namespace Shek.ECSGamePlay
{
    public struct UnitActive : IComponentData, IEnableableComponent { }
    public struct Unit : IComponentData
    {
        public int Id; //bind an id with a name
        public Faction Faction;
        public UnitState State;
        public byte PopulationCost;
        public byte Size; //cell size;
        public int AttackDamage;
        public float AttackSpeed; 
        public float MovementSpeed;
    }

    public struct HealthComponent : IComponentData
    {
        public int MaxHealth;
        public int CurrentHealth;
    }
    public struct UnitAI : IComponentData
    {  
        //cell based
        public int AttackRange;
        public int DetectionRange;
        public int ChaseRange;
    }

    public enum Faction : byte
    {
        Green = 0,
        Red = 1,
        Blue = 2,
        Yellow = 3,
        Orange = 4,
    }

    public struct Attackers : IComponentData
    {
        public byte MeeleAttackers;
        public byte CellsOccupied;
        public byte CellsAvaliable;
        public int RangedAttacker;
    }

    public struct Worker : IComponentData
    {
        
    }

    public struct Meele : IComponentData
    {

    }

    public struct Ranged : IComponentData
    {

    }

    public enum UnitState : byte
    {
        Aggressive = 0,
        Defensive = 1,
        Stationed = 2,
    }

    public struct Hero : IComponentData
    {
        public float HealthModifier;
        public float AttackModifier;
        public float MovementSpeedModifer;
        public float AttackSpeedModifier;
    }

    public struct Legend : IComponentData
    {
        public float HealthModifier;
        public float AttackModifier;
        public float MovementSpeedModifer;
        public float AttackSpeedModifier;
    }

    public struct Group : IComponentData 
    {
        public FixedString64Bytes Title;
        public int id;
        public Entity BannerHolder;
        public int GroupCapacity;
        public UnitState GroupState;
    }

    public struct GroupMemeber : IBufferElementData
    {
        public Entity Member;
    }
    public struct BigGroup : IComponentData
    {
        public FixedString512Bytes Title;
        public int id;
        public Entity BannerHolder;
    }

    public struct BigGroupMember : IBufferElementData
    {
        public Group Member;
    }



    




}