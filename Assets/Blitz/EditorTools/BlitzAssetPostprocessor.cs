using System;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace BlitzLegacy.EditorTools
{
    /// <summary>
    /// Deterministic import settings for source-backed Godot assets. Reimporting
    /// the project must never depend on somebody clicking through an Inspector.
    /// </summary>
    public sealed class BlitzAssetPostprocessor : AssetPostprocessor
    {
        private const string TextureRoot = "Assets/Blitz/Resources/Textures/";
        private const string ModelRoot = "Assets/Blitz/Resources/Models/";
        /// <summary>Every ported map lives under this folder.</summary>
        private const string MapsRoot = "Assets/Blitz/Resources/Maps/";

        private const string MapRoot = MapsRoot + "Rudniki/";

        /// <summary>
        /// A client .sc2 map is exported as one OBJ beside a flat Textures
        /// folder, so its material textures are recognised by that folder
        /// rather than by the Godot Objects/ layout.
        /// </summary>
        private const string MapSceneTextureFolder = "/Textures/";

        /// <summary>Godot maps keep their prop artwork in this folder.</summary>
        private const string MapObjectTextureFolder = "/Objects/";

        /// <summary>
        /// The baked lightmap atlases. Godot stores each scene's atlas as one
        /// 2D texture array and lays the slices out in the source import grid.
        /// </summary>
        private const string HangarLightmapRoot = ModelRoot + "Hangar/Lightmaps/";

        private const string MapLightmapRoot = MapRoot + "Lightmaps/";

        /// <summary>
        /// Tank models are dissected at runtime: BattleTank mirrors the hull,
        /// cuts the armour plates out of the armour model and hands both to
        /// MeshColliders. Only these stay readable in a player build.
        /// </summary>
        private const string TankModelRoot = ModelRoot + "Tanks/";

        private const string AudioRoot = "Assets/Blitz/Resources/Audio/";

        /// <summary>
        /// Sprite sheets of the ported Godot particle scenes. Alpha is visual
        /// coverage here, and every sheet is sampled frame by frame, so a mip
        /// chain would bleed neighbouring frames into each other.
        /// </summary>
        private const string ParticleRoot = "Assets/Blitz/Resources/Particles/";

        /// <summary>The three layered login stems, which stream instead of loading.</summary>
        private static readonly string[] MusicClips =
        {
            "harmony",
            "ambient_wo_explosions",
            "drums",
        };

        /// <summary>
        /// Vorbis quality of the tunes, the map intros and the battle results:
        /// the Halloween hangar's, which comes to about 110 kbps for these
        /// 160 kbps MP3s.
        /// </summary>
        public const float TuneQuality = 0.85f;

        /// <summary>The tunes are the clips under the audio root that come as MP3.</summary>
        private const string TuneExtension = ".mp3";

        /// <summary>
        /// The tag the 11.20 exporters put on the models and the textures they
        /// decode from the client. A texture's tag goes on with the ASTC block
        /// the client shipped it in, as in "blitz-source-11.20 astc=6x5"; a
        /// bare tag on a texture means the client did not ship it as ASTC.
        /// </summary>
        public const string Source1120Tag = "blitz-source-11.20";

        private const string SourceBlockPrefix = Source1120Tag + " astc=";

        /// <summary>The 11.20 track sheets, whose alpha the track shader cuts at a threshold.</summary>
        private const string TrackTextureSuffix = "_tracks.png";

        /// <summary>
        /// Raised whenever the settings below change. Unity trusts the meta
        /// files an earlier revision wrote until this version moves, so
        /// without it a changed rule would only reach newly added assets. A
        /// change that rewrites the meta files of every asset it reaches can
        /// leave it: those reimport on their own, and the thousands of others
        /// are spared a reimport.
        /// </summary>
        public override uint GetVersion()
        {
            return 6;
        }

        /// <summary>Whether <paramref name="userData"/> is the 11.20 exporters' tag, with a block or without.</summary>
        public static bool IsSource1120(string userData)
        {
            return userData == Source1120Tag ||
                (userData != null && userData.StartsWith(SourceBlockPrefix, StringComparison.Ordinal));
        }

        /// <summary>
        /// The Android format of a texture decoded from the 11.20 client: ASTC
        /// again, at no less than the client's rate. Every ASTC block holds
        /// 128 bits, as much as RGBA32 spends on four texels, so a 6x6 block
        /// takes a ninth of the room the decoded pixels would. The coarsest
        /// square block Unity has that covers no more texels than the
        /// client's keeps the rate: 6x5 goes to 5x5, 8x6 and 10x5 to 6x6. The
        /// client encoded these pixels at that rate once, so the second pass
        /// loses little. Track sheets, whose cut-out alpha a second pass would
        /// fray, and textures the client did not ship as ASTC stay RGBA32.
        /// </summary>
        public static TextureImporterFormat Source1120AndroidFormat(string userData, string path)
        {
            int width;
            int height;
            if (path.EndsWith(TrackTextureSuffix, StringComparison.Ordinal) ||
                !TrySourceBlock(userData, out width, out height))
            {
                return TextureImporterFormat.RGBA32;
            }

            return SquareAstc(width * height);
        }

        /// <summary>Reads the client's ASTC block, 4 to 12 texels a side, from a texture's tag.</summary>
        public static bool TrySourceBlock(string userData, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (userData == null || !userData.StartsWith(SourceBlockPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            var block = userData.Substring(SourceBlockPrefix.Length).Split('x');
            return block.Length == 2 &&
                int.TryParse(block[0], NumberStyles.None, CultureInfo.InvariantCulture, out width) &&
                int.TryParse(block[1], NumberStyles.None, CultureInfo.InvariantCulture, out height) &&
                width >= 4 && width <= 12 && height >= 4 && height <= 12;
        }

        /// <summary>The coarsest square ASTC block that covers no more than <paramref name="texels"/> texels.</summary>
        public static TextureImporterFormat SquareAstc(int texels)
        {
            if (texels >= 12 * 12)
            {
                return TextureImporterFormat.ASTC_12x12;
            }

            if (texels >= 10 * 10)
            {
                return TextureImporterFormat.ASTC_10x10;
            }

            if (texels >= 8 * 8)
            {
                return TextureImporterFormat.ASTC_8x8;
            }

            if (texels >= 6 * 6)
            {
                return TextureImporterFormat.ASTC_6x6;
            }

            return texels >= 5 * 5 ? TextureImporterFormat.ASTC_5x5 : TextureImporterFormat.ASTC_4x4;
        }

        private void OnPreprocessTexture()
        {
            var isUiAtlas = assetPath.StartsWith(TextureRoot, StringComparison.Ordinal);
            var isModelTexture = assetPath.StartsWith(ModelRoot, StringComparison.Ordinal);
            var isTerrainTexture = IsTerrainTexture(assetPath);
            var isVegetationTexture = IsVegetationTexture(assetPath);
            var isMapColourTexture = IsMapColourTexture(assetPath);
            var isLightmapAtlas = IsLightmapAtlas(assetPath);
            var isParticleSheet = assetPath.StartsWith(ParticleRoot, StringComparison.Ordinal)
                || assetPath.StartsWith(TextureRoot + "result_fire", StringComparison.Ordinal);
            if (!isUiAtlas && !isModelTexture && !isTerrainTexture &&
                !isVegetationTexture && !isMapColourTexture && !isLightmapAtlas &&
                !isParticleSheet)
            {
                return;
            }

            var importer = (TextureImporter)assetImporter;
            if (isParticleSheet)
            {
                ApplyParticleSheetSettings(importer);
                if (assetPath.StartsWith(TextureRoot + "result_fire", StringComparison.Ordinal))
                {
                    // Native 18-frame strip is 2304 pixels wide. Never downsample
                    // it to 2048 or introduce compression across frame boundaries.
                    importer.maxTextureSize = 4096;
                    importer.textureCompression = TextureImporterCompression.Uncompressed;
                    var fireAndroid = importer.GetPlatformTextureSettings("Android");
                    fireAndroid.maxTextureSize = 4096;
                    fireAndroid.format = TextureImporterFormat.RGBA32;
                    importer.SetPlatformTextureSettings(fireAndroid);
                }
                return;
            }

            if (isLightmapAtlas)
            {
                ApplyLightmapAtlasSettings(importer);
                return;
            }

            if (isTerrainTexture)
            {
                ApplyTerrainTextureSettings(importer, assetPath);
                return;
            }

            if (isVegetationTexture)
            {
                ApplyVegetationTextureSettings(importer, assetPath);
                return;
            }

            if (isMapColourTexture)
            {
                ApplyMapColourTextureSettings(importer, assetPath);
                return;
            }

            var isSmoke = assetPath.EndsWith("/Smoke.png", StringComparison.Ordinal);

            // Default, not Sprite: UI atlas regions are cut from source rects at
            // runtime, while 3D textures are assigned to runtime URP materials.
            importer.textureType = TextureImporterType.Default;
            importer.mipmapEnabled = isModelTexture && !isSmoke;
            importer.wrapMode = isModelTexture && !isSmoke
                ? TextureWrapMode.Repeat
                : TextureWrapMode.Clamp;
            importer.filterMode = importer.mipmapEnabled
                ? FilterMode.Trilinear
                : FilterMode.Bilinear;
            importer.anisoLevel = importer.mipmapEnabled ? 4 : 1;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;

            // Tank albedo alpha stores material roughness and must not receive
            // Unity's transparent-colour dilation. Only UI/smoke use visual alpha.
            importer.alphaIsTransparency = isUiAtlas || isSmoke;
            importer.isReadable = false;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.maxTextureSize = 2048;
            importer.textureCompression = TextureImporterCompression.CompressedHQ;

            var android = importer.GetPlatformTextureSettings("Android");
            android.overridden = true;
            android.maxTextureSize = 2048;
            android.format = TextureImporterFormat.ASTC_6x6;
            android.compressionQuality = 100;
            if (IsSource1120(importer.userData))
            {
                // These PNGs are decoded from the client's own textures. The
                // editor and the desktop keep those pixels as they are; Android
                // gets the client's rate back, see Source1120AndroidFormat.
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.maxTextureSize = 8192;
                android.maxTextureSize = 8192;
                android.format = Source1120AndroidFormat(importer.userData, assetPath);
            }
            importer.SetPlatformTextureSettings(android);
        }

        private static void ApplyParticleSheetSettings(TextureImporter importer)
        {
            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = true;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.anisoLevel = 1;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = true;
            importer.isReadable = false;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.maxTextureSize = 2048;
            importer.textureCompression = TextureImporterCompression.CompressedHQ;

            var android = importer.GetPlatformTextureSettings("Android");
            android.overridden = true;
            android.maxTextureSize = 2048;
            // The flip-book strips are long and thin; 4x4 keeps a frame edge
            // from leaking into its neighbour.
            android.format = TextureImporterFormat.ASTC_4x4;
            android.compressionQuality = 100;
            importer.SetPlatformTextureSettings(android);
        }

        private static bool IsLightmapAtlas(string path)
        {
            return (path.StartsWith(HangarLightmapRoot, StringComparison.Ordinal) ||
                path.StartsWith(MapLightmapRoot, StringComparison.Ordinal)) &&
                path.EndsWith(".exr", StringComparison.OrdinalIgnoreCase);
        }

        private static void ApplyLightmapAtlasSettings(TextureImporter importer)
        {
            // The strips hold radiance in linear HDR exactly as the source
            // baked it, so nothing may decode them as sRGB. Godot imports them
            // without mipmaps and every instance samples one slice, so a mip
            // chain would only bleed neighbouring slices into each other.
            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = false;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.anisoLevel = 1;
            importer.alphaSource = TextureImporterAlphaSource.None;
            importer.alphaIsTransparency = false;
            importer.isReadable = false;
            importer.npotScale = TextureImporterNPOTScale.None;

            // The map atlas is 4608 px wide for its nine slices. A smaller
            // limit would rescale the strip and shift every baked UV rect.
            importer.maxTextureSize = 8192;
            importer.textureCompression = TextureImporterCompression.Uncompressed;

            // Godot ships the strips VRAM-compressed with HDR compression on,
            // which is ASTC HDR on Android. The editor keeps the uncompressed
            // reference the render comparisons are measured against.
            var android = importer.GetPlatformTextureSettings("Android");
            android.overridden = true;
            android.maxTextureSize = 8192;
            android.format = TextureImporterFormat.ASTC_HDR_6x6;
            android.compressionQuality = 100;
            importer.SetPlatformTextureSettings(android);
        }

        private static bool IsTerrainTexture(string path)
        {
            if (!path.StartsWith(MapsRoot, StringComparison.Ordinal))
            {
                return false;
            }

            var name = Path.GetFileName(path);
            return name == "colormap.png" ||
                name == "tilemask.png" ||
                name == "tiletextures.png";
        }

        private static void ApplyTerrainTextureSettings(
            TextureImporter importer, string path)
        {
            var isTiledDetail = path.EndsWith(
                "/tiletextures.png", StringComparison.Ordinal);

            // The source shader declares all three samplers without
            // source_color and performs its authored gamma-like contrast at
            // the end, so Unity must not silently decode them as sRGB first.
            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = false;

            // hterrain_data.gd declares the colour and splat maps with
            // mipmaps = false, and the source imports both PNGs with
            // mipmaps/generate=false. Only the repeated tile detail is
            // imported with a mip chain, which is where it is needed.
            importer.mipmapEnabled = isTiledDetail;
            importer.wrapMode = isTiledDetail
                ? TextureWrapMode.Repeat
                : TextureWrapMode.Clamp;
            importer.filterMode = isTiledDetail
                ? FilterMode.Trilinear
                : FilterMode.Bilinear;
            importer.anisoLevel = isTiledDetail ? 8 : 1;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = false;
            importer.isReadable = false;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.maxTextureSize = 2048;
            importer.textureCompression = TextureImporterCompression.CompressedHQ;

            // ASTC 4x4 keeps the repeated sand grain intact on Android,
            // where the generic 6x6 model setting visibly softens it. The
            // colour map and the four-channel mask are read as data, and
            // the source ships both lossless, so they stay uncompressed.
            var android = importer.GetPlatformTextureSettings("Android");
            android.overridden = true;
            android.maxTextureSize = 2048;
            android.format = isTiledDetail
                ? TextureImporterFormat.ASTC_4x4
                : TextureImporterFormat.RGBA32;
            android.compressionQuality = 100;
            importer.SetPlatformTextureSettings(android);
        }

        /// <summary>
        /// Foliage sheets of the client scene keep the plant name the artists
        /// authored, which is the only marker the flat export carries.
        /// </summary>
        private static readonly string[] FoliageNames =
        {
            "tree",
            "bush",
            "grass",
            "leaf",
            "trunk",
        };

        private static bool IsVegetationTexture(string path)
        {
            if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (IsMapObjectTexture(path) &&
                path.IndexOf("/Vegetation/", StringComparison.Ordinal) >= 0)
            {
                return true;
            }

            if (!IsMapSceneTexture(path))
            {
                return false;
            }

            var name = Path.GetFileNameWithoutExtension(path);
            for (var i = 0; i < FoliageNames.Length; i++)
            {
                if (name.IndexOf(FoliageNames[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsMapObjectTexture(string path)
        {
            return path.StartsWith(MapsRoot, StringComparison.Ordinal) &&
                path.IndexOf(MapObjectTextureFolder, StringComparison.Ordinal) >= 0;
        }

        private static bool IsMapSceneTexture(string path)
        {
            return path.StartsWith(MapsRoot, StringComparison.Ordinal) &&
                path.IndexOf(MapSceneTextureFolder, StringComparison.Ordinal) >= 0;
        }

        private static bool IsMapColourTexture(string path)
        {
            return path.StartsWith(MapsRoot, StringComparison.Ordinal) &&
                path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
                !IsTerrainTexture(path) && !IsVegetationTexture(path);
        }

        private static void ApplyMapColourTextureSettings(
            TextureImporter importer, string path)
        {
            var isObject = IsMapObjectTexture(path) || IsMapSceneTexture(path);
            var isSky = path.StartsWith(MapRoot + "Sky/", StringComparison.Ordinal) ||
                path.EndsWith("/sky.png", StringComparison.Ordinal);
            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = true;
            importer.mipmapEnabled = isObject || isSky;
            importer.wrapMode = isObject ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
            importer.filterMode = importer.mipmapEnabled
                ? FilterMode.Trilinear
                : FilterMode.Bilinear;
            importer.anisoLevel = isObject ? 4 : 1;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = false;
            importer.isReadable = false;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.maxTextureSize = 2048;
            importer.textureCompression = TextureImporterCompression.CompressedHQ;

            var android = importer.GetPlatformTextureSettings("Android");
            android.overridden = true;
            android.maxTextureSize = 2048;
            android.format = TextureImporterFormat.ASTC_6x6;
            android.compressionQuality = 100;
            importer.SetPlatformTextureSettings(android);
        }

        private static void ApplyVegetationTextureSettings(
            TextureImporter importer, string path)
        {
            const bool cutout = true;

            // Source vegetation samplers are colour textures with mipmaps. Alpha is
            // visual coverage only on the three vegetation sheets; preserving
            // it with ASTC 4x4 avoids dark, crawling leaf edges on Android.
            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = true;
            importer.mipmapEnabled = true;
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.filterMode = FilterMode.Trilinear;
            importer.anisoLevel = 4;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = cutout;
            importer.isReadable = false;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.maxTextureSize = 2048;
            importer.textureCompression = TextureImporterCompression.CompressedHQ;

            var android = importer.GetPlatformTextureSettings("Android");
            android.overridden = true;
            android.maxTextureSize = 2048;
            android.format = cutout
                ? TextureImporterFormat.ASTC_4x4
                : TextureImporterFormat.ASTC_6x6;
            android.compressionQuality = 100;
            importer.SetPlatformTextureSettings(android);
        }

        private void OnPreprocessModel()
        {
            var isMapScene =
                assetPath.StartsWith(MapsRoot, StringComparison.Ordinal) &&
                assetPath.EndsWith(".obj", StringComparison.OrdinalIgnoreCase);
            if (isMapScene)
            {
                ApplyMapSceneModelSettings((ModelImporter)assetImporter);
                return;
            }

            if (!assetPath.StartsWith(ModelRoot, StringComparison.Ordinal) ||
                !assetPath.EndsWith(".obj", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var importer = (ModelImporter)assetImporter;
            importer.globalScale = 1f;
            importer.useFileScale = false;
            importer.meshCompression = importer.userData == Source1120Tag
                ? ModelImporterMeshCompression.Off : ModelImporterMeshCompression.Low;

            // The editor reads any mesh, a player build only reads the ones
            // marked readable. Tank models are cut apart at runtime, so an
            // unreadable import ships a device build with empty armour plates
            // and no hull collider, and the tank drops through the landscape.
            importer.isReadable =
                assetPath.StartsWith(TankModelRoot, StringComparison.Ordinal);
            importer.importBlendShapes = false;
            importer.importAnimation = false;
            importer.importCameras = false;
            importer.importLights = false;
            importer.generateSecondaryUV = false;
            importer.importNormals = ModelImporterNormals.Import;
            importer.importTangents = ModelImporterTangents.CalculateMikk;
            importer.materialImportMode = ModelImporterMaterialImportMode.None;
            importer.optimizeMeshPolygons = true;
            importer.optimizeMeshVertices = true;
        }

        /// <summary>
        /// A client .sc2 map is ported as a single OBJ that still carries the
        /// authored per-entity groups and its own .mtl. BattleObjects walks the
        /// imported hierarchy at runtime and cooks a MeshCollider from every
        /// mesh, so these meshes must stay readable in a player build, and the
        /// OBJ materials must be imported instead of being dropped the way tank
        /// models drop theirs.
        /// </summary>
        private static void ApplyMapSceneModelSettings(ModelImporter importer)
        {
            importer.globalScale = 1f;
            importer.useFileScale = false;
            importer.meshCompression = ModelImporterMeshCompression.Off;
            importer.isReadable = true;
            importer.importBlendShapes = false;
            importer.importAnimation = false;
            importer.importCameras = false;
            importer.importLights = false;
            importer.importVisibility = false;
            importer.generateSecondaryUV = false;
            importer.importNormals = ModelImporterNormals.Import;
            importer.importTangents = ModelImporterTangents.CalculateMikk;
            importer.materialImportMode =
                ModelImporterMaterialImportMode.ImportViaMaterialDescription;
            importer.materialLocation = ModelImporterMaterialLocation.InPrefab;

            // The exporter emits one indexed vertex stream per source batch;
            // reordering it would dissolve the authored entity groups the
            // runtime uses to separate foliage from solid geometry.
            importer.optimizeMeshPolygons = false;
            importer.optimizeMeshVertices = false;
            importer.addCollider = false;
        }

        /// <summary>Whether <paramref name="path"/> is one of the login stems, which stream.</summary>
        public static bool IsMusicStem(string path)
        {
            return Array.IndexOf(MusicClips, Path.GetFileNameWithoutExtension(path)) >= 0;
        }

        /// <summary>Whether <paramref name="path"/> is a tune, which plays from Vorbis in memory.</summary>
        public static bool IsTune(string path)
        {
            return path.EndsWith(TuneExtension, StringComparison.OrdinalIgnoreCase);
        }

        private void OnPreprocessAudio()
        {
            if (!assetPath.StartsWith(AudioRoot, StringComparison.Ordinal))
            {
                return;
            }

            var importer = (AudioImporter)assetImporter;
            var isMusic = IsMusicStem(assetPath);

            importer.forceToMono = false;
            importer.ambisonic = false;
            importer.loadInBackground = isMusic;

            var settings = importer.defaultSampleSettings;

            // Never resample: every clip keeps the rate it was made at.
            settings.sampleRateSetting = AudioSampleRateSetting.PreserveSampleRate;

            if (isMusic)
            {
                // ~43 s stereo each; PCM would add 24 MB to the APK, so these
                // stream as near-transparent Vorbis. Switch to PCM here if a
                // lossless build is ever wanted.
                settings.loadType = AudioClipLoadType.Streaming;
                settings.compressionFormat = AudioCompressionFormat.Vorbis;
                settings.quality = 1f;
                settings.preloadAudioData = false;
            }
            else if (IsTune(assetPath))
            {
                // Tunes run up to 35 s of stereo, and as PCM they took 25 MB.
                // One plays at a time, so they stay Vorbis in memory and
                // decode as they play.
                settings.loadType = AudioClipLoadType.CompressedInMemory;
                settings.compressionFormat = AudioCompressionFormat.Vorbis;
                settings.quality = TuneQuality;
                settings.preloadAudioData = true;
            }
            else
            {
                // Effects: a battle plays many at once, so they decode once
                // on load. ADPCM decodes about as cheaply as PCM loads, in
                // under a third of the 123 MB PCM took for them.
                settings.loadType = AudioClipLoadType.DecompressOnLoad;
                settings.compressionFormat = AudioCompressionFormat.ADPCM;
                settings.preloadAudioData = true;
            }

            importer.defaultSampleSettings = settings;
        }
    }
}
