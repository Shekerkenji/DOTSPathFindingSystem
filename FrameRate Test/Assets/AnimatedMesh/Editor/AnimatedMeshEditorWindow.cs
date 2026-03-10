using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

public class AnimatedMeshEditorWindow : EditorWindow
{
    [MenuItem("Tools/Animated Mesh Creator")]
    public static void CreateEditorWindow()
    {
        EditorWindow window = GetWindow<AnimatedMeshEditorWindow>();
        window.titleContent = new GUIContent("Animated Mesh Editor");
    }

    private GameObject AnimatedModel;
    private int AnimationFPS = 30;
    private string Name;
    private bool Optimize = false;
    private bool DryRun = false;

    private const string BASE_PATH = "Assets/Animated Models/";

    private void OnGUI()
    {
        GameObject newAnimatedModel = EditorGUILayout.ObjectField("Animated Model", AnimatedModel, typeof(GameObject), true) as GameObject;
        if (newAnimatedModel != AnimatedModel && newAnimatedModel != null)
        {
            Name = newAnimatedModel.name + " animations";
        }

        Animator animator = newAnimatedModel == null ? null : newAnimatedModel.GetComponentInChildren<Animator>();
        AnimatedModel = newAnimatedModel;

        Name = EditorGUILayout.TextField("Name", Name);
        AnimationFPS = EditorGUILayout.IntSlider("Animation FPS", AnimationFPS, 1, 100);
        Optimize = EditorGUILayout.Toggle("Optimize", Optimize);
        DryRun = EditorGUILayout.Toggle("Dry Run", DryRun);

        GUI.enabled = newAnimatedModel != null && animator != null && animator.runtimeAnimatorController != null;
        if (GUILayout.Button("Generate ScriptableObjects"))
        {
            if (newAnimatedModel == null || animator == null)
                return;

            if (!DryRun)
                GenerateFolderPaths(BASE_PATH + Name);

            GenerateModels(animator, DryRun);
        }
        GUI.enabled = true;

        if (GUILayout.Button("Clear progress bar"))
        {
            EditorUtility.ClearProgressBar();
        }
    }

    private void GenerateFolderPaths(string FullPath)
    {
        string[] requiredFolders = FullPath.Split("/");
        string path = string.Empty;
        for (int i = 0; i < requiredFolders.Length; i++)
        {
            if (i > 0) path += "/";
            path += requiredFolders[i];
            if (!string.IsNullOrEmpty(path) && !AssetDatabase.IsValidFolder(path))
            {
                System.IO.Directory.CreateDirectory(path);
            }
        }
    }

    private void GenerateModels(Animator animator, bool dryRun)
    {
        AnimatedMeshScriptableObject scriptableObject = CreateInstance<AnimatedMeshScriptableObject>();
        scriptableObject.AnimationFPS = AnimationFPS;

        AnimationClip[] clips = animator.runtimeAnimatorController.animationClips;
        Debug.Log($"Found {clips.Length} clips. Creating SO with name \"{Name}\" with Animation FPS {AnimationFPS}");

        string parentFolder = BASE_PATH + Name + "/";
        int clipIndex = 1;

        try
        {
            foreach (AnimationClip clip in clips)
            {
                Debug.Log($"Processing clip {clipIndex}: \"{clip.name}\". Length: {clip.length:N4}.");
                EditorUtility.DisplayProgressBar(
                    "Processing Animations",
                    $"Processing animation {clip.name} ({clipIndex} / {clips.Length})",
                    clipIndex / (float)clips.Length);

                List<Mesh> meshes = new();
                AnimatedMeshScriptableObject.Animation animation = new();
                animation.Name = clip.name;

                float increment = 1f / AnimationFPS;
                animator.Play(clip.name);

                for (float time = increment; time < clip.length; time += increment)
                {
                    Debug.Log($"Processing {clip.name} frame {time:N4}");
                    animator.Update(increment);

                    foreach (SkinnedMeshRenderer smr in AnimatedModel.GetComponentsInChildren<SkinnedMeshRenderer>())
                    {
                        Mesh mesh = new Mesh();
                        smr.BakeMesh(mesh, true);

                        if (Optimize)
                            mesh.Optimize();

                        if (!dryRun)
                        {
                            string clipFolder = parentFolder + clip.name;
                            if (!AssetDatabase.IsValidFolder(clipFolder))
                            {
                                Debug.Log("Creating folder: " + clipFolder);
                                System.IO.Directory.CreateDirectory(clipFolder);
                            }
                            AssetDatabase.CreateAsset(mesh, clipFolder + $"/{time:N4}.asset");
                        }

                        meshes.Add(mesh);
                    }
                }

                Debug.Log($"Setting {clip.name} to have {meshes.Count} meshes");
                animation.Meshes = meshes;
                scriptableObject.Animations.Add(animation);
                clipIndex++;
            }
        }
        finally
        {
            // Always clear the progress bar, even if an exception occurs
            EditorUtility.ClearProgressBar();
        }

        if (!dryRun)
        {
            Debug.Log($"Creating asset with {scriptableObject.Animations.Count} animations and {scriptableObject.Animations.Sum(a => a.Meshes.Count)} meshes");
            EditorUtility.SetDirty(scriptableObject);
            AssetDatabase.CreateAsset(scriptableObject, BASE_PATH + Name + ".asset");
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }
    }
}