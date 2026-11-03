using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace BlitzLegacy.EditorTools
{
    /// <summary>
    /// Keeps Resources/Data/tank_models.txt, the list TankModelIndex reads,
    /// in step with the models: every folder under Resources/Models/Tanks
    /// that holds its own model, i.e. what Resources.Load("Models/Tanks/ID/ID")
    /// finds. Rewritten after any import, move or delete under that folder.
    /// </summary>
    public sealed class TankModelIndexWriter : AssetPostprocessor
    {
        public const string ModelsFolder = "Assets/Blitz/Resources/Models/Tanks";
        public const string IndexPath = "Assets/Blitz/Resources/Data/tank_models.txt";

        private static void OnPostprocessAllAssets(
            string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
        {
            if (Touches(importedAssets) || Touches(deletedAssets) ||
                Touches(movedAssets) || Touches(movedFromAssetPaths))
            {
                Write();
            }
        }

        /// <summary>The list as it should read now: the ids in ordinal order, one per line.</summary>
        public static string Build()
        {
            var ids = new List<string>();
            if (AssetDatabase.IsValidFolder(ModelsFolder))
            {
                foreach (var folder in AssetDatabase.GetSubFolders(ModelsFolder))
                {
                    var id = folder.Substring(folder.LastIndexOf('/') + 1);
                    if (HoldsModel(folder, id))
                    {
                        ids.Add(id);
                    }
                }
            }

            ids.Sort(StringComparer.Ordinal);
            var text = new StringBuilder();
            foreach (var id in ids)
            {
                text.Append(id).Append('\n');
            }

            return text.ToString();
        }

        /// <summary>Rewrites the list if it is out of date. True when it changed.</summary>
        public static bool Write()
        {
            var text = Build();
            if (File.Exists(IndexPath) && File.ReadAllText(IndexPath) == text)
            {
                return false;
            }

            File.WriteAllText(IndexPath, text);
            AssetDatabase.ImportAsset(IndexPath);
            Debug.Log("[Blitz] tank model index rewritten: " + IndexPath);
            return true;
        }

        /// <summary>A model asset named after its folder: ID/ID.obj and the like.</summary>
        private static bool HoldsModel(string folder, string id)
        {
            foreach (var file in Directory.GetFiles(folder, id + ".*"))
            {
                var path = file.Replace('\\', '/');
                if (path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileNameWithoutExtension(path) != id)
                {
                    continue;
                }

                if (AssetDatabase.GetMainAssetTypeAtPath(path) == typeof(GameObject))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool Touches(string[] paths)
        {
            foreach (var path in paths)
            {
                if (path.StartsWith(ModelsFolder + "/", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
