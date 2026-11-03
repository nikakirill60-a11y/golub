using System;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// project.godot's <c>config/icon</c>, ported by Tools/port_godot_icon.py
    /// and wired into the player settings by EditorTools/AppIconSetup.cs.
    ///
    /// Godot needs nothing else: its Android export scales that one image for
    /// every launcher density. Unity stores a texture per icon slot instead, so
    /// an unassigned slot silently ships the engine placeholder - which is what
    /// these tests exist to catch, against
    /// Resources/Data/app_icon_source_manifest.json rather than against numbers
    /// repeated here.
    /// </summary>
    public sealed class AppIconTests
    {
        [Serializable]
        private sealed class Entry
        {
            public string path;
            public int[] size;
        }

        [Serializable]
        private sealed class Outputs
        {
            public Entry legacy;
            public Entry round;
            public Entry adaptive_foreground;
            public Entry adaptive_background;
        }

        [Serializable]
        private sealed class Manifest
        {
            public string source_file;
            public int[] source_size;
            public Outputs outputs;
        }

        private static Manifest Load()
        {
            var text = Resources.Load<TextAsset>("Data/app_icon_source_manifest");
            Assert.IsNotNull(text, "run Tools/port_godot_icon.py");

            var manifest = JsonUtility.FromJson<Manifest>(text.text);
            Assert.IsNotNull(manifest.outputs, "the icon manifest carries no outputs");
            return manifest;
        }

        private static Texture2D Asset(Entry entry)
        {
            Assert.IsNotNull(entry, "the icon manifest is missing an output");

            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(entry.path);
            Assert.IsNotNull(texture, entry.path + " is not in the project");
            return texture;
        }

        [Test]
        public void PortedBitmaps_HaveTheSizesTheManifestRecords()
        {
            var manifest = Load();
            Assert.AreEqual(
                new[] { 256, 256 }, manifest.source_size,
                "the Godot project icon changed size; re-run the port");

            foreach (var entry in new[]
            {
                manifest.outputs.legacy,
                manifest.outputs.round,
                manifest.outputs.adaptive_foreground,
                manifest.outputs.adaptive_background,
            })
            {
                var texture = Asset(entry);
                Assert.AreEqual(
                    new[] { entry.size[0], entry.size[1] },
                    new[] { texture.width, texture.height },
                    entry.path + " was imported at another size");
            }
        }

        [Test]
        public void EveryAndroidIconSlot_HoldsThePortedBitmaps()
        {
            var manifest = Load();
            var legacy = Asset(manifest.outputs.legacy);
            var foreground = Asset(manifest.outputs.adaptive_foreground);
            var background = Asset(manifest.outputs.adaptive_background);

            var kinds = PlayerSettings.GetSupportedIconKinds(NamedBuildTarget.Android);
            Assert.IsNotNull(kinds);
            Assert.Greater(kinds.Length, 0, "this editor exposes no android icon kinds");

            foreach (var kind in kinds)
            {
                var icons = PlayerSettings.GetPlatformIcons(NamedBuildTarget.Android, kind);
                Assert.IsNotNull(icons, "no android icon slots for " + kind);
                Assert.Greater(icons.Length, 0, "no android icon slots for " + kind);

                foreach (var icon in icons)
                {
                    // Layered slots are background first, then foreground;
                    // Godot has no round variant, so a single-layer slot is
                    // expected to hold the plain project icon.
                    // Layers past the foreground (monochrome) stay empty, as
                    // Godot's preset leaves adaptive_monochrome unset.
                    var expected = icon.maxLayerCount >= 2
                        ? new Texture2D[Math.Max(2, icon.minLayerCount)]
                        : new[] { legacy };
                    if (icon.maxLayerCount >= 2)
                    {
                        expected[0] = background;
                        expected[1] = foreground;
                    }

                    for (var layer = 0; layer < expected.Length; layer++)
                    {
                        Assert.AreSame(
                            expected[layer], icon.GetTexture(layer),
                            kind + " icon " + icon.width + "px layer " + layer
                            + " is not the ported bitmap; run Tools/unity.sh icon");
                    }
                }
            }
        }
    }
}
