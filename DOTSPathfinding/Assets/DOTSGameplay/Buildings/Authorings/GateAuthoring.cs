using UnityEngine;
using Unity.Entities;


namespace Shek.ECSGamePlay
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(BuildingAuthoring))]
    public class GateAuthoring : MonoBehaviour
    {

        public class Baker : Baker<GateAuthoring>
        {
            public override void Bake(GateAuthoring authoring)
            {
               var entity = GetEntity(TransformUsageFlags.None);
               AddComponent<GateTag>(entity);

            }
        }
    }
}