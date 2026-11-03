using BlitzLegacy.Network;
using Fusion;
using Fusion.Editor;
using UnityEditor;
using UnityEngine;

namespace BlitzLegacy.EditorTools
{
    /// <summary>
    /// Generates the single networked prefab the Fusion session spawns and
    /// registers it in Fusion's prefab table. Headless: Tools/unity.sh net-setup.
    /// </summary>
    public static class NetPrefabSetup
    {
        const string Folder = "Assets/Blitz/Network/Resources/BlitzNet";
        const string PrefabPath = Folder + "/NetBattle.prefab";

        [MenuItem("Blitz/Network/Generate NetBattle prefab")]
        public static void Generate()
        {
            if (!AssetDatabase.IsValidFolder(Folder))
            {
                System.IO.Directory.CreateDirectory(Folder);
                AssetDatabase.Refresh();
            }
            var go = new GameObject("NetBattle");
            try
            {
                go.AddComponent<NetworkObject>();
                go.AddComponent<NetBattle>();
                PrefabUtility.SaveAsPrefabAsset(go, PrefabPath, out var ok);
                if (!ok) throw new System.Exception("prefab save failed: " + PrefabPath);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
            AssetDatabase.SaveAssets();
            NetworkProjectConfigUtilities.RebuildPrefabTable();
            AssetDatabase.SaveAssets();
            Debug.Log("[Blitz] net prefab ok: " + PrefabPath);
        }

        /// <summary>
        /// Builds embed the Photon App ID from <c>BLITZ_PHOTON_APP_ID</c> when
        /// CI provides it; the resulting resource is gitignored. Without an id
        /// the build simply does not offer online play.
        /// </summary>
        public static void EnsureAppIdResource()
        {
            var id = System.Environment.GetEnvironmentVariable(NetAppSettings.EnvironmentVariable);
            var path = Folder + "/photon_app_id.txt";
            if (NetAppSettings.IsValidAppId(id))
            {
                var trimmed = id.Trim();
                if (!System.IO.File.Exists(path) || System.IO.File.ReadAllText(path).Trim() != trimmed)
                {
                    System.IO.Directory.CreateDirectory(Folder);
                    System.IO.File.WriteAllText(path, trimmed);
                    AssetDatabase.ImportAsset(path);
                }
            }
            if (!NetAppSettings.HasAppId())
            {
                Debug.LogWarning("[Blitz] no Photon App ID: this build offers no online play");
            }
        }

        public static void Run()
        {
            try
            {
                Generate();
                EditorApplication.Exit(0);
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
                EditorApplication.Exit(1);
            }
        }
    }
}
