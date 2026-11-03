using System.Collections;
using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Maps;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Lighting;
using UnityEngine;
using UnityEngine.Rendering;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// The 3D half of a battle: landscape, sun, sky and spawn markers, all
    /// rebuilt from Tools/port_godot_map.py output rather than a scene file, so
    /// the numbers stay diffable against the source .tscn.
    /// </summary>
    public sealed class BattleWorld : MonoBehaviour
    {
        public const string DefaultMapId = "rudniki";

        /// <summary>
        /// The sky material is built at runtime, so nothing in the project
        /// references this shader and a player build is free to strip it.
        /// ProjectBootstrap keeps it in the build by name.
        /// </summary>
        public const string SkyShaderName = "Skybox/6 Sided";

        /// <summary>
        /// Rudniki does not use a cubemap in Godot: five transformed PlaneMesh
        /// nodes overlap around the map. Keeping those planes avoids the hard
        /// cyan edge produced when their oriented UVs are forced into six
        /// canonical Unity skybox slots.
        /// </summary>
        public const string SkyFaceShaderName = "BlitzLegacy/Map/Sky Face";

        /// <summary>
        /// The source terrain material is built from three textures and map
        /// parameters at runtime, so ProjectBootstrap must retain this shader.
        /// </summary>
        public const string LandscapeShaderName = "BlitzLegacy/Map/Landscape";

        /// <summary>The client's shadowColor, read by the ClientMap shaders.</summary>
        public static readonly int ClientShadowColorId = Shader.PropertyToID("_ClientShadowColor");

        /// <summary>The scene light's raw colour and ambient, as the lit
        /// client shaders read lightColor0 and lightAmbientColor0.</summary>
        public static readonly int ClientLightColorId = Shader.PropertyToID("_ClientLightColor");
        public static readonly int ClientAmbientColorId = Shader.PropertyToID("_ClientAmbientColor");

        /// <summary>
        /// The scene's only light, and the key the lighting manifest files it
        /// under. Direction, colour and energy still come from the source; Unity
        /// adds the realtime shadow quality deliberately requested for the port.
        /// The object atlas is excluded from battle rendering, so this light is
        /// the sole owner of both illumination and object shadows.
        /// </summary>
        public const string SunLightPath = "DirectionalLight3D";

        // The source opacity 0.24 was meant for a disabled Godot shadow. Unity's
        // soft directional shadow needs enough contrast to ground an object, but
        // remains translucent so black silhouettes never replace sand detail.
        public const float RealtimeShadowStrength = 0.58f;
        public const float RealtimeShadowBias = 0.03f;
        public const float RealtimeShadowNormalBias = 0.35f;
        public const float RealtimeShadowNearPlane = 0.2f;

        private readonly List<Material> _materials = new List<Material>();
        private readonly List<Mesh> _meshes = new List<Mesh>();

        public MapDefinition Map { get; private set; }
        public TerrainHeightField Heights { get; private set; }
        public GameObject Landscape { get; private set; }
        public GameObject Sky { get; private set; }
        public Light Sun { get; private set; }
        public Transform PlayerSpawn { get; private set; }
        public Transform BotSpawn { get; private set; }

        /// <summary>Every teamA and teamB marker, in scene order.</summary>
        public List<Transform> PlayerSpawns { get; private set; }

        public List<Transform> BotSpawns { get; private set; }

        /// <summary>True once <see cref="SwapSides"/> put the local team on teamB.</summary>
        public bool SidesSwapped { get; private set; }

        /// <summary>
        /// The Sandbox side choice (_battle.gd dice): the local team takes the
        /// teamB spawners and the opponents teamA. Logical team ids stay put.
        /// </summary>
        public void SwapSides()
        {
            var spawns = PlayerSpawns; PlayerSpawns = BotSpawns; BotSpawns = spawns;
            var spawn = PlayerSpawn; PlayerSpawn = BotSpawn; BotSpawn = spawn;
            SidesSwapped = !SidesSwapped;
        }

        /// <summary>The map's AIMarker nodes, as Battle.ai_markers keeps them.</summary>
        public List<BattleAiMarker> AiMarkers { get; private set; }
        public Bounds PlayArea { get; private set; }
        public BattleObjects Objects { get; private set; }

        /// <summary>The ported navmesh the bots plan their routes on.</summary>
        public BattleNavigation Navigation { get; private set; }

        /// <summary>
        /// Source environment and sun metadata. The battle intentionally does
        /// not load the atlas and unwrapped meshes stored beside it.
        /// </summary>
        public LightingDefinition Lighting { get; private set; }

        public static BattleWorld Create(Transform parent, string mapId)
        {
            BattleWorld result = null;
            var steps = CreateSteps(parent, mapId, built => result = built, false);
            while (steps.MoveNext())
            {
            }
            return result;
        }

        /// <summary>
        /// The same build, spread over frames. A 4.8 client map instantiates a
        /// 30-50 MB scene, and doing all of that in one frame is what Android
        /// reported as an ANR on the battle queue (REMASTERDELUXE-2). With
        /// allowAsync the map scene is pulled in with Resources.LoadAsync;
        /// callers that drain the steps in a single frame must leave it off.
        /// </summary>
        public static IEnumerator CreateSteps(
            Transform parent, string mapId, Action<BattleWorld> built,
            bool allowAsync = true)
        {
            var go = new GameObject("BattleWorld");
            go.transform.SetParent(parent, false);
            var world = go.AddComponent<BattleWorld>();
            var steps = world.BuildSteps(
                string.IsNullOrEmpty(mapId) ? DefaultMapId : mapId, allowAsync);
            while (steps.MoveNext())
            {
                yield return steps.Current;
            }
            if (built != null)
            {
                built(world);
            }
        }

        /// <summary>Reads a map table without touching the scene.</summary>
        public static MapDefinition LoadDefinition(string mapId)
        {
            return MapDefinition.Parse(BlitzResources.LoadText("map_" + mapId));
        }

        public static TerrainHeightField LoadHeights(MapDefinition map)
        {
            var asset = Resources.Load<TextAsset>(map.Terrain.Resource);
            if (asset == null)
            {
                Debug.LogError("[Blitz] missing terrain: " + map.Terrain.Resource);
                return null;
            }

            return TerrainHeightField.Parse(asset.bytes);
        }

        private IEnumerator BuildSteps(string mapId, bool allowAsync)
        {
            Map = LoadDefinition(mapId);
            yield return null;
            Heights = LoadHeights(Map);
            yield return null;
            // The 4.8 client maps carry their sky, sun and fog in their own
            // table, so no Godot lighting manifest exists or is wanted for
            // them and asking for one logged an error on every battle load.
            // The Godot ports keep that data in the manifest instead, so a
            // missing one there is still worth reporting.
            Lighting = Map.Environment == null
                ? BakedLighting.LoadDefinition(Map.Id)
                : null;
            ApplyEnvironment();
            yield return null;
            BuildLandscape();
            yield return null;
            BuildNavigation();
            yield return null;
            var objectSteps = BuildObjectSteps(allowAsync);
            while (objectSteps.MoveNext())
            {
                yield return objectSteps.Current;
            }
            yield return null;
            BuildSun();
            BuildSpawns();
            BuildAiMarkers();

            Debug.Log(
                "[Blitz] battle world built: map=" + Map.Id +
                " terrain=" + (Heights == null ? 0 : Heights.TriangleCount) + " tris" +
                " objects=" + (Objects == null ? 0 : Objects.InstanceCount) +
                " renderers=" + (Objects == null ? 0 : Objects.RendererCount) +
                " lightmapped=" + (Objects == null ? 0 : Objects.LightmappedCount) +
                " colliders=" + (Objects == null ? 0 : Objects.ColliderCount) +
                " trees=" + (Objects == null ? 0 : Objects.TreeCount) +
                " bushes=" + (Objects == null ? 0 : Objects.VegetationCount) +
                " borderPanels=" + (Objects == null ? 0 : Objects.BorderPanelCount) +
                " spawns=" + Map.TeamA.Count + "/" + Map.TeamB.Count +
                " aiMarkers=" + (AiMarkers == null ? 0 : AiMarkers.Count) +
                " area=" + PlayArea.size.ToString("F1"));
        }

        /// <summary>
        /// Ambient radiance of a ported map, taken from the Godot environment
        /// of Rudniki (ambientLightColor 0.3716 grey). A client scene only
        /// authors an ambient *colour* on its light node - DAVA multiplies it
        /// by terms Unity has no equivalent for - so the port keeps the hue
        /// and reuses the scale the sibling maps already ship with.
        /// </summary>
        public const float ClientAmbientLuminance = 0.371622f;

        /// <summary>
        /// Unity's linear fog always reaches full opacity at its far distance,
        /// while DAVA stops at `fogLimit`. Pushing Unity's far distance out by
        /// that ratio reproduces the authored curve over the authored range
        /// instead of swallowing the far side of the map.
        /// </summary>
        public static float LinearFogEnd(float start, float end, float limit)
        {
            if (limit <= 0f || limit >= 1f)
            {
                return end;
            }

            return (end - ((1f - limit) * start)) / limit;
        }

        /// <summary>The fog and ambient authored on the client global material.</summary>
        private void ApplyClientEnvironment()
        {
            var environment = Map.Environment;
            RenderSettings.ambientMode = AmbientMode.Flat;
            var light = Map.Light != null && Map.Light.Color != null ? Map.Light.Color : new[] { 1f, 1f, 1f };
            Shader.SetGlobalVector(ClientLightColorId, new Vector4(light[0], light[1], light[2], 1f));
            var ambient = environment != null && environment.Ambient != null
                ? environment.Ambient : new[] { 0f, 0f, 0f };
            Shader.SetGlobalVector(ClientAmbientColorId, new Vector4(ambient[0], ambient[1], ambient[2], 1f));
            if (environment == null)
            {
                RenderSettings.ambientLight = new Color(
                    ClientAmbientLuminance, ClientAmbientLuminance,
                    ClientAmbientLuminance, 1f);
                RenderSettings.fog = false;
                Shader.SetGlobalVector(SourceEnvironment.FogRangeId, Vector4.zero);
                Shader.SetGlobalVector(SourceEnvironment.FogSkyId, Vector4.zero);
                SourceEnvironment.ClearClientFog();
                Shader.SetGlobalVector(ClientShadowColorId, Vector4.zero);
                return;
            }

            // ShadowRect of a client-shaded map: what a shadow volume
            // multiplies the frame by. Zero alpha leaves the shaders alone.
            var shadowColour = environment.ShadowColor;
            Shader.SetGlobalVector(ClientShadowColorId, shadowColour == null
                ? Vector4.zero
                : new Vector4(shadowColour[0], shadowColour[1], shadowColour[2], 1f));

            var hue = new Color(
                environment.Ambient[0], environment.Ambient[1],
                environment.Ambient[2], 1f);
            var luminance = Mathf.Max(
                (0.2126f * hue.r) + (0.7152f * hue.g) + (0.0722f * hue.b), 0.0001f);
            var target = environment.AmbientLuminance > 0f
                ? environment.AmbientLuminance
                : ClientAmbientLuminance;
            var scale = target / luminance;
            RenderSettings.ambientLight = new Color(
                hue.r * scale, hue.g * scale, hue.b * scale, 1f);

            var fog = new Color(
                environment.FogColor[0], environment.FogColor[1],
                environment.FogColor[2], 1f);

            // Unity's own linear fog is what everything outside the ported
            // shaders reads - the tank draws with URP Lit - and it always
            // reaches pure fog colour at its far distance, so its range is
            // stretched to land on the authored limit instead.
            RenderSettings.fog = true;
            RenderSettings.fogColor = fog;
            if (environment.FogLinear)
            {
                RenderSettings.fogMode = FogMode.Linear;
                RenderSettings.fogStartDistance = environment.FogStart;
                RenderSettings.fogEndDistance = LinearFogEnd(
                    environment.FogStart, environment.FogEnd, environment.FogLimit);
            }
            else
            {
                // FOG_LINEAR off: 1 - exp(-density * distance), which is
                // Unity's own exponential fog up to the authored limit.
                RenderSettings.fogMode = FogMode.Exponential;
                RenderSettings.fogDensity = environment.FogDensity;
            }

            // The terrain and the props read the authored curve itself, cap
            // and all, through BlitzFog.hlsl. Without this the ground kept no
            // fog at all while the buildings behind it washed out to white.
            Shader.SetGlobalVector(
                SourceEnvironment.FogColourId, new Vector4(fog.r, fog.g, fog.b, 1f));
            Shader.SetGlobalVector(SourceEnvironment.FogRangeId, new Vector4(
                environment.FogStart,
                environment.FogEnd,
                environment.FogLimit,
                environment.FogLinear ? 1f : 2f));
            Shader.SetGlobalVector("_BlitzFogDepth", new Vector4(
                0f, 0f, environment.FogLinear ? 0f : environment.FogDensity, 0f));
            ApplyClientFogTerms(environment, Map.ClientScene);

            if (!environment.FogAtmosphere)
            {
                Shader.SetGlobalVector(SourceEnvironment.FogSkyId, Vector4.zero);
                GraphicsQuality.ApplyFog();
                return;
            }

            // FOG_ATMOSPHERE: the haze picks up the sky colour away from the
            // sun and the sun colour towards it, which is what the client's
            // own loading art shows and Unity's flat fog colour cannot.
            Shader.SetGlobalVector(SourceEnvironment.FogSkyId, new Vector4(
                environment.FogSkyColor[0], environment.FogSkyColor[1],
                environment.FogSkyColor[2], 1f));
            Shader.SetGlobalVector(SourceEnvironment.FogSunId, new Vector4(
                environment.FogSunColor[0], environment.FogSunColor[1],
                environment.FogSunColor[2], environment.FogScattering));
            Shader.SetGlobalVector(SourceEnvironment.FogSunDirectionId, new Vector4(
                environment.SunDirection[0], environment.SunDirection[1],
                environment.SunDirection[2], environment.FogAtmosphereDistance));

            // The 4.8 client maps push their own fog curve here instead of
            // going through SourceEnvironment, so the graphics tab's fog row
            // has to be re-applied on this path as well.
            GraphicsQuality.ApplyFog();
        }

        /// <summary>
        /// The parts of vp-fog-math.slh only a map drawn with the client's own
        /// formulas reads: the exact atmosphere angle and FOG_HALFSPACE.
        /// </summary>
        public static void ApplyClientFogTerms(MapEnvironmentInfo environment, bool clientScene)
        {
            if (environment == null || !clientScene)
            {
                SourceEnvironment.ClearClientFog();
                return;
            }

            var halfspace = environment.FogHalfspace;
            Shader.SetGlobalVector(SourceEnvironment.FogHalfspaceId, halfspace == null
                ? Vector4.zero
                : new Vector4(halfspace.Density, halfspace.Falloff, halfspace.Height, halfspace.Limit));
            Shader.SetGlobalVector(SourceEnvironment.FogClientId, new Vector4(
                1f,
                halfspace != null ? 1f : 0f,
                halfspace != null && halfspace.Linear ? 1f : 0f,
                0f));
        }

        private void ApplyEnvironment()
        {
            if (Lighting == null)
            {
                // The old 4.8 map carries its sky, sun and fog in the .sc2
                // scene, not in the Godot lighting manifest used by newer
                // ports, so the manifest's own environment block drives this.
                ApplyClientEnvironment();
                if (Map.ClientScene)
                {
                    // The binary client scene draws its own dome at the far
                    // plane. URP's skybox pass fills every pixel still at the
                    // far depth and painted over it, so there is none here.
                    RenderSettings.skybox = null;
                }

                BuildSkyFaces();
                DynamicGI.UpdateEnvironment();
                return;
            }

            var source = Lighting.Environment;

            // The ambient term and the fog are read out of the manifest the
            // same way in both scenes, so the hangar and the map now share one
            // implementation instead of two that can drift apart.
            SourceEnvironment.Apply(source);

            if (Map.GodotScene)
            {
                // The render scene carries its five sky planes as nodes and
                // shades itself (GodotMap.hlsl), so only its globals go out.
                RenderSettings.skybox = null;
                ApplyGodotLighting(source);
                DynamicGI.UpdateEnvironment();
                GraphicsQuality.Apply();
                return;
            }

            var shader = Shader.Find(SkyShaderName);
            if (shader == null)
            {
                throw new InvalidOperationException(
                    "required sky shader is unavailable: " + SkyShaderName);
            }

            var sky = new Material(shader) { name = Map.Id + "Sky" };
            _materials.Add(sky);
            for (var i = 0; i < Map.SkyFaces.Count; i++)
            {
                var face = Map.SkyFaces[i];
                var texture = Resources.Load<Texture2D>(face.Resource);
                if (texture == null)
                {
                    throw new InvalidOperationException(
                        "required sky face is unavailable: " + face.Resource);
                }

                sky.SetTexture(SkyProperty(face.Slot), texture);
            }
            sky.SetColor("_Tint", Color.white);
            sky.SetFloat("_Exposure", 1f);
            sky.SetFloat("_Rotation", 0f);
            RenderSettings.skybox = sky;
            BuildSkyFaces();

            // The sky lands after the ambient and the fog above, so the
            // environment is pushed once more here. A player build never
            // refreshes the ambient probe on its own: flat ambient and the sky
            // only reach the shaders once it is pushed, which is why
            // HangarRenderCli does the same before every capture. Without it
            // the device lights the map by the sun alone and reads as unlit.
            DynamicGI.UpdateEnvironment();

            // The map's own sun and its fog are in place now, so the graphics
            // tab gets the last word on both.
            GraphicsQuality.Apply();
        }

        public static readonly int GodotAmbientId = Shader.PropertyToID("_GodotAmbient");
        public static readonly int GodotLightDirectionId = Shader.PropertyToID("_GodotLightDirection");
        public static readonly int GodotLightColorId = Shader.PropertyToID("_GodotLightColor");

        /// <summary>
        /// The terms GodotMap.hlsl reads: the background ambient a surface
        /// outside the lightmap gets, and the sun the vertex Lambert uses.
        /// Godot decodes the light colour from sRGB and folds the exposure in.
        /// </summary>
        private void ApplyGodotLighting(LightingEnvironment source)
        {
            var ambient = source.AmbientRgb();
            var exposure = source.TonemapExposure;
            Shader.SetGlobalVector(GodotAmbientId, new Vector4(
                ambient[0] * exposure, ambient[1] * exposure, ambient[2] * exposure, 1f));

            var light = Lighting.Light(SunLightPath);
            var basis = light != null ? light.Basis : Map.Light.Basis;
            var colour = light != null ? light.Color : Map.Light.Color;
            var energy = (light != null ? light.Energy : Map.Light.Energy) * exposure;
            // Basis rows as the porter writes them; the light shines along
            // -Z, so +Z (third column) points at the sun. Godot z -> -z.
            var towards = new Vector3(basis[2], basis[5], -basis[8]).normalized;
            Shader.SetGlobalVector(GodotLightDirectionId, new Vector4(towards.x, towards.y, towards.z, 0f));
            Shader.SetGlobalVector(GodotLightColorId, new Vector4(
                LightingEnvironment.SourceChannel(colour[0]) * energy,
                LightingEnvironment.SourceChannel(colour[1]) * energy,
                LightingEnvironment.SourceChannel(colour[2]) * energy, 1f));
        }

        private static string SkyProperty(string slot)
        {
            if (string.Equals(slot, "front", StringComparison.Ordinal)) return "_FrontTex";
            if (string.Equals(slot, "back", StringComparison.Ordinal)) return "_BackTex";
            if (string.Equals(slot, "left", StringComparison.Ordinal)) return "_LeftTex";
            if (string.Equals(slot, "right", StringComparison.Ordinal)) return "_RightTex";
            if (string.Equals(slot, "up", StringComparison.Ordinal)) return "_UpTex";
            throw new ArgumentException("unsupported sky slot '" + slot + "'");
        }

        private void BuildSkyFaces()
        {
            var shader = Shader.Find(SkyFaceShaderName);
            if (shader == null)
            {
                throw new InvalidOperationException(
                    "required sky face shader is unavailable: " + SkyFaceShaderName);
            }

            Sky = new GameObject("SkyFaces");
            Sky.transform.SetParent(transform, false);
            for (var i = 0; i < Map.SkyFaces.Count; i++)
            {
                var source = Map.SkyFaces[i];
                var texture = Resources.Load<Texture2D>(source.Resource);
                if (texture == null)
                {
                    throw new InvalidOperationException(
                        "required sky face is unavailable: " + source.Resource);
                }

                var face = new GameObject(source.Name);
                face.transform.SetParent(Sky.transform, false);
                var mesh = BuildSkyFaceMesh(source);
                _meshes.Add(mesh);
                face.AddComponent<MeshFilter>().sharedMesh = mesh;

                var material = new Material(shader)
                {
                    name = Map.Id + "Sky_" + source.Name,
                };
                material.SetTexture("_MainTex", texture);
                _materials.Add(material);

                var renderer = face.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
        }

        /// <summary>
        /// Rebuilds Godot's default 2x2 PlaneMesh after applying its composed
        /// Transform3D. Vertices, not a decomposed Unity Transform, carry the
        /// basis so reflected axes and the intentional 0.1% face overlap remain
        /// exact. The final Z mirror is the same one used by all map geometry.
        /// </summary>
        public static Mesh BuildSkyFaceMesh(MapSkyFace face)
        {
            if (face == null || face.Basis == null || face.Basis.Length != 9 ||
                face.Origin == null || face.Origin.Length != 3)
            {
                throw new ArgumentException("sky face transform is invalid");
            }

            var vertices = new[]
            {
                SkyPoint(face, -1f, -1f),
                SkyPoint(face, 1f, -1f),
                SkyPoint(face, 1f, 1f),
                SkyPoint(face, -1f, 1f),
            };
            var mesh = new Mesh { name = "sky_" + face.Name };
            mesh.vertices = vertices;
            // Godot's UV origin is top-left, while Unity samples imported
            // textures from bottom-left. Flipping V is essential here: without
            // it the eight authored cube edges miss by 11.4 RGB levels on
            // average and beige horizon bands meet the cloud ceiling.
            mesh.uv = new[]
            {
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(1f, 0f),
                new Vector2(0f, 0f),
            };
            // Mirroring source Z reverses the source winding.
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Vector3 SkyPoint(MapSkyFace face, float x, float z)
        {
            var basis = face.Basis;
            var origin = face.Origin;
            var sourceX = origin[0] + basis[0] * x + basis[2] * z;
            var sourceY = origin[1] + basis[3] * x + basis[5] * z;
            var sourceZ = origin[2] + basis[6] * x + basis[8] * z;
            return new Vector3(sourceX, sourceY, -sourceZ);
        }

        /// <summary>
        /// Rebuilds the navmesh the source bakes into the map scene. Without
        /// it the bots have no walkable surface to plan on, and a goal in the
        /// lake or behind a quarry wall looks as good as any other.
        /// </summary>
        private void BuildNavigation()
        {
            Navigation = BattleNavigation.Create(transform, Map.Id);
        }

        private void BuildLandscape()
        {
            if (Heights == null)
            {
                return;
            }

            // The HTerrain node carries no transform, so the drivable surface
            // sits at the origin. terrain_FullMesh hangs 2.545 metres lower,
            // but that copy only exists to bake the navmesh.
            Landscape = new GameObject("Landscape");
            Landscape.layer = BattleLayers.Static;
            Landscape.transform.SetParent(transform, false);

            // BattleTerrain always builds the grid outwards from its first
            // sample. A Godot map authored its landscape at the origin, but a
            // client DAVA landscape is centred on its bounding box, so the
            // authored corner has to move the object instead of the mesh.
            if (Map.Terrain.Origin != null)
            {
                var corner = GodotSpace.Point(Map.Terrain.Origin);
                Landscape.transform.localPosition =
                    new Vector3(corner.X, corner.Y, corner.Z);
            }

            var mesh = BattleTerrain.BuildMesh(Heights, Map.Id + "Terrain");
            Landscape.AddComponent<MeshFilter>().sharedMesh = mesh;

            var renderer = Landscape.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = CreateLandscapeMaterial();
            // The source sets cast_shadow = 0 on the landscape.
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = true;

            if (Map.Terrain.CollisionEnabled)
            {
                Landscape.AddComponent<MeshCollider>().sharedMesh = mesh;
            }
        }

        private Material CreateLandscapeMaterial()
        {
            var material = CreateLandscapeMaterial(Map);
            _materials.Add(material);
            return material;
        }

        /// <summary>
        /// Rebuilds the source landscape material without a scene asset. The
        /// Godot shader combines the global colour map with an RGBA detail
        /// texture repeated 48 times and selects its four channels through the
        /// tile mask. Omitting that second scale is what made the Unity ground
        /// look like one stretched, blurry image.
        /// </summary>
        public static Material CreateLandscapeMaterial(MapDefinition map)
        {
            if (map == null || map.Terrain == null)
            {
                throw new ArgumentNullException("map");
            }

            var terrain = map.Terrain;
            if (terrain.Client != null)
            {
                return CreateClientLandscapeMaterial(map);
            }
            if (terrain.Godot != null)
            {
                return CreateGodotLandscapeMaterial(map);
            }

            var shader = Shader.Find(LandscapeShaderName);
            if (shader == null)
            {
                throw new InvalidOperationException(
                    "required landscape shader is unavailable: " + LandscapeShaderName);
            }

            var colour = LoadLandscapeTexture(terrain.ColorMapResource);
            var mask = LoadLandscapeTexture(terrain.TileMaskResource);
            var detail = LoadLandscapeTexture(terrain.TileTextureResource);
            var tileColours = new[]
            {
                terrain.TileColour0,
                terrain.TileColour1,
                terrain.TileColour2,
                terrain.TileColour3,
            };
            for (var i = 0; i < tileColours.Length; i++)
            {
                if (tileColours[i] == null || tileColours[i].Length != 4)
                {
                    throw new InvalidOperationException(
                        "terrain tile colour " + i + " is invalid");
                }
            }

            var material = new Material(shader) { name = map.Id + "Landscape" };
            material.SetTexture("_ColorMap", colour);
            material.SetTexture("_TileMask", mask);
            material.SetTexture("_TileTexture", detail);
            material.SetFloat("_TileTexScale", terrain.TileTexScale);
            for (var i = 0; i < tileColours.Length; i++)
            {
                var source = tileColours[i];
                material.SetColor("_TileColor" + i, new Color(
                    source[0], source[1], source[2], source[3]));
            }
            material.SetFloat("_DetailStrength", terrain.DetailStrength);
            material.SetFloat("_DetailContrast", terrain.DetailContrast);
            material.SetFloat("_Brightness", terrain.Brightness);
            material.SetFloat("_Contrast", terrain.Contrast);
            material.SetFloat("_MaskSharpness", terrain.MaskSharpness);
            return material;
        }

        /// <summary>landscape.gdshader with the hterrain maps the porter dumped.</summary>
        private static Material CreateGodotLandscapeMaterial(MapDefinition map)
        {
            var shader = Shader.Find(ClientMapScene.GodotLandscapeShader);
            if (shader == null)
            {
                throw new InvalidOperationException(
                    "required landscape shader is unavailable: " + ClientMapScene.GodotLandscapeShader);
            }

            var terrain = map.Terrain;
            var godot = terrain.Godot;
            var material = new Material(shader) { name = map.Id + "Landscape" };
            material.SetTexture("_ColorMap", LoadLandscapeTexture(terrain.ColorMapResource));
            material.SetTexture("_SplatMap", LoadLandscapeTexture(terrain.TileMaskResource));
            material.SetTexture("_NormalMap", LoadLandscapeTexture(terrain.NormalMapResource));
            material.SetTexture("_TileTexture", LoadLandscapeTexture(terrain.TileTextureResource));
            if (godot.TileColours == null || godot.TileColours.Length != 4)
            {
                throw new InvalidOperationException("the Godot landscape needs four tile colours");
            }
            for (var i = 0; i < 4; i++)
            {
                var c = godot.TileColours[i];
                material.SetVector("_TileColor" + i, new Vector4(c[0], c[1], c[2], c[3]));
            }
            material.SetVector("_Terrain", new Vector4(
                godot.MapScale[0], godot.MapScale[2], godot.Resolution, 0f));
            material.SetVector("_Detail", new Vector4(
                godot.TileTexScale, godot.DetailStrength, godot.DetailContrast, godot.MaskSharpness));
            material.SetVector("_Grade", new Vector4(godot.Brightness, godot.Contrast, 0f, 0f));
            material.SetFloat("_Fog", 1f);
            return material;
        }

        /// <summary>
        /// TileMaskSpecular with the landscape material's own values: the
        /// client formula, not the Godot HTerrain blend of the other maps.
        /// </summary>
        private static Material CreateClientLandscapeMaterial(MapDefinition map)
        {
            var shader = Shader.Find(ClientMapScene.LandscapeShader);
            if (shader == null)
            {
                throw new InvalidOperationException(
                    "required landscape shader is unavailable: " + ClientMapScene.LandscapeShader);
            }

            var terrain = map.Terrain;
            var client = terrain.Client;
            var material = new Material(shader) { name = map.Id + "Landscape" };
            material.SetTexture("_ColorMap", LoadLandscapeTexture(terrain.ColorMapResource));
            material.SetTexture("_TileMask", LoadLandscapeTexture(terrain.TileMaskResource));
            material.SetTexture("_TileTexture", LoadLandscapeTexture(terrain.TileTextureResource));
            material.SetVector("_Tiling", new Vector4(client.Tiling[0], client.Tiling[1], 0f, 0f));
            var colours = new[] { terrain.TileColour0, terrain.TileColour1, terrain.TileColour2, terrain.TileColour3 };
            for (var i = 0; i < colours.Length; i++)
            {
                if (colours[i] == null || colours[i].Length != 4)
                {
                    throw new InvalidOperationException("terrain tile colour " + i + " is invalid");
                }
                material.SetVector("_TileColor" + i, new Vector4(
                    colours[i][0], colours[i][1], colours[i][2], colours[i][3]));
            }
            material.SetVector("_Specular", new Vector4(
                client.Glossiness, client.Specularity, client.MetalFresnelReflectance, 0f));
            var light = map.Light != null && map.Light.Color != null ? map.Light.Color : new[] { 1f, 1f, 1f };
            material.SetVector("_LightColor", new Vector4(light[0], light[1], light[2], 1f));
            // 11.20 keeps the baked light in the colour map's alpha and
            // draws the ground without a highlight.
            material.SetVector("_LandscapeOptions", new Vector4(
                client.SeparateLightmapChannel ? 1f : 0f, client.Specular ? 1f : 0f, 0f, 0f));
            return material;
        }

        private static Texture2D LoadLandscapeTexture(string resource)
        {
            var texture = Resources.Load<Texture2D>(resource);
            if (texture == null)
            {
                throw new InvalidOperationException(
                    "required landscape texture is unavailable: " + resource);
            }

            return texture;
        }

        /// <summary>
        /// The source hangs 635 props off a single node. Ordinary objects port
        /// as shared meshes; the 103 trees and 76 bush groups keep their nested
        /// visual parts, shared billboard clusters and primitive colliders.
        /// </summary>
        /// <summary>
        /// The client maps ship one big scene prefab, so it is requested with
        /// Resources.LoadAsync while the loading overlay animates and only
        /// instantiated once the request is done.
        /// </summary>
        private IEnumerator BuildObjectSteps(bool allowAsync)
        {
            if (!allowAsync || string.IsNullOrEmpty(Map.ExternalScene))
            {
                BuildObjects();
                yield break;
            }

            // The client scene is a payload rather than a prefab; streaming
            // its 50 MB of vertices in is what the overlay waits for.
            var request = Map.ClientScene || Map.GodotScene
                ? Resources.LoadAsync<TextAsset>(Map.ExternalScene + "_meshes")
                : Resources.LoadAsync<GameObject>(Map.ExternalScene);
            while (!request.isDone)
            {
                yield return null;
            }

            BuildObjects();
        }

        private void BuildObjects()
        {
            BuildObjectsCore();
            BuildWater();
        }

        /// <summary>
        /// The water surfaces of the map as a gameplay field: buoyancy, drag
        /// and the drowning countdown of <see cref="TankWaterPhysics"/>
        /// sample it. Built once the drawn objects exist.
        /// </summary>
        private void BuildWater()
        {
            if (Water != null) { BattleWater.Release(Water); }
            Water = BattleWater.Build(Objects != null && Objects.Root != null ? Objects.Root.transform : transform);
            BattleWater.SetActive(Water);
            Debug.Log(Water == null
                ? "[Blitz] battle water: none on " + Map.Id
                : "[Blitz] battle water: map=" + Map.Id + " renderers=" + Water.RendererCount +
                  " triangles=" + Water.TriangleCount);
        }

        /// <summary>The map's water field, or null when the map has none.</summary>
        public BattleWater Water { get; private set; }

        private void BuildObjectsCore()
        {
            if (!string.IsNullOrEmpty(Map.ExternalScene) && !Map.GodotScene)
            {
                Objects = BattleObjects.BuildExternal(
                    transform, Map.ExternalScene, Map.ExternalDestructibles,
                    Map.ExternalTrees);
                BuildVegetation();
                return;
            }

            var table = BattleObjects.LoadTable(Map.Id);
            if (table == null)
            {
                return;
            }

            var meshes = BattleObjects.LoadMeshes(table);
            var shapes = BattleObjects.LoadShapes(table);
            if (meshes == null || shapes == null)
            {
                return;
            }

            // Keep the manifest for sky/fog/sun data, but do not bind its
            // object atlas: realtime lighting and that bake contain the same sun
            // in different places and would otherwise double every dark edge.
            // A Godot render scene draws the map as Godot does, LightmapGI
            // and all; the table then only brings the gameplay bodies.
            Objects = BattleObjects.Build(
                transform, table, meshes, shapes, null, useBakedLighting: false,
                scene: Map.GodotScene ? Map.ExternalScene : null);
        }

        /// <summary>
        /// The generated clusters of an 11.20 scene: drawn over the landscape
        /// the battle just built, so they need its heights.
        /// </summary>
        private void BuildVegetation()
        {
            var scene = Objects == null || Objects.Root == null
                ? null : Objects.Root.GetComponent<ClientMapScene>();
            if (scene == null || !ClientMapVegetation.Present(scene.Layout.vegetation))
            {
                return;
            }

            Vegetation = ClientMapVegetation.Build(
                scene.transform, scene.Layout.vegetation, Heights,
                Map.Terrain == null ? null : Map.Terrain.Origin);
            Vegetation.gameObject.layer = BattleLayers.Static;
        }

        /// <summary>The 11.20 scene's vegetation, if it has one.</summary>
        public ClientMapVegetation Vegetation { get; private set; }

        private void BuildSun()
        {
            var source = Lighting == null ? null : Lighting.Light(SunLightPath);
            var go = new GameObject("Sun");
            go.transform.SetParent(transform, false);
            Place(
                go.transform,
                source == null ? Map.Light.Basis : source.Basis,
                source == null ? Map.Light.Origin : source.Origin);

            Sun = go.AddComponent<Light>();
            Sun.type = LightType.Directional;
            var colour = source == null ? Map.Light.Color : source.Color;
            Sun.color = SourceColour(
                new Color(colour[0], colour[1], colour[2], 1f));

            // Keep the authored energy undimmed. Terrain, props and foliage now
            // share this one source instead of mixing realtime and baked units.
            Sun.intensity = source == null ? Map.Light.Energy : source.Energy;

            // Unity now lights the complete battle consistently. Static props,
            // destructibles, foliage and tanks all enter this one shadow map;
            // the old lightmap is not sampled by the battle, so no second set
            // of baked shadows can remain as ownerless stains.
            // Rudniki's Godot sun has shadow_enabled off: every shadow on the
            // map is in the lightmap, so the ported scene casts none either.
            // An 11.20 map without shadowMapsAvailable draws none either.
            var shadowless = Map.GodotScene || Map.Light != null && !Map.Light.Shadows;
            if (shadowless)
            {
                go.AddComponent<BakedSun>();
            }
            Sun.shadows = shadowless
                ? LightShadows.None
                : LightShadows.Soft;
            Sun.shadowStrength = RealtimeShadowStrength;
            Sun.shadowBias = RealtimeShadowBias;
            Sun.shadowNormalBias = RealtimeShadowNormalBias;
            Sun.shadowNearPlane = RealtimeShadowNearPlane;

            // The device build renders through Mobile_RPAsset while the editor
            // renders through PC_RPAsset, so the lighting a run really got has
            // to be readable in logcat instead of inferred from a screenshot.
            Debug.Log(
                "[Blitz] battle lighting: sun=" + Sun.intensity.ToString("F2") +
                " shadows=" + Sun.shadows +
                " ambient=" + RenderSettings.ambientLight.ToString("F3") +
                " sky=" + (RenderSettings.skybox != null ? "yes" : "no") +
                " quality=" + QualitySettings.names[QualitySettings.GetQualityLevel()] +
                " pipeline=" + (GraphicsSettings.currentRenderPipeline != null
                    ? GraphicsSettings.currentRenderPipeline.name
                    : "builtin"));
        }

        private void BuildSpawns()
        {
            var root = new GameObject("Spawns");
            root.transform.SetParent(transform, false);

            PlayerSpawns = BuildTeam(root.transform, "TeamA", Map.TeamA);
            BotSpawns = BuildTeam(root.transform, "TeamB", Map.TeamB);
            PlayerSpawn = PlayerSpawns.Count > 0 ? PlayerSpawns[0] : null;
            BotSpawn = BotSpawns.Count > 0 ? BotSpawns[0] : null;

            var min = GodotSpace.Point(Map.BorderMin);
            var max = GodotSpace.Point(Map.BorderMax);
            var low = new Vector3(min.X, min.Y, min.Z);
            var high = new Vector3(max.X, max.Y, max.Z);
            var area = new Bounds();
            area.SetMinMax(Vector3.Min(low, high), Vector3.Max(low, high));
            PlayArea = area;
        }

        /// <summary>
        /// The source picks a random slot per battle. Every marker is built
        /// here and handed to BattleSession, which seats its four tanks on the
        /// leading markers of each side so a run stays reproducible.
        /// </summary>
        private static List<Transform> BuildTeam(
            Transform parent, string name, List<MapSpawnPoint> spawns)
        {
            var root = new GameObject(name);
            root.transform.SetParent(parent, false);

            var markers = new List<Transform>();
            for (var i = 0; i < spawns.Count; i++)
            {
                var marker = new GameObject(spawns[i].Name);
                marker.transform.SetParent(root.transform, false);
                Place(marker.transform, spawns[i].Basis, spawns[i].Origin);
                markers.Add(marker.transform);
            }

            return markers;
        }

        /// <summary>
        /// ai_marker.gd pushes itself into Battle.ai_markers on _ready, so the
        /// world builds the same list once the map is up.
        /// </summary>
        private void BuildAiMarkers()
        {
            AiMarkers = new List<BattleAiMarker>();
            if (Map.AiMarkers.Count == 0)
            {
                return;
            }

            var root = new GameObject("AiMarkers");
            root.transform.SetParent(transform, false);
            for (var i = 0; i < Map.AiMarkers.Count; i++)
            {
                AiMarkers.Add(BattleAiMarker.Create(root.transform, Map.AiMarkers[i]));
            }
        }

        /// <summary>Places a transform from a source basis and origin.</summary>
        public static void Place(Transform target, float[] basis, float[] origin)
        {
            var point = GodotSpace.Point(origin);
            target.localPosition = new Vector3(point.X, point.Y, point.Z);

            Vec3 right, up, forward;
            GodotSpace.Basis(basis, out right, out up, out forward);
            target.localRotation = Quaternion.LookRotation(
                new Vector3(forward.X, forward.Y, forward.Z),
                new Vector3(up.X, up.Y, up.Z));
        }

        /// <summary>
        /// Godot stores resource colours as linear values while Unity reads
        /// Color fields as sRGB, exactly as HangarWorld documents.
        /// </summary>
        internal static Color SourceColour(Color linear)
        {
            // One encode for both scenes; the alpha is reset because these
            // colours land in fields Unity also reads an opacity from.
            var result = SourceEnvironment.SourceColour(linear);
            result.a = 1f;
            return result;
        }

        private void OnDestroy()
        {
            if (Objects != null)
            {
                Objects.Release();
                Objects = null;
            }

            if (Water != null)
            {
                BattleWater.Release(Water);
                Water = null;
            }

            Lighting = null;

            for (var i = 0; i < _materials.Count; i++)
            {
                if (_materials[i] != null)
                {
                    Destroy(_materials[i]);
                }
            }

            _materials.Clear();

            for (var i = 0; i < _meshes.Count; i++)
            {
                if (_meshes[i] != null)
                {
                    Destroy(_meshes[i]);
                }
            }

            _meshes.Clear();
        }
    }
}
