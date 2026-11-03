using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace BlitzLegacy.EditorTools
{
    /// <summary>
    /// Assigns the Android launcher icon ported from the Godot project.
    ///
    /// Godot only needs project.godot's <c>config/icon</c>: with every
    /// <c>launcher_icons/*</c> entry of the Android preset left empty, the
    /// export scales that one image into each density itself. Unity instead
    /// keeps a texture per icon slot in the project settings, so the bitmaps
    /// written by Tools/port_godot_icon.py are wired up here and every Android
    /// build runs this first rather than shipping Unity's placeholder icon.
    ///
    /// The kinds are taken from the editor rather than named: Unity 6 keeps
    /// only the adaptive kind for Android, with legacy and round obsolete, and
    /// an adaptive slot wants two layers where the older ones want one.
    /// </summary>
    public static class AppIconSetup
    {
        public const string LegacyIcon = "Assets/Blitz/AppIcon/app_icon.png";

        public const string AdaptiveForeground =
            "Assets/Blitz/AppIcon/app_icon_adaptive_foreground.png";

        public const string AdaptiveBackground =
            "Assets/Blitz/AppIcon/app_icon_adaptive_background.png";

        /// <summary>Tools/unity.sh icon.</summary>
        public static void Run()
        {
            if (!Ensure())
            {
                EditorApplication.Exit(1);
                return;
            }

            AssetDatabase.SaveAssets();
        }

        /// <summary>
        /// Points every Android icon slot at the ported bitmaps. False means
        /// the port has not been run, which is worth stopping a build for: an
        /// alpha APK with Unity's placeholder icon is not this game.
        /// </summary>
        public static bool Ensure()
        {
            var legacy = Load(LegacyIcon);
            var foreground = Load(AdaptiveForeground);
            var background = Load(AdaptiveBackground);

            if (legacy == null || foreground == null || background == null)
            {
                Debug.LogError(
                    "[Blitz] app icon assets are missing; run Tools/port_godot_icon.py");
                return false;
            }

            var kinds = PlayerSettings.GetSupportedIconKinds(NamedBuildTarget.Android);
            if (kinds == null || kinds.Length == 0)
            {
                Debug.LogError("[Blitz] this editor exposes no android icon kinds");
                return false;
            }

            foreach (var kind in kinds)
            {
                var icons = PlayerSettings.GetPlatformIcons(NamedBuildTarget.Android, kind);
                if (icons == null || icons.Length == 0)
                {
                    Debug.LogError("[Blitz] no android icon slots for kind " + kind);
                    return false;
                }

                foreach (var icon in icons)
                {
                    // An adaptive slot is layered: background first, then the
                    // foreground that the launcher mask animates. A one-layer
                    // slot takes the plain icon, which is also all Godot has -
                    // there is no round variant in the source to port.
                    icon.SetTextures(Layers(icon, legacy, foreground, background));
                }

                PlayerSettings.SetPlatformIcons(NamedBuildTarget.Android, kind, icons);
                Debug.Log("[Blitz] android app icon: " + kind + ", " + icons.Length
                    + " slot(s) from " + LegacyIcon);
            }

            return true;
        }

        /// <summary>
        /// The textures for one slot. Newer editors add slots that insist on a
        /// third layer (the monochrome themed-icon layer); Godot's preset leaves
        /// <c>adaptive_monochrome</c> empty, so every layer past the foreground
        /// stays unassigned and the export ships no monochrome drawable, as
        /// Godot's does.
        /// </summary>
        private static Texture2D[] Layers(
            PlatformIcon icon, Texture2D legacy, Texture2D foreground, Texture2D background)
        {
            if (icon.maxLayerCount < 2)
            {
                return new[] { legacy };
            }

            var layers = new Texture2D[Mathf.Max(2, icon.minLayerCount)];
            layers[0] = background;
            layers[1] = foreground;
            return layers;
        }

        /// <summary>
        /// Launcher icons are resized by the build, not sampled by a shader, so
        /// they are kept uncompressed and mip-free: a crunched icon shows its
        /// blocks at the densities Unity downscales for.
        /// </summary>
        private static Texture2D Load(string path)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null
                && (importer.textureCompression != TextureImporterCompression.Uncompressed
                    || importer.mipmapEnabled
                    || !importer.alphaIsTransparency
                    || importer.npotScale != TextureImporterNPOTScale.None))
            {
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.mipmapEnabled = false;
                importer.alphaIsTransparency = true;
                // The adaptive layers are 432x432; the default NPOT rescale would
                // stretch them to 512 and move the icon out of its safe zone.
                importer.npotScale = TextureImporterNPOTScale.None;
                importer.SaveAndReimport();
            }

            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }
    }
}
