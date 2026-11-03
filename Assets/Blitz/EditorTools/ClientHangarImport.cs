using UnityEditor;

namespace BlitzLegacy.EditorTools
{
    /// <summary>Source pixels are already baked/sprite-packed. Never apply the
    /// Godot hangar's importer rules to these new, independently sourced sheets.</summary>
    public sealed class ClientHangarImport : AssetPostprocessor
    {
        private void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith("Assets/Blitz/Resources/Hangars480/")) return;
            var importer = (TextureImporter)assetImporter;
            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = true;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.maxTextureSize = 4096;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = assetPath.Contains("/Particles/");
            importer.mipmapEnabled = !assetPath.Contains("/Particles/");
            importer.wrapMode = assetPath.Contains("/Particles/")
                ? UnityEngine.TextureWrapMode.Clamp : UnityEngine.TextureWrapMode.Repeat;
            importer.filterMode = UnityEngine.FilterMode.Bilinear;
            importer.npotScale = TextureImporterNPOTScale.None;
        }
    }
}
