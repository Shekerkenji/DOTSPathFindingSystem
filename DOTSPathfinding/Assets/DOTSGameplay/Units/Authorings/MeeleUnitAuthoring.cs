using UnityEngine;
using Unity.Entities;


namespace Shek.ECSGamePlay
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(UnitAuthoring))]
    public class MeeleUnitAuthroing : MonoBehaviour
    {
        public class Baker : Baker<MeeleUnitAuthroing>
        {
            public override void Bake(MeeleUnitAuthroing authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent<Meele>(entity);
            }
        }

    }
}