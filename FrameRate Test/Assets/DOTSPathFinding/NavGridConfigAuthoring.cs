using UnityEngine;
using Unity.Entities;

/// <summary>
/// Place on a GameObject in your SubScene.
/// Only one field needed — cell size.
/// All cells start walkable; obstacles are registered at runtime via NavGridOccupancyAPI.
/// </summary>
public class NavGridConfigAuthoring : MonoBehaviour
{
    [Tooltip("World-space width and depth of one navigation cell.\n" +
             "Smaller = more precise paths but more A* nodes.\n" +
             "Recommended: match your building grid snap size (e.g. 1 or 2 units).")]
    public float CellSize = 1f;

    public class Baker : Baker<NavGridConfigAuthoring>
    {
        public override void Bake(NavGridConfigAuthoring src)
        {
            var entity = GetEntity(TransformUsageFlags.None);
            AddComponent(entity, new NavGridConfig { CellSize = src.CellSize });
        }
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (CellSize <= 0f) return;
        Gizmos.color = new Color(0.2f, 1f, 0.4f, 0.15f);
        var o = transform.position;
        const int preview = 12;
        for (int x = -preview; x <= preview; x++)
        for (int z = -preview; z <= preview; z++)
        {
            var c = new Vector3(
                Mathf.Floor(o.x / CellSize) * CellSize + (x + 0.5f) * CellSize,
                o.y,
                Mathf.Floor(o.z / CellSize) * CellSize + (z + 0.5f) * CellSize);
            Gizmos.DrawWireCube(c, new Vector3(CellSize, 0.05f, CellSize));
        }
    }
#endif
}
