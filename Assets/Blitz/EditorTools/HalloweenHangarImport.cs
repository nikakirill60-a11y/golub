using System;
using System.Collections.Generic;
using System.IO;
using BlitzLegacy.Runtime.Hangar.Halloween;
using UnityEditor;
using UnityEngine;

namespace BlitzLegacy.EditorTools
{
    /// <summary>
    /// Import rules for the ported 11.x hangars (<see cref="PortedHangar"/>),
    /// which their exporters write to <c>Resources/&lt;Root&gt;</c>. Every
    /// texture is sampled the way the client's .tex descriptor asks (colour
    /// space, mips, filter, address modes, anisotropy) and compressed no
    /// coarser than the client's own block; the exporter records those
    /// settings per file in the export's import.json, so the importer depends
    /// on it. The ambience clips keep their sample rate.
    /// </summary>
    public sealed class HalloweenHangarImport : AssetPostprocessor
    {
        /// <summary>Project folder of an export, with the trailing slash.</summary>
        public static string RootOf(PortedHangar export)
        {
            return "Assets/Blitz/Resources/" + export.Root + "/";
        }

        public static string SpecPathOf(PortedHangar export)
        {
            return RootOf(export) + "import.json";
        }

        /// <summary>The export <paramref name="path"/> belongs to, or null.</summary>
        public static PortedHangar ExportOf(string path)
        {
            foreach (var export in PortedHangar.All)
            {
                if (path.StartsWith(RootOf(export), StringComparison.Ordinal)) return export;
            }
            return null;
        }

        // Clips longer than this stay Vorbis in memory; shorter ones decode on
        // load so the scatterers start them without a decoder spin-up.
        public const double DecompressOnLoadSeconds = 5d;

        public override uint GetVersion()
        {
            return 1;
        }

        // Nothing else claims this folder; run after the generic rules anyway.
        public override int GetPostprocessOrder()
        {
            return 110;
        }

        public static bool IsHangarAsset(string path)
        {
            return ExportOf(path) != null;
        }

        /// <summary>Import settings of one hangar texture, as import.json writes them.</summary>
        public sealed class TextureSpec
        {
            public bool Srgb;
            public bool Mips;
            public FilterMode Filter;
            public TextureWrapMode WrapU;
            public TextureWrapMode WrapV;
            public int Aniso;
            public int MaxSize;
            public TextureImporterFormat Android;
        }

        private static readonly Dictionary<string, KeyValuePair<DateTime, Dictionary<string, TextureSpec>>> _specs =
            new Dictionary<string, KeyValuePair<DateTime, Dictionary<string, TextureSpec>>>(StringComparer.Ordinal);

        /// <summary>The spec of a texture by its path under the export's root, or null.</summary>
        public static TextureSpec Spec(PortedHangar export, string relativePath)
        {
            var specPath = SpecPathOf(export);
            var stamp = File.Exists(specPath) ? File.GetLastWriteTimeUtc(specPath) : DateTime.MinValue;
            KeyValuePair<DateTime, Dictionary<string, TextureSpec>> cached;
            if (!_specs.TryGetValue(specPath, out cached) || cached.Key != stamp)
            {
                cached = new KeyValuePair<DateTime, Dictionary<string, TextureSpec>>(stamp,
                    File.Exists(specPath) ? ParseSpecs(File.ReadAllText(specPath)) : new Dictionary<string, TextureSpec>());
                _specs[specPath] = cached;
            }

            TextureSpec spec;
            return cached.Value.TryGetValue(relativePath, out spec) ? spec : null;
        }

        public static Dictionary<string, TextureSpec> ParseSpecs(string json)
        {
            var images = HalloweenJson.Parse(json).Req("images");
            var specs = new Dictionary<string, TextureSpec>(StringComparer.Ordinal);
            foreach (var key in images.Keys)
            {
                var image = images[key];
                specs[key] = new TextureSpec
                {
                    Srgb = image.Req("srgb").B(),
                    Mips = image.Req("mips").B(),
                    Filter = Parse<FilterMode>(image.Req("filter").S(), key),
                    WrapU = Parse<TextureWrapMode>(image.Req("wrapU").S(), key),
                    WrapV = Parse<TextureWrapMode>(image.Req("wrapV").S(), key),
                    Aniso = Math.Max(1, image.Req("aniso").I()),
                    MaxSize = image.Req("maxSize").I(),
                    Android = Parse<TextureImporterFormat>("ASTC_" + image.Req("astc").S(), key),
                };
            }
            return specs;
        }

        private static T Parse<T>(string value, string key) where T : struct
        {
            T parsed;
            if (!Enum.TryParse(value, false, out parsed))
            {
                throw new InvalidOperationException(
                    "import.json: " + key + " names " + value + ", which is no " + typeof(T).Name);
            }
            return parsed;
        }

        private void OnPreprocessTexture()
        {
            var export = ExportOf(assetPath);
            if (export == null) return;
            var specPath = SpecPathOf(export);
            context.DependsOnSourceAsset(specPath);
            var spec = Spec(export, assetPath.Substring(RootOf(export).Length));
            if (spec == null)
            {
                context.LogImportError(
                    export.Label + " hangar texture " + assetPath + " is not listed in " + specPath +
                    "; rerun " + export.Exporter);
                return;
            }

            var importer = (TextureImporter)assetImporter;
            importer.textureType = TextureImporterType.Default;
            importer.textureShape = TextureImporterShape.Texture2D;
            importer.sRGBTexture = spec.Srgb;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = false;
            importer.mipmapEnabled = spec.Mips;
            importer.streamingMipmaps = false;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.isReadable = false;
            importer.wrapModeU = spec.WrapU;
            importer.wrapModeV = spec.WrapV;
            importer.filterMode = spec.Filter;
            importer.anisoLevel = spec.Aniso;
            importer.maxTextureSize = spec.MaxSize;
            importer.textureCompression = TextureImporterCompression.Compressed;
            Platform(importer, "Android", spec.Android, spec.MaxSize);
            Platform(importer, "Standalone", TextureImporterFormat.BC7, spec.MaxSize);
        }

        private static void Platform(
            TextureImporter importer, string platform, TextureImporterFormat format, int maxSize)
        {
            var settings = importer.GetPlatformTextureSettings(platform);
            settings.overridden = true;
            settings.format = format;
            settings.maxTextureSize = maxSize;
            settings.compressionQuality = (int)TextureCompressionQuality.Best;
            importer.SetPlatformTextureSettings(settings);
        }

        private void OnPreprocessAudio()
        {
            if (!IsHangarAsset(assetPath)) return;
            var importer = (AudioImporter)assetImporter;
            importer.forceToMono = false;
            importer.ambisonic = false;
            importer.loadInBackground = false;

            var seconds = OggSeconds(File.ReadAllBytes(assetPath));
            var settings = importer.defaultSampleSettings;
            settings.sampleRateSetting = AudioSampleRateSetting.PreserveSampleRate;
            settings.compressionFormat = AudioCompressionFormat.Vorbis;
            settings.quality = 0.85f;
            settings.loadType = seconds >= 0d && seconds <= DecompressOnLoadSeconds
                ? AudioClipLoadType.DecompressOnLoad
                : AudioClipLoadType.CompressedInMemory;
            settings.preloadAudioData = true;
            importer.defaultSampleSettings = settings;
        }

        /// <summary>
        /// Length of an Ogg Vorbis file in seconds without decoding it: the
        /// granule position of the last page over the identification header's
        /// sample rate. Negative when the file is no Ogg Vorbis stream.
        /// </summary>
        public static double OggSeconds(byte[] bytes)
        {
            const int PageHeader = 27;
            if (bytes == null || bytes.Length < PageHeader || !IsPage(bytes, 0)) return -1d;
            var packet = PageHeader + bytes[26];
            if (bytes.Length < packet + 16 || bytes[packet] != 1 ||
                System.Text.Encoding.ASCII.GetString(bytes, packet + 1, 6) != "vorbis")
            {
                return -1d;
            }

            var rate = BitConverter.ToInt32(bytes, packet + 12);
            for (var i = bytes.Length - PageHeader; i > 0; i--)
            {
                if (!IsPage(bytes, i)) continue;
                var granule = BitConverter.ToInt64(bytes, i + 6);
                return rate > 0 && granule >= 0 ? granule / (double)rate : -1d;
            }
            return -1d;
        }

        private static bool IsPage(byte[] bytes, int at)
        {
            return bytes[at] == (byte)'O' && bytes[at + 1] == (byte)'g' &&
                bytes[at + 2] == (byte)'g' && bytes[at + 3] == (byte)'S';
        }
    }
}
