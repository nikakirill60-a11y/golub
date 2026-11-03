using System;
using UnityEditor;
using UnityEngine;

namespace BlitzLegacy.EditorTools
{
    /// <summary>
    /// Import rules of the textures Tools/port_blitz_camouflage.py writes.
    /// A mask is coverage rather than colour: one linear channel the tank
    /// shader scales the pattern by, sampled with the albedo's mesh UVs. The
    /// patterns keep their gamma encoding, because the DAVA decal blend mixes
    /// gamma texels and TankCamouflageLit reproduces that mix.
    /// </summary>
    public sealed class CamouflageTextureImport : AssetPostprocessor
    {
        public const string MaskRoot = "Assets/Blitz/Resources/Camouflages/Masks/";
        public const string PatternRoot = "Assets/Blitz/Resources/Camouflages/Textures/";

        /// <summary>Raise when a rule below changes, so existing imports are redone.</summary>
        public override uint GetVersion()
        {
            return 1;
        }

        private void OnPreprocessTexture()
        {
            if (assetPath.StartsWith(MaskRoot, StringComparison.Ordinal))
            {
                ConfigureMask((TextureImporter)assetImporter);
            }
            else if (assetPath.StartsWith(PatternRoot, StringComparison.Ordinal))
            {
                ConfigurePattern((TextureImporter)assetImporter);
            }
        }

        private static void ConfigureMask(TextureImporter importer)
        {
            importer.textureType = TextureImporterType.SingleChannel;
            var settings = new TextureImporterSettings();
            importer.ReadTextureSettings(settings);
            settings.singleChannelComponent = TextureImporterSingleChannelComponent.Red;
            importer.SetTextureSettings(settings);
            importer.sRGBTexture = false;
            importer.alphaSource = TextureImporterAlphaSource.None;
            importer.alphaIsTransparency = false;
            importer.mipmapEnabled = true;
            // The albedo it rides on repeats, so the mask has to as well.
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.filterMode = FilterMode.Trilinear;
            importer.anisoLevel = 4;
            importer.isReadable = false;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.maxTextureSize = 1024;
            importer.textureCompression = TextureImporterCompression.CompressedHQ;

            var standalone = importer.GetPlatformTextureSettings("Standalone");
            standalone.overridden = true;
            standalone.maxTextureSize = 1024;
            standalone.format = TextureImporterFormat.BC4;
            importer.SetPlatformTextureSettings(standalone);

            // Most masks are 256 px: ASTC 6x6, like the tank albedo, keeps
            // the painted/bare borders of a 256 px sheet clean.
            var android = importer.GetPlatformTextureSettings("Android");
            android.overridden = true;
            android.maxTextureSize = 1024;
            android.format = TextureImporterFormat.ASTC_6x6;
            android.compressionQuality = 100;
            importer.SetPlatformTextureSettings(android);
        }

        private static void ConfigurePattern(TextureImporter importer)
        {
            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = false;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = false;
            importer.mipmapEnabled = true;
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.filterMode = FilterMode.Trilinear;
            importer.anisoLevel = 4;
            importer.isReadable = false;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.maxTextureSize = 1024;
            // Two 256 px tiles: not worth a lossy encoding of the pattern.
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            foreach (var platform in new[] { "Standalone", "Android", "iPhone" })
            {
                importer.ClearPlatformTextureSettings(platform);
            }
        }
    }
}
