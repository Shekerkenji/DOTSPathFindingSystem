using Unity.Entities;
using UnityEngine;

// =============================================================================
// AnimatedMeshLODAuthoring.cs
//
// Add this component to the SAME GameObject as AnimatedMeshAuthoring to enable
// LOD support.  AnimatedMeshAuthoring.AnimationData is automatically treated as
// LOD0 (highest detail).  This component adds LOD1 and LOD2.
//
// Inspector setup
// ???????????????
//   1. Add AnimatedMeshAuthoring   ? assign high-detail SO to AnimationData.
//   2. Add AnimatedMeshLODAuthoring ? assign SO1 / SO2 and set distances.
//   3. Optionally assign per-LOD material overrides (leave empty = reuse LOD0).
//
// Backward compatibility
// ???????????????????????
//   Entities without AnimatedMeshLODAuthoring gain no new components at bake
//   time and are completely invisible to both LOD runtime systems, which guard
//   themselves with RequireForUpdate<AnimatedMeshLODState>.
// =============================================================================

/// <summary>
/// Optional companion to <see cref="AnimatedMeshAuthoring"/>.
/// Assigns medium- and low-detail animation SOs for LOD1 and LOD2.
/// </summary>
[RequireComponent(typeof(AnimatedMeshAuthoring))]
public class AnimatedMeshLODAuthoring : MonoBehaviour
{
    [Header("LOD Scriptable Objects")]
    [Tooltip("Medium-detail animation data (LOD1). Leave null to disable LOD1.")]
    public AnimatedMeshScriptableObjectECS AnimationDataLOD1;

    [Tooltip("Low-detail animation data (LOD2). Leave null to disable LOD2.")]
    public AnimatedMeshScriptableObjectECS AnimationDataLOD2;

    [Header("LOD Distance Thresholds (world units from camera)")]
    [Tooltip("Switch from LOD0 ? LOD1 beyond this distance.")]
    [Min(0f)] public float LOD1Distance = 20f;

    [Tooltip("Switch from LOD1 ? LOD2 beyond this distance.")]
    [Min(0f)] public float LOD2Distance = 50f;

    [Header("Optional Per-LOD Material Overrides")]
    [Tooltip("Leave empty to reuse the MeshRenderer's materials for LOD1.")]
    public Material[] MaterialsLOD1;

    [Tooltip("Leave empty to reuse the MeshRenderer's materials for LOD2.")]
    public Material[] MaterialsLOD2;
}


// =============================================================================
// Baker
// =============================================================================

public class AnimatedMeshLODBaker : Baker<AnimatedMeshLODAuthoring>
{
    public override void Bake(AnimatedMeshLODAuthoring authoring)
    {
        // Base authoring must exist and have a valid SO (LOD0).
        var baseAuthoring = authoring.GetComponent<AnimatedMeshAuthoring>();
        if (baseAuthoring == null || baseAuthoring.AnimationData == null) return;

        var renderer = authoring.GetComponent<MeshRenderer>();
        if (renderer == null) return;

        Entity e = GetEntity(TransformUsageFlags.Renderable);

        // ?? LOD setup payload (consumed by AnimatedMeshRenderInitSystem) ?????
        AddComponentObject(e, new AnimatedMeshLODSetupData
        {
            SO1 = authoring.AnimationDataLOD1,
            SO2 = authoring.AnimationDataLOD2,

            // Null means "inherit LOD0 materials" — resolved at init time.
            Materials1 = authoring.MaterialsLOD1?.Length > 0 ? authoring.MaterialsLOD1 : null,
            Materials2 = authoring.MaterialsLOD2?.Length > 0 ? authoring.MaterialsLOD2 : null,

            LOD1Distance = authoring.LOD1Distance,
            LOD2Distance = authoring.LOD2Distance,

            ShadowMode = renderer.shadowCastingMode,
            ReceiveShadows = renderer.receiveShadows,
        });

        // ?? Blittable LOD state ???????????????????????????????????????????????
        float d1 = authoring.LOD1Distance;
        float d2 = authoring.LOD2Distance;
        AddComponent(e, new AnimatedMeshLODState
        {
            ActiveLOD = 0,
            LOD1DistanceSq = d1 * d1,
            LOD2DistanceSq = d2 * d2,
        });

        // ?? Rebuild request (present but empty at start) ??????????????????????
        AddComponent(e, new AnimatedMeshLODRebuildRequest { TargetLOD = 0 });

        // ?? Enableable rebuild flag (starts disabled) ?????????????????????????
        AddComponent(e, new AnimatedMeshLODNeedsRebuild());
        SetComponentEnabled<AnimatedMeshLODNeedsRebuild>(e, false);
    }
}