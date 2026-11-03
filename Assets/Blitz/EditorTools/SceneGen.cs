using System.IO;
using BlitzLegacy.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BlitzLegacy.EditorTools
{
    /// <summary>
    /// Generates every scene from code. Scenes are outputs, not hand-edited
    /// sources, so the whole project can be rebuilt from C# alone.
    /// </summary>
    public static class SceneGen
    {
        public const string SceneDirectory = "Assets/Blitz/Scenes";
        public const string BootstrapScenePath = SceneDirectory + "/Bootstrap.unity";

        public static void GenerateAll()
        {
            Directory.CreateDirectory(SceneDirectory);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var bootstrap = new GameObject("BlitzBootstrap");
            bootstrap.AddComponent<BlitzBootstrap>();

            if (!EditorSceneManager.SaveScene(scene, BootstrapScenePath))
            {
                Debug.LogError("[Blitz] failed to save " + BootstrapScenePath);
                EditorApplication.Exit(1);
                return;
            }

            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene(BootstrapScenePath, true),
            };

            AssetDatabase.SaveAssets();
            Debug.Log("[Blitz] generated " + BootstrapScenePath);
            EditorApplication.Exit(0);
        }
    }
}
