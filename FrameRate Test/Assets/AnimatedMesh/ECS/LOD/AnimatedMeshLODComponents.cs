using Unity.Entities;
using UnityEngine;
using UnityEngine.Rendering;

// =============================================================================
// AnimatedMeshLODComponents.cs
//
// All LOD-specific component types.  Core components in AnimatedMeshComponents.cs
// are NOT modified — full backward compatibility is preserved.
//
// Component map
// ?????????????
//  AnimatedMeshLODData             managed    SO refs + hash caches + shadow info
//  AnimatedMeshLODState            blittable  active level + squared thresholds
//  AnimatedMeshLODNeedsRebuild     enableable flip-bit tag (IEnableableComponent)
//  AnimatedMeshLODRebuildRequest   blittable  target LOD written by Burst job
//  AnimatedMeshLODSetupData        managed    bake-time payload (consumed once)
// =============================================================================


// ?? Managed: three SO assets + per-LOD materials + hash caches ???????????????

/// <summary>
/// Managed component holding all three LOD SO assets.
/// Populated once by <see cref="AnimatedMeshRenderInitSystem"/> from
/// <see cref="AnimatedMeshLODSetupData"/> plus the LOD0 data already on the entity.
/// </summary>
public class AnimatedMeshLODData : IComponentData
{
    // Highest ? lowest detail.  SO0 is always non-null (same as main SO).
    public AnimatedMeshScriptableObjectECS SO0;
    public AnimatedMeshScriptableObjectECS SO1; // null = LOD1 not configured
    public AnimatedMeshScriptableObjectECS SO2; // null = LOD2 not configured

    // Per-LOD material arrays.  LOD1/2 fall back to LOD0 when null.
    public Material[] Materials0;
    public Material[] Materials1;
    public Material[] Materials2;

    // Shadow settings shared across LODs (copied from MeshRenderer at init).
    public ShadowCastingMode ShadowMode;
    public bool ReceiveShadows;

    // Per-LOD clip-name hash caches — built once, never reallocated.
    public int[] ClipNameHashes0;
    public int[] ClipNameHashes1;
    public int[] ClipNameHashes2;

    // ?? Convenience accessors ?????????????????????????????????????????????????

    public AnimatedMeshScriptableObjectECS GetSO(int level) => level switch
    {
        2 => SO2 ?? SO1 ?? SO0,
        1 => SO1 ?? SO0,
        _ => SO0,
    };

    public Material[] GetMaterials(int level) => level switch
    {
        2 => Materials2 ?? Materials1 ?? Materials0,
        1 => Materials1 ?? Materials0,
        _ => Materials0,
    };

    public int[] GetHashes(int level) => level switch
    {
        2 => ClipNameHashes2 ?? ClipNameHashes0,
        1 => ClipNameHashes1 ?? ClipNameHashes0,
        _ => ClipNameHashes0,
    };

    // ?? Init helper ???????????????????????????????????????????????????????????

    public void BuildHashCaches()
    {
        ClipNameHashes0 = BuildCache(SO0);
        ClipNameHashes1 = BuildCache(SO1);
        ClipNameHashes2 = BuildCache(SO2);
    }

    private static int[] BuildCache(AnimatedMeshScriptableObjectECS so)
    {
        if (so == null) return System.Array.Empty<int>();
        var clips = so.Clips;
        var arr = new int[clips.Count];
        for (int i = 0; i < clips.Count; i++)
            arr[i] = clips[i].Name != null ? clips[i].Name.GetHashCode() : 0;
        return arr;
    }
}


// ?? Managed: bake-time setup payload ?????????????????????????????????????????

/// <summary>
/// Carries LOD authoring data from the baker into the init system.
/// Removed from the entity after first-time render setup.
/// </summary>
public class AnimatedMeshLODSetupData : IComponentData
{
    public AnimatedMeshScriptableObjectECS SO1;
    public AnimatedMeshScriptableObjectECS SO2;

    public Material[] Materials1; // null = reuse LOD0
    public Material[] Materials2; // null = reuse LOD1/LOD0

    public float LOD1Distance;
    public float LOD2Distance;

    // Stored so the rebuild system can recreate RenderMeshDescription
    // without referencing the original scene GameObject.
    public ShadowCastingMode ShadowMode;
    public bool ReceiveShadows;
}