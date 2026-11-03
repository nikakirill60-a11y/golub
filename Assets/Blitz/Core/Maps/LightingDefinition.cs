using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Core.Maps
{
    /// <summary>The scene's WorldEnvironment, property for property.</summary>
    public sealed class LightingEnvironment
    {
        public string Path { get; set; }

        public string Resource { get; set; }

        public int BackgroundMode { get; set; }

        public float[] BackgroundColor { get; set; }

        /// <summary>Godot background_energy_multiplier.</summary>
        public float BackgroundEnergy { get; set; }

        /// <summary>Godot AmbientSource: 0 background, 1 disabled, 2 colour, 3 sky.</summary>
        public int AmbientSource { get; set; }

        public float[] AmbientColor { get; set; }

        public float AmbientEnergy { get; set; }

        public float AmbientSkyContribution { get; set; }

        public int ReflectedLightSource { get; set; }

        public int TonemapMode { get; set; }

        public float TonemapExposure { get; set; }

        public float TonemapWhite { get; set; }

        /// <summary>Both scenes leave glow off, so the port draws no bloom.</summary>
        public bool GlowEnabled { get; set; }

        public bool FogEnabled { get; set; }

        /// <summary>Godot FogMode: 1 is depth fog, which both scenes use.</summary>
        public int FogMode { get; set; }

        public float[] FogLightColor { get; set; }

        public float FogLightEnergy { get; set; }

        public float FogSunScatter { get; set; }

        public float FogDensity { get; set; }

        public float FogHeight { get; set; }

        public float FogHeightDensity { get; set; }

        public float FogAerialPerspective { get; set; }

        public float FogSkyAffect { get; set; }

        public float FogDepthCurve { get; set; }

        public float FogDepthBegin { get; set; }

        public float FogDepthEnd { get; set; }

        public bool AdjustmentEnabled { get; set; }

        public float AdjustmentBrightness { get; set; }

        public float AdjustmentContrast { get; set; }

        public float AdjustmentSaturation { get; set; }

        public bool SsaoEnabled { get; set; }

        public bool SsilEnabled { get; set; }

        public bool SdfgiEnabled { get; set; }

        public bool VolumetricFogEnabled { get; set; }

        /// <summary>
        /// Decodes one authored colour channel the way the source does. A Godot
        /// Environment stores its colours sRGB encoded and runs srgb_to_linear
        /// on them before they shade anything: a probe scene that lights a
        /// white quad with a 0.5 ambient reads 0.5 back out of the frame, not
        /// the 0.735 an already-linear 0.5 would have produced.
        /// </summary>
        public static float SourceChannel(float value)
        {
            return value <= 0.04045f
                ? value / 12.92f
                : (float)Math.Pow((value + 0.055f) / 1.055f, 2.4f);
        }

        /// <summary>
        /// Ambient as Godot resolves it. Both scenes set the ambient source to
        /// Background, so the flat background colour scaled by its energy
        /// multiplier is the ambient term and ambient_light_color never reaches
        /// a shader. The multiplier scales the decoded colour, because the
        /// source decodes first and multiplies in linear space.
        /// </summary>
        public float[] AmbientRgb()
        {
            if (AmbientSource == 1)
            {
                return new float[] { 0f, 0f, 0f };
            }

            var source = AmbientSource == 0 ? BackgroundColor : AmbientColor;
            var energy = AmbientSource == 0 ? BackgroundEnergy : AmbientEnergy;
            return new float[]
            {
                SourceChannel(source[0]) * energy,
                SourceChannel(source[1]) * energy,
                SourceChannel(source[2]) * energy,
            };
        }

        /// <summary>
        /// The fog colour as Godot resolves it: fog_light_color decoded, then
        /// scaled by fog_light_energy.
        /// </summary>
        public float[] FogLightRgb()
        {
            return new float[]
            {
                SourceChannel(FogLightColor[0]) * FogLightEnergy,
                SourceChannel(FogLightColor[1]) * FogLightEnergy,
                SourceChannel(FogLightColor[2]) * FogLightEnergy,
            };
        }
    }

    /// <summary>
    /// One light node, with every authored value: the port reads bake mode and
    /// shadow flags from here instead of hard-coding a rig.
    /// </summary>
    public sealed class LightingLight
    {
        public string Path { get; set; }

        public string Type { get; set; }

        public bool Visible { get; set; }

        public float[] Basis { get; set; }

        public float[] Origin { get; set; }

        public float[] Color { get; set; }

        public float Energy { get; set; }

        public float IndirectEnergy { get; set; }

        public float VolumetricFogEnergy { get; set; }

        public float Specular { get; set; }

        public float Size { get; set; }

        public bool Negative { get; set; }

        /// <summary>Godot BakeMode: 0 disabled, 1 static, 2 dynamic.</summary>
        public int BakeMode { get; set; }

        public long CullMask { get; set; }

        public bool ShadowEnabled { get; set; }

        public float ShadowBias { get; set; }

        public float ShadowNormalBias { get; set; }

        public float ShadowOpacity { get; set; }

        public float ShadowBlur { get; set; }

        public bool ShadowReverseCullFace { get; set; }

        public float ShadowTransmittanceBias { get; set; }

        public long ShadowCasterMask { get; set; }

        public bool DistanceFadeEnabled { get; set; }

        public float DistanceFadeBegin { get; set; }

        public float DistanceFadeShadow { get; set; }

        public float DistanceFadeLength { get; set; }

        public float OmniRange { get; set; }

        public float OmniAttenuation { get; set; }

        public int OmniShadowMode { get; set; }

        public float SpotRange { get; set; }

        public float SpotAttenuation { get; set; }

        public float SpotAngle { get; set; }

        public float SpotAngleAttenuation { get; set; }

        public int DirectionalShadowMode { get; set; }

        public float DirectionalShadowSplit1 { get; set; }

        public float DirectionalShadowSplit2 { get; set; }

        public float DirectionalShadowSplit3 { get; set; }

        public bool DirectionalShadowBlendSplits { get; set; }

        public float DirectionalShadowFadeStart { get; set; }

        public float DirectionalShadowMaxDistance { get; set; }

        public float DirectionalShadowPancakeSize { get; set; }

        public float AngularDistance { get; set; }

        public int SkyMode { get; set; }

        /// <summary>
        /// Baked into the atlas. Godot still lights dynamic objects with such a
        /// light in realtime and simply keeps its direct contribution out of
        /// the probes, which is why the port keeps these nodes in the scene.
        /// </summary>
        public bool IsStaticBake
        {
            get { return BakeMode == 1; }
        }

        /// <summary>Fully realtime: the hangar chest spot is the only one.</summary>
        public bool IsDynamicBake
        {
            get { return BakeMode == 2; }
        }
    }

    /// <summary>The LightmapGI node and the atlas strip it baked.</summary>
    public sealed class LightingAtlas
    {
        public string Path { get; set; }

        public string Resource { get; set; }

        /// <summary>Unity resource path of the ported atlas grid.</summary>
        public string Atlas { get; set; }

        public int Slices { get; set; }

        public int SliceWidth { get; set; }

        public int SliceHeight { get; set; }

        /// <summary>Number of source lightmap slices packed along each atlas axis.</summary>
        public int LayoutHorizontal { get; set; }

        public int LayoutVertical { get; set; }

        public float BakedExposure { get; set; }

        /// <summary>Both bakes are plain colour, so one texture fetch is exact.</summary>
        public bool UsesSphericalHarmonics { get; set; }

        public bool Interior { get; set; }

        /// <summary>Light probes in the bake: both scenes baked none.</summary>
        public int Probes { get; set; }

        public int Bounces { get; set; }

        public float Bias { get; set; }

        public int Bytes { get; set; }

        public string Sha256 { get; set; }
    }

    /// <summary>One baked instance: which slice and rect of the strip it owns.</summary>
    public sealed class LightingEntry
    {
        public string Path { get; set; }

        public string Name { get; set; }

        public int Slice { get; set; }

        /// <summary>Position and size inside the slice, in Godot UV space.</summary>
        public float[] Rect { get; set; }

        public List<string> Meshes { get; private set; }

        public float[] Basis { get; set; }

        public float[] Origin { get; set; }

        /// <summary>Godot cast_shadow, straight off the node.</summary>
        public int CastShadow { get; set; }

        /// <summary>Godot gi_mode: 1 is static, the one the bake covers.</summary>
        public int GiMode { get; set; }

        public long Layers { get; set; }

        /// <summary>
        /// Key of the source material this instance draws with, or null in a
        /// scene whose props carry their materials elsewhere (the map's object
        /// table already does). One instance owns one rect of the atlas, so it
        /// draws with exactly one material.
        /// </summary>
        public string Material { get; set; }

        /// <summary>
        /// The rect as a Unity lightmap transform, so a shader can sample the
        /// strip with uv = uv2 * scale + offset: scale in 0 and 1, offset in 2
        /// and 3. The horizontal terms fold the slice index into the strip the
        /// porter copies whole, and the vertical terms are mirrored because
        /// Godot counts V downwards while Unity counts it upwards.
        /// </summary>
        public float[] AtlasScaleOffset { get; internal set; }

        public LightingEntry()
        {
            Meshes = new List<string>();
        }
    }

    /// <summary>
    /// One source material, in the terms the port's shader rebuilds it with.
    /// The atlas carries the light alone, so a baked surface still needs the
    /// albedo, the emission and the tint the source multiplies them by.
    /// </summary>
    public sealed class LightingMaterial
    {
        public string Key { get; set; }

        public string Resource { get; set; }

        /// <summary>Godot class name, ShaderMaterial for every baked surface.</summary>
        public string Class { get; set; }

        /// <summary>The source shader, kept so a port can refuse a stranger.</summary>
        public string Shader { get; set; }

        /// <summary>Linear RGBA tint the source multiplies its textures by.</summary>
        public float[] Albedo { get; set; }

        /// <summary>Resources path of the ported albedo texture.</summary>
        public string Texture { get; set; }

        public bool Emissive { get; set; }

        /// <summary>
        /// The ported emission texture, null unless the surface emits: Godot
        /// keeps the texture assigned when the flag is off and never samples
        /// it, so neither may the port.
        /// </summary>
        public string EmissiveTexture { get; set; }

        /// <summary>The exponent the source raises the albedo to.</summary>
        public float Contrast { get; set; }

        public float[] Uv1Scale { get; set; }

        public float[] Uv1Offset { get; set; }
    }

    /// <summary>The payload of unwrapped meshes the entries draw.</summary>
    public sealed class LightingMeshInfo
    {
        public string Resource { get; set; }

        public int Count { get; set; }

        public int Vertices { get; set; }

        public int Triangles { get; set; }

        public int Bytes { get; set; }

        public string Sha256 { get; set; }
    }

    /// <summary>
    /// A scene's lighting as Tools/port_godot_lighting.py exported it. The
    /// source bakes both the hangar rig and the map sun into atlases, so the
    /// port reproduces the bake itself rather than fitting realtime lights to
    /// it: every number here is still the one Godot authored.
    /// </summary>
    public sealed class LightingDefinition
    {
        private Dictionary<string, LightingLight> _lightsByPath;
        private Dictionary<string, LightingEntry> _entriesByPath;
        private Dictionary<string, LightingMaterial> _materialsByKey;

        public LightingDefinition()
        {
            Lights = new List<LightingLight>();
            Entries = new List<LightingEntry>();
            Materials = new List<LightingMaterial>();
            StaleEntries = new List<string>();
        }

        public string Id { get; set; }

        public string Scene { get; set; }

        public LightingEnvironment Environment { get; set; }

        public LightingAtlas Lightmap { get; set; }

        public LightingMeshInfo Meshes { get; set; }

        public List<LightingLight> Lights { get; private set; }

        public List<LightingEntry> Entries { get; private set; }

        /// <summary>
        /// The materials the entries draw with. Empty for a scene that ports
        /// its materials some other way, which the map does.
        /// </summary>
        public List<LightingMaterial> Materials { get; private set; }

        /// <summary>
        /// Bake users whose node the source scene no longer has. The bake keeps
        /// them, the port skips them, and callers log them so a stale atlas is
        /// visible rather than silent.
        /// </summary>
        public List<string> StaleEntries { get; private set; }

        public LightingLight Light(string path)
        {
            if (_lightsByPath == null)
            {
                _lightsByPath = new Dictionary<string, LightingLight>(
                    Lights.Count, StringComparer.Ordinal);
                foreach (var light in Lights)
                {
                    _lightsByPath[light.Path] = light;
                }
            }

            LightingLight found;
            if (path == null || !_lightsByPath.TryGetValue(path, out found))
            {
                throw new ArgumentException("lighting '" + Id + "' has no light '" + path + "'");
            }

            return found;
        }

        public bool HasEntry(string path)
        {
            EnsureEntryIndex();
            return path != null && _entriesByPath.ContainsKey(path);
        }

        public LightingEntry Entry(string path)
        {
            EnsureEntryIndex();
            LightingEntry found;
            if (path == null || !_entriesByPath.TryGetValue(path, out found))
            {
                throw new ArgumentException("lighting '" + Id + "' does not bake '" + path + "'");
            }

            return found;
        }

        public bool HasMaterial(string key)
        {
            EnsureMaterialIndex();
            return key != null && _materialsByKey.ContainsKey(key);
        }

        /// <summary>The material an entry names.</summary>
        public LightingMaterial Material(string key)
        {
            EnsureMaterialIndex();
            LightingMaterial found;
            if (key == null || !_materialsByKey.TryGetValue(key, out found))
            {
                throw new ArgumentException(
                    "lighting '" + Id + "' has no material '" + key + "'");
            }

            return found;
        }

        public static LightingDefinition Parse(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                throw new ArgumentException("lighting definition is empty");
            }

            var root = JsonNode.Parse(json);
            var lighting = new LightingDefinition
            {
                Id = root["id"].AsString(),
                Scene = root["scene"].AsString(),
                Environment = ReadEnvironment(root["environment"]),
                Lightmap = ReadAtlas(root["lightmap"]),
                Meshes = ReadMeshes(root["meshes"]),
            };

            foreach (var item in root["lights"].Items)
            {
                lighting.Lights.Add(ReadLight(item));
            }

            foreach (var item in root["materials"].Items)
            {
                lighting.Materials.Add(ReadMaterial(item));
            }

            foreach (var item in root["entries"].Items)
            {
                lighting.Entries.Add(ReadEntry(item, lighting.Lightmap));
            }

            foreach (var item in root["staleEntries"].Items)
            {
                lighting.StaleEntries.Add(item.AsString());
            }

            if (lighting.Entries.Count == 0)
            {
                throw new ArgumentException(
                    "lighting definition '" + lighting.Id + "' bakes nothing");
            }

            // A named material the manifest does not carry would draw white at
            // runtime, which is exactly the silent failure the hand-written
            // material list used to allow.
            foreach (var entry in lighting.Entries)
            {
                if (entry.Material != null && !lighting.HasMaterial(entry.Material))
                {
                    throw new ArgumentException(
                        "baked instance '" + entry.Path + "' draws with unexported '" +
                        entry.Material + "'");
                }
            }

            return lighting;
        }

        private void EnsureEntryIndex()
        {
            if (_entriesByPath != null)
            {
                return;
            }

            _entriesByPath = new Dictionary<string, LightingEntry>(
                Entries.Count, StringComparer.Ordinal);
            foreach (var entry in Entries)
            {
                _entriesByPath[entry.Path] = entry;
            }
        }

        private void EnsureMaterialIndex()
        {
            if (_materialsByKey != null)
            {
                return;
            }

            _materialsByKey = new Dictionary<string, LightingMaterial>(
                Materials.Count, StringComparer.Ordinal);
            foreach (var material in Materials)
            {
                _materialsByKey[material.Key] = material;
            }
        }

        private static LightingEnvironment ReadEnvironment(JsonNode node)
        {
            return new LightingEnvironment
            {
                Path = node["path"].AsString(),
                Resource = node["resource"].AsString(),
                BackgroundMode = node["backgroundMode"].AsInt(),
                BackgroundColor = Floats(node["backgroundColor"], 4),
                BackgroundEnergy = node["backgroundEnergyMultiplier"].AsFloat(),
                AmbientSource = node["ambientLightSource"].AsInt(),
                AmbientColor = Floats(node["ambientLightColor"], 4),
                AmbientEnergy = node["ambientLightEnergy"].AsFloat(),
                AmbientSkyContribution = node["ambientLightSkyContribution"].AsFloat(),
                ReflectedLightSource = node["reflectedLightSource"].AsInt(),
                TonemapMode = node["tonemapMode"].AsInt(),
                TonemapExposure = node["tonemapExposure"].AsFloat(),
                TonemapWhite = node["tonemapWhite"].AsFloat(),
                GlowEnabled = node["glowEnabled"].AsBool(),
                FogEnabled = node["fogEnabled"].AsBool(),
                FogMode = node["fogMode"].AsInt(),
                FogLightColor = Floats(node["fogLightColor"], 4),
                FogLightEnergy = node["fogLightEnergy"].AsFloat(),
                FogSunScatter = node["fogSunScatter"].AsFloat(),
                FogDensity = node["fogDensity"].AsFloat(),
                FogHeight = node["fogHeight"].AsFloat(),
                FogHeightDensity = node["fogHeightDensity"].AsFloat(),
                FogAerialPerspective = node["fogAerialPerspective"].AsFloat(),
                FogSkyAffect = node["fogSkyAffect"].AsFloat(),
                FogDepthCurve = node["fogDepthCurve"].AsFloat(),
                FogDepthBegin = node["fogDepthBegin"].AsFloat(),
                FogDepthEnd = node["fogDepthEnd"].AsFloat(),
                AdjustmentEnabled = node["adjustmentEnabled"].AsBool(),
                AdjustmentBrightness = node["adjustmentBrightness"].AsFloat(),
                AdjustmentContrast = node["adjustmentContrast"].AsFloat(),
                AdjustmentSaturation = node["adjustmentSaturation"].AsFloat(),
                SsaoEnabled = node["ssaoEnabled"].AsBool(),
                SsilEnabled = node["ssilEnabled"].AsBool(),
                SdfgiEnabled = node["sdfgiEnabled"].AsBool(),
                VolumetricFogEnabled = node["volumetricFogEnabled"].AsBool(),
            };
        }

        private static LightingLight ReadLight(JsonNode node)
        {
            var transform = node["transform"];
            return new LightingLight
            {
                Path = node["path"].AsString(),
                Type = node["type"].AsString(),
                Visible = node["visible"].AsBool(),
                Basis = Floats(transform["basis"], 9),
                Origin = Floats(transform["origin"], 3),
                Color = Floats(node["lightColor"], 4),
                Energy = node["lightEnergy"].AsFloat(),
                IndirectEnergy = node["lightIndirectEnergy"].AsFloat(),
                VolumetricFogEnergy = node["lightVolumetricFogEnergy"].AsFloat(),
                Specular = node["lightSpecular"].AsFloat(),
                Size = node["lightSize"].AsFloat(),
                Negative = node["lightNegative"].AsBool(),
                BakeMode = node["lightBakeMode"].AsInt(),
                CullMask = node["lightCullMask"].AsLong(),
                ShadowEnabled = node["shadowEnabled"].AsBool(),
                ShadowBias = node["shadowBias"].AsFloat(),
                ShadowNormalBias = node["shadowNormalBias"].AsFloat(),
                ShadowOpacity = node["shadowOpacity"].AsFloat(),
                ShadowBlur = node["shadowBlur"].AsFloat(),
                ShadowReverseCullFace = node["shadowReverseCullFace"].AsBool(),
                ShadowTransmittanceBias = node["shadowTransmittanceBias"].AsFloat(),
                ShadowCasterMask = node["shadowCasterMask"].AsLong(),
                DistanceFadeEnabled = node["distanceFadeEnabled"].AsBool(),
                DistanceFadeBegin = node["distanceFadeBegin"].AsFloat(),
                DistanceFadeShadow = node["distanceFadeShadow"].AsFloat(),
                DistanceFadeLength = node["distanceFadeLength"].AsFloat(),
                OmniRange = node["omniRange"].AsFloat(),
                OmniAttenuation = node["omniAttenuation"].AsFloat(),
                OmniShadowMode = node["omniShadowMode"].AsInt(),
                SpotRange = node["spotRange"].AsFloat(),
                SpotAttenuation = node["spotAttenuation"].AsFloat(),
                SpotAngle = node["spotAngle"].AsFloat(),
                SpotAngleAttenuation = node["spotAngleAttenuation"].AsFloat(),
                DirectionalShadowMode = node["directionalShadowMode"].AsInt(),
                DirectionalShadowSplit1 = node["directionalShadowSplit1"].AsFloat(),
                DirectionalShadowSplit2 = node["directionalShadowSplit2"].AsFloat(),
                DirectionalShadowSplit3 = node["directionalShadowSplit3"].AsFloat(),
                DirectionalShadowBlendSplits = node["directionalShadowBlendSplits"].AsBool(),
                DirectionalShadowFadeStart = node["directionalShadowFadeStart"].AsFloat(),
                DirectionalShadowMaxDistance = node["directionalShadowMaxDistance"].AsFloat(),
                DirectionalShadowPancakeSize = node["directionalShadowPancakeSize"].AsFloat(),
                AngularDistance = node["lightAngularDistance"].AsFloat(),
                SkyMode = node["skyMode"].AsInt(),
            };
        }

        private static LightingAtlas ReadAtlas(JsonNode node)
        {
            var atlas = new LightingAtlas
            {
                Path = node["path"].AsString(),
                Resource = node["resource"].AsString(),
                Atlas = node["atlas"].AsString(),
                Slices = node["slices"].AsInt(),
                SliceWidth = node["sliceWidth"].AsInt(),
                SliceHeight = node["sliceHeight"].AsInt(),
                LayoutHorizontal = node["layout"]["horizontal"].AsInt(),
                LayoutVertical = node["layout"]["vertical"].AsInt(),
                BakedExposure = node["bakedExposure"].AsFloat(),
                UsesSphericalHarmonics = node["usesSphericalHarmonics"].AsBool(),
                Interior = node["interior"].AsBool(),
                Probes = node["probes"].AsInt(),
                Bounces = node["bounces"].AsInt(),
                Bias = node["bias"].AsFloat(),
                Bytes = node["bytes"].AsInt(),
                Sha256 = node["sha256"].AsString(),
            };

            if (atlas.Slices <= 0 || atlas.LayoutHorizontal <= 0 ||
                atlas.LayoutVertical <= 0 ||
                atlas.LayoutHorizontal * atlas.LayoutVertical != atlas.Slices)
            {
                throw new ArgumentException("the lightmap atlas layout does not match its slices");
            }

            return atlas;
        }

        private static LightingMeshInfo ReadMeshes(JsonNode node)
        {
            return new LightingMeshInfo
            {
                Resource = node["resource"].AsString(),
                Count = node["count"].AsInt(),
                Vertices = node["vertices"].AsInt(),
                Triangles = node["triangles"].AsInt(),
                Bytes = node["bytes"].AsInt(),
                Sha256 = node["sha256"].AsString(),
            };
        }

        private static LightingEntry ReadEntry(JsonNode node, LightingAtlas atlas)
        {
            var transform = node["transform"];
            var entry = new LightingEntry
            {
                Path = node["path"].AsString(),
                Name = node["name"].AsString(),
                Slice = node["slice"].AsInt(),
                Rect = Floats(node["rect"], 4),
                Basis = Floats(transform["basis"], 9),
                Origin = Floats(transform["origin"], 3),
                CastShadow = node["castShadow"].AsInt(),
                GiMode = node["giMode"].AsInt(),
                Layers = node["layers"].AsLong(),
                Material = node.Has("material") ? node["material"].AsString() : null,
            };

            foreach (var mesh in node["meshes"].Items)
            {
                entry.Meshes.Add(mesh.AsString());
            }

            if (entry.Meshes.Count == 0)
            {
                throw new ArgumentException("baked instance '" + entry.Path + "' draws nothing");
            }

            if (entry.Slice < 0 || entry.Slice >= atlas.Slices)
            {
                throw new ArgumentException(
                    "baked instance '" + entry.Path + "' points at a slice the atlas lacks");
            }

            if (entry.Rect[2] <= 0f || entry.Rect[3] <= 0f ||
                entry.Rect[0] < 0f || entry.Rect[1] < 0f ||
                entry.Rect[0] + entry.Rect[2] > 1.0001f ||
                entry.Rect[1] + entry.Rect[3] > 1.0001f)
            {
                throw new ArgumentException(
                    "baked instance '" + entry.Path + "' has a rect outside its slice");
            }

            var atlasColumn = entry.Slice % atlas.LayoutHorizontal;
            var atlasRow = entry.Slice / atlas.LayoutHorizontal;
            entry.AtlasScaleOffset = new float[]
            {
                entry.Rect[2] / atlas.LayoutHorizontal,
                -entry.Rect[3] / atlas.LayoutVertical,
                (atlasColumn + entry.Rect[0]) / atlas.LayoutHorizontal,
                1f - (atlasRow + entry.Rect[1]) / atlas.LayoutVertical,
            };

            return entry;
        }

        private static LightingMaterial ReadMaterial(JsonNode node)
        {
            var material = new LightingMaterial
            {
                Key = node["key"].AsString(),
                Resource = node["resource"].AsString(),
                Class = node["class"].AsString(),
                Shader = node["shader"].AsString(),
                Albedo = Floats(node["albedo"], 4),
                Texture = node["texture"].AsString(),
                Emissive = node["emissive"].AsBool(),
                EmissiveTexture = node["emissiveTexture"].IsNull
                    ? null
                    : node["emissiveTexture"].AsString(),
                Contrast = node["contrast"].AsFloat(),
                Uv1Scale = Floats(node["uv1Scale"], 2),
                Uv1Offset = Floats(node["uv1Offset"], 2),
            };

            if (string.IsNullOrEmpty(material.Key))
            {
                throw new ArgumentException("a ported material has no key");
            }

            if (string.IsNullOrEmpty(material.Texture))
            {
                throw new ArgumentException(
                    "material '" + material.Key + "' has no albedo texture");
            }

            if (material.Emissive && string.IsNullOrEmpty(material.EmissiveTexture))
            {
                throw new ArgumentException(
                    "material '" + material.Key + "' emits without a texture");
            }

            // The source raises the albedo to this power, so zero would flatten
            // every surface to white.
            if (material.Contrast <= 0f)
            {
                throw new ArgumentException(
                    "material '" + material.Key + "' has a non-positive contrast");
            }

            return material;
        }

        private static float[] Floats(JsonNode node, int expected)
        {
            if (node == null || node.Count != expected)
            {
                throw new ArgumentException("expected " + expected + " components");
            }

            var values = new float[expected];
            for (var i = 0; i < expected; i++)
            {
                values[i] = node[i].AsFloat();
            }

            return values;
        }
    }
}
