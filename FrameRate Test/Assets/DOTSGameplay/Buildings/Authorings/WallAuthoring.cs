using UnityEngine;
using Unity.Entities;
using Shek.ECSGamePlay;


namespace Shek.ECSGamePlay
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(BuildingAuthoring))]
    public class WallAuthoring : MonoBehaviour
    {   

        public class Baker : Baker<WallAuthoring>
        {
            public override void Bake(WallAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent<WallTag>(entity);

            }
        }
    }
}