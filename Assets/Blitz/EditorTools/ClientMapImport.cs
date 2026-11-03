using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace BlitzLegacy.EditorTools
{
    /// <summary>
    /// Import rules for the textures of a binary client map scene (a map
    /// folder that holds <c>*_scene_layout.json</c>). The client shaders do
    /// their lighting on the raw gamma values exactly as the DAVA shaders did
    /// and only convert the result, so nothing here may be decoded as sRGB.
    /// Runs after <see cref="BlitzAssetPostprocessor"/>, whose OBJ-map rules
    /// would otherwise claim the same Textures folder.
    /// </summary>
    public sealed class ClientMapImport : AssetPostprocessor
    {
        private const string MapsRoot = "Assets/Blitz/Resources/Maps/";
        private const string TextureFolder = "/Textures/";
        private const string LayoutSuffix = "_scene_layout.json";
        private const string CubeSuffix = "_cube.png";

        // Tools/port_godot_map_scene.py names a Godot scene's textures after
        // the sampler that reads them: source_color decodes sRGB, a raw
        // sampler does not, hterrain's splat and normal maps have no
        // mipmaps, and the LightmapGI slices are half-float EXRs.
        private const string GodotSrgbMark = "_srgb";
        private const string GodotRawMark = "_raw";
        private const string GodotNoMipMark = "_nomip";
        // compress/mode lossless or lossy in the Godot .import: the texture
        // never reaches VRAM compressed, so DXT/ASTC would add 4x4 blocks
        // Godot does not show (Rudniki's ground tile texture).
        private const string GodotLosslessMark = "_lossless_";
        private const string LightmapSuffix = "_hdr.exr";

        public override uint GetVersion()
        {
            return 3;
        }

        public override int GetPostprocessOrder()
        {
            return 100;
        }

        /// <summary>Whether a path is a scene texture of a client-format map.</summary>
        public static bool IsClientSceneTexture(string path)
        {
            if (!path.StartsWith(MapsRoot, StringComparison.Ordinal) ||
                !(path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                  path.EndsWith(LightmapSuffix, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            var folder = Path.GetDirectoryName(path);
            if (folder == null ||
                !(folder.Replace('\\', '/') + "/").EndsWith(TextureFolder, StringComparison.Ordinal))
            {
                return false;
            }

            var mapFolder = Path.GetDirectoryName(folder);
            return mapFolder != null && Directory.Exists(mapFolder) &&
                Directory.GetFiles(mapFolder, "*" + LayoutSuffix).Length > 0;
        }

        private void OnPreprocessTexture()
        {
            if (!IsClientSceneTexture(assetPath))
            {
                return;
            }

            var importer = (TextureImporter)assetImporter;
            var isCube = assetPath.EndsWith(CubeSuffix, StringComparison.OrdinalIgnoreCase);
            importer.textureType = TextureImporterType.Default;
            importer.textureShape = isCube
                ? TextureImporterShape.TextureCube
                : TextureImporterShape.Texture2D;
            if (isCube)
            {
                // The exporter lays the faces out as a horizontal 6x1 strip
                // in Unity's +X,-X,+Y,-Y,+Z,-Z order.
                importer.generateCubemap = TextureImporterGenerateCubemap.AutoCubemap;
            }

            importer.sRGBTexture = false;
            importer.mipmapEnabled = true;
            importer.wrapMode = isCube ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
            importer.filterMode = FilterMode.Trilinear;
            importer.anisoLevel = isCube ? 1 : 8;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = false;
            importer.isReadable = false;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.maxTextureSize = 2048;
            importer.textureCompression = TextureImporterCompression.CompressedHQ;

            // The source shipped ETC/ASTC already; 4x4 avoids a second
            // visible generation loss on the alpha-tested foliage edges.
            var android = importer.GetPlatformTextureSettings("Android");
            android.overridden = true;
            android.maxTextureSize = 2048;
            android.format = TextureImporterFormat.ASTC_4x4;
            android.compressionQuality = 100;
            importer.SetPlatformTextureSettings(android);

            ApplyGodotRules(importer);
        }

        /// <summary>Whether a scene texture was written for a Godot scene.</summary>
        public static bool IsGodotSceneTexture(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            return path.EndsWith(LightmapSuffix, StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(GodotSrgbMark, StringComparison.Ordinal) ||
                name.EndsWith(GodotRawMark, StringComparison.Ordinal) ||
                name.EndsWith(GodotRawMark + GodotNoMipMark, StringComparison.Ordinal);
        }

        private void ApplyGodotRules(TextureImporter importer)
        {
            if (!IsGodotSceneTexture(assetPath))
            {
                return;
            }

            var name = Path.GetFileNameWithoutExtension(assetPath);
            // filter_linear_mipmap without the _anisotropic variant: Godot
            // samples these trilinear, never anisotropic.
            importer.anisoLevel = 1;
            importer.sRGBTexture = name.EndsWith(GodotSrgbMark, StringComparison.Ordinal);
            if (assetPath.EndsWith(LightmapSuffix, StringComparison.OrdinalIgnoreCase))
            {
                // Godot samples layer 0 of the bake bilinearly (the bicubic
                // runs in the shader) and values reach about 1.9.
                importer.mipmapEnabled = false;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.filterMode = FilterMode.Bilinear;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                SetFormat(importer, "Standalone", TextureImporterFormat.RGBAHalf);
                SetFormat(importer, "Android", TextureImporterFormat.RGBAHalf);
                return;
            }

            if (name.Contains(GodotLosslessMark))
            {
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                SetFormat(importer, "Android", TextureImporterFormat.RGBA32);
            }

            if (name.EndsWith(GodotNoMipMark, StringComparison.Ordinal))
            {
                // hterrain splat and normal maps: lossless, one level, read
                // by the vertex stage and the per-pixel blend alike.
                importer.mipmapEnabled = false;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.filterMode = FilterMode.Bilinear;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                SetFormat(importer, "Android", TextureImporterFormat.RGBA32);
            }
        }

        private static void SetFormat(TextureImporter importer, string platform, TextureImporterFormat format)
        {
            var settings = importer.GetPlatformTextureSettings(platform);
            settings.overridden = true;
            settings.maxTextureSize = 2048;
            settings.format = format;
            importer.SetPlatformTextureSettings(settings);
        }
    }
}
