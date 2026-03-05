using UnityEngine;
using Unity.Entities;


namespace Shek.ECSGamePlay
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(UnitAuthoring))]
    public class RangedUnitAuthroing : MonoBehaviour
    {
        public class Baker : Baker<RangedUnitAuthroing>
        {
            public override void Bake(RangedUnitAuthroing authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent<Ranged>(entity);
            }
        }




    }
}