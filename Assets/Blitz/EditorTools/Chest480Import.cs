using UnityEditor;
using UnityEngine;

namespace BlitzLegacy.EditorTools
{
    /// <summary>
    /// Import rules for the ported 4.8.0 container-opening scene
    /// (Tools/port_blitz_480_chest.py). DAVA sampled these textures as raw
    /// bytes and blended in gamma space, so nothing is decoded from sRGB and
    /// nothing is compressed: the chest renderer reproduces DAVA's math on the
    /// exact source values. Scene textures repeat with trilinear mips (the
    /// .tex descriptors ask for wrap REPEAT, linear min/mag and linear mips);
    /// particle sprites are single atlas rectangles, clamped without mips.
    /// </summary>
    public sealed class Chest480Import : AssetPostprocessor
    {
        public const string Root = "Assets/Blitz/Resources/Chest480/";

        private void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(Root)) return;
            var importer = (TextureImporter)assetImporter;
            var sprite = assetPath.StartsWith(Root + "Sprites/");
            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = false;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.maxTextureSize = 4096;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = false;
            importer.isReadable = false;
            importer.mipmapEnabled = !sprite;
            importer.wrapMode = sprite ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
            importer.filterMode = sprite ? FilterMode.Bilinear : FilterMode.Trilinear;
        }
    }
}
