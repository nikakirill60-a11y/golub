using BlitzLegacy.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    public sealed class BlitzAssetPostprocessorTests
    {
        private const string Albedo = "Assets/Blitz/Resources/Models/Tanks/T/Textures/T.png";
        private const string Tracks = "Assets/Blitz/Resources/Models/Tanks/T/Textures/T_tracks.png";
        private const string AudioRoot = "Assets/Blitz/Resources/Audio";

        /// <summary>
        /// Requires the import of a texture decoded from the 11.20 client: the
        /// exporters' tag, and the Android format that the block on it calls for.
        /// </summary>
        public static void RequireSourceAndroidFormat(TextureImporter importer)
        {
            var path = importer.assetPath;
            Assert.IsTrue(BlitzAssetPostprocessor.IsSource1120(importer.userData), path + ": " + importer.userData);
            var android = importer.GetPlatformTextureSettings("Android");
            Assert.IsTrue(android.overridden, path);
            Assert.AreEqual(
                BlitzAssetPostprocessor.Source1120AndroidFormat(importer.userData, path), android.format, path);
        }

        [TestCase("4x4", TextureImporterFormat.ASTC_4x4)]
        [TestCase("5x4", TextureImporterFormat.ASTC_4x4)]
        [TestCase("5x5", TextureImporterFormat.ASTC_5x5)]
        [TestCase("6x5", TextureImporterFormat.ASTC_5x5)]
        [TestCase("6x6", TextureImporterFormat.ASTC_6x6)]
        [TestCase("8x5", TextureImporterFormat.ASTC_6x6)]
        [TestCase("8x6", TextureImporterFormat.ASTC_6x6)]
        [TestCase("10x5", TextureImporterFormat.ASTC_6x6)]
        [TestCase("10x6", TextureImporterFormat.ASTC_6x6)]
        [TestCase("8x8", TextureImporterFormat.ASTC_8x8)]
        [TestCase("10x8", TextureImporterFormat.ASTC_8x8)]
        [TestCase("10x10", TextureImporterFormat.ASTC_10x10)]
        [TestCase("12x10", TextureImporterFormat.ASTC_10x10)]
        [TestCase("12x12", TextureImporterFormat.ASTC_12x12)]
        public void AnAlbedoGoesBackToAstcAtNoLessThanTheClientRate(string block, TextureImporterFormat expected)
        {
            Assert.AreEqual(expected, BlitzAssetPostprocessor.Source1120AndroidFormat(
                BlitzAssetPostprocessor.Source1120Tag + " astc=" + block, Albedo));
        }

        [TestCase("blitz-source-11.20 astc=6x6", Tracks)]
        [TestCase("blitz-source-11.20", Albedo)]
        [TestCase("blitz-source-11.20 astc=6", Albedo)]
        [TestCase("blitz-source-11.20 astc=3x3", Albedo)]
        [TestCase("blitz-source-11.20 astc=14x12", Albedo)]
        [TestCase("blitz-source-11.20 astc=-6x6", Albedo)]
        public void TracksBareTagsAndUnreadableBlocksStayRgba32(string userData, string path)
        {
            Assert.AreEqual(
                TextureImporterFormat.RGBA32, BlitzAssetPostprocessor.Source1120AndroidFormat(userData, path));
        }

        [TestCase("blitz-source-11.20", true)]
        [TestCase("blitz-source-11.20 astc=6x5", true)]
        [TestCase("", false)]
        [TestCase(null, false)]
        [TestCase("blitz-source-11.2", false)]
        [TestCase("blitz-source-11.20x", false)]
        public void OnlyTheExportersTagMarksAnAssetOfThe1120Client(string userData, bool expected)
        {
            Assert.AreEqual(expected, BlitzAssetPostprocessor.IsSource1120(userData));
        }

        [Test]
        public void TheStemsStreamTheTunesStayVorbisAndTheEffectsLoadAsAdpcm()
        {
            var stems = 0;
            var tunes = 0;
            var effects = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:AudioClip", new[] { AudioRoot }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var importer = (AudioImporter)AssetImporter.GetAtPath(path);
                var settings = importer.defaultSampleSettings;
                Assert.IsFalse(importer.ContainsSampleSettingsOverride("Android"), path);
                Assert.IsFalse(importer.forceToMono, path);
                Assert.AreEqual(AudioSampleRateSetting.PreserveSampleRate, settings.sampleRateSetting, path);
                if (BlitzAssetPostprocessor.IsMusicStem(path))
                {
                    stems++;
                    Assert.AreEqual(AudioClipLoadType.Streaming, settings.loadType, path);
                    Assert.AreEqual(AudioCompressionFormat.Vorbis, settings.compressionFormat, path);
                    Assert.AreEqual(1f, settings.quality, 1e-4f, path);
                    Assert.IsFalse(settings.preloadAudioData, path);
                    Assert.IsTrue(importer.loadInBackground, path);
                    continue;
                }

                Assert.IsTrue(settings.preloadAudioData, path);
                Assert.IsFalse(importer.loadInBackground, path);
                if (BlitzAssetPostprocessor.IsTune(path))
                {
                    tunes++;
                    Assert.AreEqual(AudioClipLoadType.CompressedInMemory, settings.loadType, path);
                    Assert.AreEqual(AudioCompressionFormat.Vorbis, settings.compressionFormat, path);
                    Assert.AreEqual(BlitzAssetPostprocessor.TuneQuality, settings.quality, 1e-4f, path);
                }
                else
                {
                    effects++;
                    Assert.AreEqual(AudioClipLoadType.DecompressOnLoad, settings.loadType, path);
                    Assert.AreEqual(AudioCompressionFormat.ADPCM, settings.compressionFormat, path);
                }
            }

            // The three login stems; the two map intros and three battle results.
            Assert.AreEqual(3, stems);
            Assert.AreEqual(5, tunes);
            Assert.Greater(effects, 0);
        }
    }
}
