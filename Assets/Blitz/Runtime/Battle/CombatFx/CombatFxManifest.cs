using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Tanks;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle.CombatFx
{
    /// <summary>A baked <c>CurveTexture</c> of the process material.</summary>
    public sealed class GodotCurve
    {
        /// <summary><c>curve.sample_baked(i / width)</c> per texel; empty for the placeholder.</summary>
        public float[] Texels = new float[0];

        /// <summary>The texture has no curve, so the renderer binds its checkerboard.</summary>
        public bool Placeholder;

        /// <summary><c>texture(curve, vec2(t)).r</c>.</summary>
        public float Sample(float t)
        {
            return Placeholder ? GodotFxMath.PlaceholderCurve(t) : GodotFxMath.SampleRow(Texels, t);
        }

        internal static GodotCurve Read(JsonNode node)
        {
            if (node == null || node.IsNull)
            {
                return null;
            }

            var curve = new GodotCurve { Placeholder = node["placeholder"].AsBool() };
            if (!curve.Placeholder)
            {
                curve.Texels = CombatFxManifest.Floats(node["texels"]);
                if (curve.Texels.Length == 0)
                {
                    throw new FormatException("combat fx curve has no texels");
                }
            }

            return curve;
        }
    }

    /// <summary>A baked <c>GradientTexture1D</c>: its RGBA8 texels as the GPU reads them.</summary>
    public sealed class GodotRamp
    {
        public Vector4[] Texels = new Vector4[0];

        /// <summary><c>texture(ramp, vec2(t))</c>, the raw texel values.</summary>
        public Vector4 Sample(float t)
        {
            return GodotFxMath.SampleRow(Texels, t);
        }

        internal static GodotRamp Read(JsonNode node)
        {
            if (node == null || node.IsNull)
            {
                return null;
            }

            var texels = node["texels"];
            var ramp = new GodotRamp { Texels = new Vector4[texels.Count] };
            for (var i = 0; i < texels.Count; i++)
            {
                ramp.Texels[i] = CombatFxManifest.Vec4(texels[i]);
            }

            if (ramp.Texels.Length == 0)
            {
                throw new FormatException("combat fx gradient has no texels");
            }

            return ramp;
        }
    }

    /// <summary>The <c>StandardMaterial3D</c> on an emitter's draw pass.</summary>
    public sealed class GodotParticleMaterial
    {
        public bool Transparent;
        public bool BlendAdd;
        public bool Fog;
        public bool VertexColor;
        public bool VertexColorSrgb;
        /// <summary>Linear albedo colour (source_color).</summary>
        public Vector4 Albedo;
        public string Texture;
        /// <summary>TEXTURE_FILTER_LINEAR_WITH_MIPMAPS rather than plain LINEAR.</summary>
        public bool Mipmaps;
        public bool Repeat;
        public bool BillboardParticles;
        public int HFrames = 1;
        public int VFrames = 1;
        public bool AnimLoop;
    }

    /// <summary>One <c>GPUParticles3D</c> of a ported scene with its process material.</summary>
    public sealed class GodotEmitterData
    {
        public string Name;
        public int Index;
        public bool Visible;
        public GodotXform Local;
        public int Amount;
        public float AmountRatio;
        public float Lifetime;
        public bool OneShot;
        public float Preprocess;
        public float SpeedScale;
        public float Explosiveness;
        public float Randomness;
        public int FixedFps;
        public bool Interpolate;
        public bool FractDelta;
        public bool LocalCoords;
        public int TransformAlign;
        public int AlignChannel;
        public int AlignAxis;
        public bool UseFixedSeed;
        public uint Seed;
        public float SortingOffset;

        // ParticleProcessMaterial
        public Vector3 Direction;
        public float Spread;
        public float Flatness;
        public float VelocityMin, VelocityMax;
        public float AngularVelocityMin, AngularVelocityMax;
        public float DirectionalVelocityMin, DirectionalVelocityMax;
        public float RadialVelocityMin, RadialVelocityMax;
        public float OrbitVelocityMin, OrbitVelocityMax;
        public Vector3 Gravity;
        public float LinearAccelMin, LinearAccelMax;
        public float RadialAccelMin, RadialAccelMax;
        public float TangentialAccelMin, TangentialAccelMax;
        public float DampingMin, DampingMax;
        public float AngleMin, AngleMax;
        public float ScaleMin, ScaleMax;
        public float LifetimeRandomness;
        public float AnimSpeedMin, AnimSpeedMax;
        public float AnimOffsetMin, AnimOffsetMax;
        public float HueMin, HueMax;
        /// <summary>0 point, 1 sphere (the only shapes the scenes use).</summary>
        public int EmissionShape;
        public Vector3 EmissionOffset;
        public Vector3 EmissionScale;
        public float EmissionRadius;
        public Vector3 VelocityPivot;
        public bool AlignY;
        public bool RotateY;
        public bool DampingAsFriction;
        /// <summary>Linear <c>color_value</c> (source_color).</summary>
        public Vector4 Color;
        public GodotCurve ScaleCurve;
        public GodotCurve AlphaCurve;
        public GodotRamp ColorRamp;

        // QuadMesh draw pass
        public Vector2 Size;
        public Vector3 CenterOffset;
        /// <summary>0 FACE_X, 1 FACE_Y, 2 FACE_Z.</summary>
        public int Orientation;
        public GodotParticleMaterial Material;
    }

    /// <summary>One <c>ParticleStarter</c> scene.</summary>
    public sealed class GodotEffectData
    {
        public string Name;
        public GodotXform Root;
        public readonly List<GodotEmitterData> Emitters = new List<GodotEmitterData>();

        /// <summary>The longest emitter lifetime, which ParticleStarter frees after.</summary>
        public float MaxLifetime
        {
            get
            {
                var max = 0f;
                for (var i = 0; i < Emitters.Count; i++)
                {
                    max = Mathf.Max(max, Emitters[i].Lifetime);
                }

                return max;
            }
        }
    }

    /// <summary>The <c>trail_preset.tscn</c> GPUTrail3D and its draw pass.</summary>
    public sealed class GodotTrailData
    {
        public int Amount;
        public int FixedFps;
        public string Texture;
        public GodotCurve Curve;
    }

    /// <summary>
    /// <c>Resources/Data/combat_fx_manifest.json</c>, written by
    /// <c>Tools/port_godot_combat_fx.py</c> from a headless dump of the Godot
    /// project: every scene, emitter and material the combat effects use, the
    /// constants the scripts hard-code and which scene answers which event.
    /// </summary>
    public sealed class CombatFxManifest
    {
        public const string ResourcePath = "Data/combat_fx_manifest";

        public readonly Dictionary<string, GodotEffectData> Scenes = new Dictionary<string, GodotEffectData>();
        public readonly Dictionary<string, string> TextureResources = new Dictionary<string, string>();
        private readonly Dictionary<int, string> _shotScenes = new Dictionary<int, string>();
        private readonly Dictionary<int, string> _penetrationScenes = new Dictionary<int, string>();
        private readonly Dictionary<int, string> _objectScenes = new Dictionary<int, string>();

        public GodotTrailData Trail;

        /// <summary><c>ap_color</c>, the head of the tracer gradient.</summary>
        public Vector4 TrailColor;

        /// <summary><c>heat_color</c>.</summary>
        public Vector4 TrailColorHeat;

        /// <summary><c>Color.BEIGE</c> / <c>Color.BISQUE</c> on the shell sprite.</summary>
        public Vector4 ShellModulate, ShellModulateHeat;

        public string ShellTexture;
        public bool ShellMipmaps;

        public float ShellPixelCaliberMin, ShellPixelCaliberMax, ShellPixelFactor;
        public float TrailCaliberMin, TrailCaliberMax, TrailScaleFactor;
        public float RicochetTrailPixelFactor;
        public float TrailAngleFirst, TrailAngleSecond;
        public float TrailFreeDelay, RicochetTrailFreeDelay;
        public float ImpactScaleFactor;
        public float TrailLeadMin, TrailLeadMax, TrailLeadSpeedFactor;
        public float StarterAabbExtent;
        public float StarterSelfDestroyExtra;

        private static CombatFxManifest _shared;
        private static bool _loadFailed;

        /// <summary>The manifest shipped in Resources, parsed once; null if it is missing.</summary>
        public static CombatFxManifest Shared
        {
            get
            {
                if (_shared != null || _loadFailed)
                {
                    return _shared;
                }

                var asset = Resources.Load<TextAsset>(ResourcePath);
                if (asset == null)
                {
                    _loadFailed = true;
                    Debug.LogError("[Blitz] combat fx manifest is missing: " + ResourcePath);
                    return null;
                }

                try
                {
                    _shared = Parse(asset.text);
                }
                catch (Exception e)
                {
                    _loadFailed = true;
                    Debug.LogError("[Blitz] combat fx manifest is invalid: " + e.Message);
                }

                Resources.UnloadAsset(asset);
                return _shared;
            }
        }

        /// <summary><c>gunsound_to_particle[gun_info.shot_sound]</c>.</summary>
        public GodotEffectData ShotScene(int shootSound)
        {
            string name;
            return _shotScenes.TryGetValue(shootSound, out name) ? Scene(name) : null;
        }

        /// <summary><c>shell_type_to_eff_pen</c>: penetration, bounce, ricochet, not hit.</summary>
        public GodotEffectData PenetrationScene(ShellType type)
        {
            string name;
            return _penetrationScenes.TryGetValue((int)type, out name) ? Scene(name) : null;
        }

        /// <summary><c>shell_type_to_eff_obj</c>: anything that is not a tank.</summary>
        public GodotEffectData ObjectScene(ShellType type)
        {
            string name;
            return _objectScenes.TryGetValue((int)type, out name) ? Scene(name) : null;
        }

        public GodotEffectData Scene(string name)
        {
            GodotEffectData scene;
            return name != null && Scenes.TryGetValue(name, out scene) ? scene : null;
        }

        /// <summary>The Resources path of a texture key.</summary>
        public string TexturePath(string key)
        {
            string path;
            return key != null && TextureResources.TryGetValue(key, out path) ? path : null;
        }

        /// <summary><c>clampi(caliber, 40, 120) * 0.00012</c>.</summary>
        public float ShellPixelSize(int caliber)
        {
            return Mathf.Clamp(caliber, (int)ShellPixelCaliberMin, (int)ShellPixelCaliberMax) * ShellPixelFactor;
        }

        /// <summary><c>clampi(caliber, 20, 150) * 0.0075</c>.</summary>
        public float TrailScale(int caliber)
        {
            return Mathf.Clamp(caliber, (int)TrailCaliberMin, (int)TrailCaliberMax) * TrailScaleFactor;
        }

        public static CombatFxManifest Parse(string text)
        {
            var root = JsonNode.Parse(text);
            var manifest = new CombatFxManifest();

            foreach (var texture in root["textures"].Items)
            {
                var resource = texture["resource"].AsString();
                var key = resource.Substring(resource.LastIndexOf('/') + 1);
                manifest.TextureResources[key] = resource;
            }

            foreach (var field in root["scenes"].Fields)
            {
                manifest.Scenes[field.Key] = ReadScene(field.Key, field.Value);
            }

            var bindings = root["bindings"];
            ReadMap(bindings["shotScenes"], manifest._shotScenes);
            ReadMap(bindings["penetrationScenes"], manifest._penetrationScenes);
            ReadMap(bindings["objectScenes"], manifest._objectScenes);
            manifest.TrailColor = Vec4(bindings["trailColor"]);
            manifest.TrailColorHeat = Vec4(bindings["trailColorHeat"]);
            manifest.ShellModulate = Vec4(bindings["shellModulate"]);
            manifest.ShellModulateHeat = Vec4(bindings["shellModulateHeat"]);
            manifest.ShellTexture = bindings["shellTextureId"].AsString();
            manifest.ShellMipmaps = bindings["sprite"]["textureFilter"].AsInt() == 3;

            var trail = root["trail"];
            manifest.Trail = new GodotTrailData
            {
                Amount = trail["amount"].AsInt(),
                FixedFps = trail["fixedFps"].AsInt(),
                Texture = trail["textureId"].AsString(),
                Curve = GodotCurve.Read(trail["curve"]),
            };

            var code = root["code"];
            manifest.ShellPixelCaliberMin = code["shellPixelSize"]["caliberMin"].AsFloat();
            manifest.ShellPixelCaliberMax = code["shellPixelSize"]["caliberMax"].AsFloat();
            manifest.ShellPixelFactor = code["shellPixelSize"]["factor"].AsFloat();
            manifest.TrailCaliberMin = code["trailScale"]["caliberMin"].AsFloat();
            manifest.TrailCaliberMax = code["trailScale"]["caliberMax"].AsFloat();
            manifest.TrailScaleFactor = code["trailScale"]["factor"].AsFloat();
            manifest.RicochetTrailPixelFactor = code["ricochetTrailScale"]["pixelSizeFactor"].AsFloat();
            manifest.TrailAngleFirst = code["trailAngles"]["first"].AsFloat();
            manifest.TrailAngleSecond = (float)(code["trailAngles"]["secondTimesPi"].AsDouble() * Math.PI);
            manifest.TrailFreeDelay = code["trailFreeDelay"]["seconds"].AsFloat();
            manifest.RicochetTrailFreeDelay = code["ricochetTrailFreeDelay"]["seconds"].AsFloat();
            manifest.ImpactScaleFactor = code["impactScale"]["factor"].AsFloat();
            manifest.TrailLeadMin = code["trailLead"]["randomMin"].AsFloat();
            manifest.TrailLeadMax = code["trailLead"]["randomMax"].AsFloat();
            manifest.TrailLeadSpeedFactor = code["trailLead"]["speedFactor"].AsFloat();
            manifest.StarterAabbExtent = code["starterAabb"]["extent"].AsFloat();
            manifest.StarterSelfDestroyExtra = code["starterSelfDestroy"]["extraSeconds"].AsFloat();

            if (manifest.Trail.Amount < 2 || manifest.Trail.FixedFps <= 0 || manifest.Trail.Curve == null)
            {
                throw new FormatException("combat fx trail preset is incomplete");
            }

            return manifest;
        }

        private static void ReadMap(JsonNode node, Dictionary<int, string> map)
        {
            foreach (var field in node.Fields)
            {
                int key;
                if (!int.TryParse(field.Key, out key))
                {
                    throw new FormatException("combat fx binding key is not an enum value: " + field.Key);
                }

                map[key] = field.Value.AsString();
            }
        }

        private static GodotEffectData ReadScene(string name, JsonNode node)
        {
            var scene = new GodotEffectData
            {
                Name = name,
                Root = Xform(node["root"]["transform"]),
            };

            var emitters = node["emitters"];
            for (var i = 0; i < emitters.Count; i++)
            {
                scene.Emitters.Add(ReadEmitter(emitters[i], i));
            }

            return scene;
        }

        private static GodotEmitterData ReadEmitter(JsonNode e, int index)
        {
            var p = e["process"];
            var mesh = e["mesh"];
            var m = mesh["material"];
            var data = new GodotEmitterData
            {
                Name = e["name"].AsString(),
                Index = index,
                Visible = e["visible"].AsBool(true),
                Local = Xform(e["transform"]),
                Amount = e["amount"].AsInt(),
                AmountRatio = e["amountRatio"].AsFloat(1f),
                Lifetime = e["lifetime"].AsFloat(),
                OneShot = e["oneShot"].AsBool(),
                Preprocess = e["preprocess"].AsFloat(),
                SpeedScale = e["speedScale"].AsFloat(1f),
                Explosiveness = e["explosiveness"].AsFloat(),
                Randomness = e["randomness"].AsFloat(),
                FixedFps = e["fixedFps"].AsInt(),
                Interpolate = e["interpolate"].AsBool(),
                FractDelta = e["fractDelta"].AsBool(),
                LocalCoords = e["localCoords"].AsBool(),
                TransformAlign = e["transformAlign"].AsInt(),
                AlignChannel = e["transformAlignChannelFilter"].AsInt(),
                AlignAxis = e["transformAlignAxis"].AsInt(),
                UseFixedSeed = e["useFixedSeed"].AsBool(),
                Seed = (uint)e["seed"].AsLong(),
                SortingOffset = e["sortingOffset"].AsFloat(),

                Direction = Vec3(p["direction"]),
                Spread = p["spread"].AsFloat(),
                Flatness = p["flatness"].AsFloat(),
                VelocityMin = p["initialVelocityMin"].AsFloat(),
                VelocityMax = p["initialVelocityMax"].AsFloat(),
                AngularVelocityMin = p["angularVelocityMin"].AsFloat(),
                AngularVelocityMax = p["angularVelocityMax"].AsFloat(),
                DirectionalVelocityMin = p["directionalVelocityMin"].AsFloat(),
                DirectionalVelocityMax = p["directionalVelocityMax"].AsFloat(),
                RadialVelocityMin = p["radialVelocityMin"].AsFloat(),
                RadialVelocityMax = p["radialVelocityMax"].AsFloat(),
                OrbitVelocityMin = p["orbitVelocityMin"].AsFloat(),
                OrbitVelocityMax = p["orbitVelocityMax"].AsFloat(),
                Gravity = Vec3(p["gravity"]),
                LinearAccelMin = p["linearAccelMin"].AsFloat(),
                LinearAccelMax = p["linearAccelMax"].AsFloat(),
                RadialAccelMin = p["radialAccelMin"].AsFloat(),
                RadialAccelMax = p["radialAccelMax"].AsFloat(),
                TangentialAccelMin = p["tangentialAccelMin"].AsFloat(),
                TangentialAccelMax = p["tangentialAccelMax"].AsFloat(),
                DampingMin = p["dampingMin"].AsFloat(),
                DampingMax = p["dampingMax"].AsFloat(),
                AngleMin = p["angleMin"].AsFloat(),
                AngleMax = p["angleMax"].AsFloat(),
                ScaleMin = p["scaleMin"].AsFloat(),
                ScaleMax = p["scaleMax"].AsFloat(),
                LifetimeRandomness = p["lifetimeRandomness"].AsFloat(),
                AnimSpeedMin = p["animSpeedMin"].AsFloat(),
                AnimSpeedMax = p["animSpeedMax"].AsFloat(),
                AnimOffsetMin = p["animOffsetMin"].AsFloat(),
                AnimOffsetMax = p["animOffsetMax"].AsFloat(),
                HueMin = p["hueVariationMin"].AsFloat(),
                HueMax = p["hueVariationMax"].AsFloat(),
                EmissionShape = p["emissionShape"].AsInt(),
                EmissionOffset = Vec3(p["emissionShapeOffset"]),
                EmissionScale = Vec3(p["emissionShapeScale"]),
                EmissionRadius = p["emissionSphereRadius"].AsFloat(),
                VelocityPivot = Vec3(p["velocityPivot"]),
                AlignY = p["alignY"].AsBool(),
                RotateY = p["rotateY"].AsBool(),
                DampingAsFriction = p["dampingAsFriction"].AsBool(),
                Color = GodotFxMath.SrgbToLinear(Vec4(p["color"])),
                ScaleCurve = GodotCurve.Read(p.Has("scaleCurve") ? p["scaleCurve"] : null),
                AlphaCurve = GodotCurve.Read(p.Has("alphaCurve") ? p["alphaCurve"] : null),
                ColorRamp = GodotRamp.Read(p.Has("colorRamp") ? p["colorRamp"] : null),

                Size = Vec2(mesh["size"]),
                CenterOffset = Vec3(mesh["centerOffset"]),
                Orientation = mesh["orientation"].AsInt(),
                Material = new GodotParticleMaterial
                {
                    Transparent = m["transparency"].AsInt() != 0,
                    BlendAdd = m["blendMode"].AsInt() == 1,
                    Fog = !m["disableFog"].AsBool(),
                    VertexColor = m["vertexColorUseAsAlbedo"].AsBool(),
                    VertexColorSrgb = m["vertexColorIsSrgb"].AsBool(),
                    Albedo = GodotFxMath.SrgbToLinear(Vec4(m["albedoColor"])),
                    Texture = m["texture"].IsNull ? null : m["texture"].AsString(),
                    Mipmaps = m["textureFilter"].AsInt() == 3,
                    Repeat = m["textureRepeat"].AsBool(),
                    BillboardParticles = m["billboardMode"].AsInt() == 3,
                    HFrames = Math.Max(1, m["animHFrames"].AsInt(1)),
                    VFrames = Math.Max(1, m["animVFrames"].AsInt(1)),
                    AnimLoop = m["animLoop"].AsBool(),
                },
            };

            if (data.Amount <= 0 || data.Lifetime <= 0f)
            {
                throw new FormatException("combat fx emitter " + data.Name + " has no particles");
            }

            return data;
        }

        internal static GodotXform Xform(JsonNode node)
        {
            return new GodotXform(Vec3(node["x"]), Vec3(node["y"]), Vec3(node["z"]), Vec3(node["origin"]));
        }

        internal static float[] Floats(JsonNode node)
        {
            var values = new float[node.Count];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = node[i].AsFloat();
            }

            return values;
        }

        internal static Vector2 Vec2(JsonNode node)
        {
            return new Vector2(node[0].AsFloat(), node[1].AsFloat());
        }

        internal static Vector3 Vec3(JsonNode node)
        {
            return new Vector3(node[0].AsFloat(), node[1].AsFloat(), node[2].AsFloat());
        }

        internal static Vector4 Vec4(JsonNode node)
        {
            return new Vector4(node[0].AsFloat(), node[1].AsFloat(), node[2].AsFloat(), node[3].AsFloat());
        }
    }
}
