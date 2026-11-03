using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace BlitzLegacy.EditorTools
{
    /// <summary>Keep original 11.20 texels, including non-power-of-two particle sheets.</summary>
    public sealed class AtgmTextureImport : AssetPostprocessor
    {
        private const string Root = "Assets/Blitz/Resources/Atgm1120/Textures/";
        private static void Configure(TextureImporter importer, string path)
        {
            var marker = path == Root + "aim_rocket_marker.png";
            importer.textureType = marker ? TextureImporterType.Sprite : TextureImporterType.Default;
            if (marker)
            {
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.spritePixelsPerUnit = 100f;
                var settings = new TextureImporterSettings();
                importer.ReadTextureSettings(settings);
                settings.spriteMeshType = SpriteMeshType.FullRect;
                importer.SetTextureSettings(settings);
            }
            importer.sRGBTexture = true;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            // Do not dilate transparent source RGB or bleed adjacent frames.
            importer.alphaIsTransparency = false;
            importer.mipmapEnabled = false;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.maxTextureSize = 2048;
            importer.filterMode = FilterMode.Bilinear;
            // Native stripe UVs tile beyond [0,1]; the HUD sprite must not wrap.
            importer.wrapMode = marker ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
            foreach (var platform in new[] { "Standalone", "Android", "iPhone" })
                importer.ClearPlatformTextureSettings(platform);
        }
        private void OnPreprocessTexture()
        {
            if (assetPath.StartsWith(Root, StringComparison.Ordinal))
                Configure((TextureImporter)assetImporter, assetPath);
        }
        public static void Apply()
        {
            foreach (var path in Directory.GetFiles(Root, "*.png"))
            {
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null) continue;
                Configure(importer, path);
                importer.SaveAndReimport();
            }
        }
    }
}
