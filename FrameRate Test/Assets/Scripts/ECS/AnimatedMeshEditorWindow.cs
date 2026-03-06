#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Bakes SkinnedMeshRenderer animations into per-frame Mesh assets stored in
/// an AnimatedMeshScriptableObject, ready for ECS playback.
/// Open via Tools → Animated Mesh Creator.
/// </summary>
public class AnimatedMeshEditorWindowECS : EditorWindow
{
    [MenuItem("Tools/Animated Mesh Creator")]
    public static void Open()
    {
        var w = GetWindow<AnimatedMeshEditorWindowECS>();
        w.titleContent = new GUIContent("Animated Mesh Creator ECS");
        w.minSize = new Vector2(340, 200);
    }

    // ── Inspector state ───────────────────────────────────────────────────────
    private GameObject _model;
    private string _assetName;
    private int _fps = 30;
    private bool _optimize = false;
    private bool _dryRun = false;

    private const string BASE_PATH = "Assets/Animated Models/";

    // ── GUI ───────────────────────────────────────────────────────────────────
    private void OnGUI()
    {
        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Animated Mesh Creator", EditorStyles.boldLabel);
        EditorGUILayout.Space(4);

        var newModel = EditorGUILayout.ObjectField(
            "Animated Model", _model, typeof(GameObject), true) as GameObject;

        if (newModel != _model && newModel != null)
            _assetName = newModel.name + " animations";
        _model = newModel;

        Animator animator = _model == null ? null : _model.GetComponentInChildren<Animator>();

        _assetName = EditorGUILayout.TextField("Asset Name", _assetName);
        _fps = EditorGUILayout.IntSlider("Animation FPS", _fps, 1, 120);
        _optimize = EditorGUILayout.Toggle(
            new GUIContent("Optimize Meshes",
                           "Calls Mesh.Optimize() — smaller but slower to bake."), _optimize);
        _dryRun = EditorGUILayout.Toggle(
            new GUIContent("Dry Run",
                           "Processes everything but writes no assets. Good for testing."), _dryRun);

        EditorGUILayout.Space(6);

        bool canBake = _model != null
                    && animator != null
                    && animator.runtimeAnimatorController != null
                    && !string.IsNullOrWhiteSpace(_assetName);

        using (new EditorGUI.DisabledScope(!canBake))
        {
            if (GUILayout.Button(_dryRun ? "Dry Run" : "Bake Animations", GUILayout.Height(32)))
                Bake(animator);
        }

        if (!canBake && _model != null)
        {
            if (animator == null)
                EditorGUILayout.HelpBox("No Animator found on the model.", MessageType.Warning);
            else if (animator.runtimeAnimatorController == null)
                EditorGUILayout.HelpBox("Animator has no RuntimeAnimatorController assigned.", MessageType.Warning);
        }

        EditorGUILayout.Space(4);
        if (GUILayout.Button("Clear Progress Bar"))
            EditorUtility.ClearProgressBar();
    }

    // ── Baking ────────────────────────────────────────────────────────────────
    private void Bake(Animator animator)
    {
        var so = CreateInstance<AnimatedMeshScriptableObjectECS>();
        so.AnimationFPS = _fps;

        AnimationClip[] clips = animator.runtimeAnimatorController.animationClips;
        Debug.Log($"[AnimatedMesh] Baking {clips.Length} clip(s) → \"{_assetName}\"  FPS={_fps}");

        string root = BASE_PATH + _assetName + "/";

        if (!_dryRun)
            EnsurePath(BASE_PATH + _assetName);

        int clipIdx = 0;
        try
        {
            foreach (var clip in clips)
            {
                clipIdx++;
                EditorUtility.DisplayProgressBar(
                    "Baking Animations",
                    $"{clip.name}  ({clipIdx}/{clips.Length})",
                    clipIdx / (float)clips.Length);

                var frames = new List<Mesh>();
                float step = 1f / _fps;

                animator.Play(clip.name);

                for (float t = step; t < clip.length; t += step)
                {
                    animator.Update(step);

                    foreach (var smr in _model.GetComponentsInChildren<SkinnedMeshRenderer>())
                    {
                        var mesh = new Mesh();
                        smr.BakeMesh(mesh, true);
                        if (_optimize) mesh.Optimize();

                        if (!_dryRun)
                        {
                            string clipFolder = root + SanitizeName(clip.name);
                            EnsurePath(clipFolder);
                            AssetDatabase.CreateAsset(mesh, $"{clipFolder}/{t:N4}.asset");
                        }

                        frames.Add(mesh);
                    }
                }

                so.Clips.Add(new AnimatedMeshScriptableObjectECS.AnimClip
                {
                    Name = clip.name,
                    Frames = frames,
                });

                Debug.Log($"  [{clipIdx}] \"{clip.name}\" → {frames.Count} frames");
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        if (!_dryRun)
        {
            int total = so.Clips.Sum(c => c.Frames.Count);
            Debug.Log($"[AnimatedMesh] Done. {so.Clips.Count} clips, {total} total frames.");
            EditorUtility.SetDirty(so);
            AssetDatabase.CreateAsset(so, BASE_PATH + _assetName + ".asset");
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }
        else
        {
            Debug.Log("[AnimatedMesh] Dry run complete. No assets written.");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static void EnsurePath(string path)
    {
        string[] parts = path.Split('/');
        string cur = string.Empty;
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part)) continue;
            cur = string.IsNullOrEmpty(cur) ? part : cur + "/" + part;
            if (!AssetDatabase.IsValidFolder(cur))
                System.IO.Directory.CreateDirectory(cur);
        }
    }

    private static string SanitizeName(string n) =>
        string.Concat(n.Split(System.IO.Path.GetInvalidFileNameChars()));
}
#endif