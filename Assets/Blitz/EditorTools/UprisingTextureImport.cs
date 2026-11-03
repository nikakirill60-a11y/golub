using System.IO;
using BlitzLegacy.Core.Serialization;
using UnityEditor;
using UnityEngine;
namespace BlitzLegacy.EditorTools
{
    /// <summary>Preserve decoded original texels; noise/masks are data, not gamma-encoded colour.</summary>
    public sealed class UprisingTextureImport : AssetPostprocessor
    {
        private static bool IsData(string path)
        {
            if (path == "Assets/Blitz/Resources/Legacy1120Crash/normal.png" ||
                path == "Assets/Blitz/Resources/Legacy1120Crash/roughMetal.png") return true;
            var manifestPath="Assets/Blitz/Resources/Data/uprising_source_manifest.json";
            if(!File.Exists(manifestPath))return false;
            var manifest=JsonNode.Parse(File.ReadAllText(manifestPath));
            foreach(var texture in manifest["textures"].Fields)
            {
                if("Assets/Blitz/Resources/"+texture.Value["resource"].AsString()+".png"!=path)continue;
                foreach(var usage in texture.Value["usage"].Items)if(usage.AsString()=="noisePath" || usage.AsString()=="maskSpritePath" || usage.AsString()=="flowmapPath" || usage.AsString()=="alphaRemapPath")return true;
            }
            return false;
        }
        private void OnPreprocessTexture()
        {
            if(!assetPath.StartsWith("Assets/Blitz/Resources/Uprising/",System.StringComparison.Ordinal) && !assetPath.StartsWith("Assets/Blitz/Resources/Legacy480/",System.StringComparison.Ordinal) && !assetPath.StartsWith("Assets/Blitz/Resources/Legacy1120Crash/",System.StringComparison.Ordinal))return;
            var importer=(TextureImporter)assetImporter;importer.sRGBTexture=!IsData(assetPath);importer.textureCompression=TextureImporterCompression.Uncompressed;importer.mipmapEnabled=false;importer.alphaIsTransparency=false;importer.filterMode=FilterMode.Bilinear;
        }
        public static void Apply()
        {
            var changed=0;
            foreach(var path in Directory.GetFiles("Assets/Blitz/Resources/Uprising","*.png",SearchOption.AllDirectories))
            {
                var importer=AssetImporter.GetAtPath(path) as TextureImporter;if(importer==null)continue;
                var data=IsData(path);
                if(importer.sRGBTexture==!data && !importer.mipmapEnabled && importer.textureCompression==TextureImporterCompression.Uncompressed && !importer.alphaIsTransparency)continue;
                importer.sRGBTexture=!data;importer.textureCompression=TextureImporterCompression.Uncompressed;importer.mipmapEnabled=false;importer.alphaIsTransparency=false;importer.filterMode=FilterMode.Bilinear;importer.SaveAndReimport();changed++;
            }
            Debug.Log("[Blitz] Uprising original texture imports normalized: "+changed);
        }
    }
}
