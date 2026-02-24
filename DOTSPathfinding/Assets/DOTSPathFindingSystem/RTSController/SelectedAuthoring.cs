using Unity.Entities;
using UnityEngine;

namespace Navigation.ECS
{
    /// <summary>
    /// Add this component to any unit GameObject to make it selectable by the RTS controller.
    /// The baker adds the Selected enableable tag (disabled by default).
    /// RTSSystem toggles it at runtime — no structural changes ever occur.
    ///
    /// USAGE:
    ///   Add alongside UnitAuthoring on any unit prefab/GameObject.
    ///   No inspector fields needed — presence of this authoring is the flag.
    /// </summary>
    [AddComponentMenu("Navigation/RTS/Selectable Unit")]
    [DisallowMultipleComponent]
    public class SelectedAuthoring : MonoBehaviour { }

    public class SelectedBaker : Baker<SelectedAuthoring>
    {
        public override void Bake(SelectedAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            AddComponent<Selected>(entity);
            SetComponentEnabled<Selected>(entity, false); // Disabled until player selects it
        }
    }
}