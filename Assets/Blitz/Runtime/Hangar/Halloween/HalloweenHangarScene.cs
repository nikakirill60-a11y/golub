// A ported 11.x hangar (see PortedHangar: the WoT Blitz 11.21 Halloween hangar
// hangar_halloween_2026, the Miku hangar hangar_miku_void), built at runtime
// from its export under Resources: scene graph, meshes, the client's materials
// on the ported shaders, legacy animations, particle effects, the random state
// switchers, the scenario graphs, the lighting the vehicle is drawn with and
// the ambient sound emitters. Port of pw-mods-tool's garage map builder; the
// map's gameplay parts (start points, ground collider, SDK assets) have no
// counterpart on a hangar stand.
using System;
using System.Collections.Generic;
using System.Linq;
using BlitzLegacy.Runtime.Lighting;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace BlitzLegacy.Runtime.Hangar.Halloween
{
    // OnDestroy has to run outside Play Mode too: the render CLI and the
    // EditMode tests build the hangar in edit mode. It is the only message.
    [ExecuteAlways]
    public sealed class HalloweenHangarScene : MonoBehaviour
    {
        public const string PbrShaderName = "BlitzLegacy/HalloweenHangar/PBR";
        public const string TexturedShaderName = "BlitzLegacy/HalloweenHangar/Textured";
        public const string SkyShaderName = "BlitzLegacy/HalloweenHangar/Sky";
        public const string ParticleShaderName = "BlitzLegacy/HalloweenHangar/Particle";

        /// <summary>Shaders the build finds by name; the player build always includes them.</summary>
        public static readonly string[] ShaderNames =
        {
            PbrShaderName, TexturedShaderName, SkyShaderName, ParticleShaderName,
        };

        private static bool _unavailableLogged;

        /// <summary>
        /// Whether this device can draw the scene, by the same shader checks
        /// <see cref="Create"/> enforces. A headless player (the dedicated
        /// server, the smoke clients) runs on the Null device, which supports
        /// none of them, and a GPU may lack one; the hangar screen then keeps
        /// the account hangar instead of failing to open. Logged once.
        /// </summary>
        public static bool CanRender
        {
            get
            {
                var reason = UnavailableReason();
                if (reason != null && !_unavailableLogged)
                {
                    _unavailableLogged = true;
                    Debug.LogWarning("[Blitz] ported hangars unavailable (" + reason + "); the account hangar stays up");
                }
                return reason == null;
            }
        }

        /// <summary>Why this device cannot draw the scene, or null when it can.</summary>
        public static string UnavailableReason()
        {
            foreach (var name in ShaderNames)
            {
                var shader = Shader.Find(name);
                if (shader == null) return "missing shader " + name;
                if (!shader.isSupported) return $"shader {name} is not supported on {SystemInfo.graphicsDeviceType}";
            }
            return null;
        }

        public static string SceneResource => HH.Root + "/scene";
        public static string MeshResource => HH.Root + "/meshes";
        public static string ClipResource => HH.Root + "/clips";

        private PortedHangar _export = PortedHangar.Halloween2026;

        private readonly List<Object> _owned = new List<Object>();
        private readonly List<Object> _loaded = new List<Object>();
        private readonly List<Light> _lights = new List<Light>();
        private Cubemap _reflection;
        private Color _ambientSky, _ambientEquator, _ambientGround;
        private Vector4 _fogColour, _fogRange, _fogDepth, _fogSunDirection;
        private RenderState _previous;
        private bool _released;

        /// <summary>The hangar whose environment is applied: the one that gives it back.</summary>
        private static HalloweenHangarScene _environmentOwner;

        /// <summary>
        /// Every hangar built and not released yet. Resources.Load hands them
        /// all the same textures, so one going away unloads only what no
        /// other still draws with.
        /// </summary>
        private static readonly List<HalloweenHangarScene> Built = new List<HalloweenHangarScene>();

        /// <summary>Loaded textures the release unloaded.</summary>
        public int UnloadedTextureCount { get; private set; }

        /// <summary>Loaded textures the release left to a hangar still built.</summary>
        public int SharedTextureCount { get; private set; }

        /// <summary>The export this hangar is built from.</summary>
        public PortedHangar Export => _export;

        public string Id => _export.MapName;
        public int NodeCount { get; private set; }
        public int RendererCount { get; private set; }
        public int SkinnedRendererCount { get; private set; }
        public int MeshCount { get; private set; }
        public int TriangleCount { get; private set; }
        public int MaterialCount { get; private set; }
        public int ClipCount { get; private set; }
        public int AnimationCount { get; private set; }
        public int EffectCount { get; private set; }
        public int ParticleLayerCount { get; private set; }
        public int ParticleSystemCount { get; private set; }
        public int StateSwitcherCount { get; private set; }
        public int ScenarioCount => Scenarios.Count;

        /// <summary>Unity lights of the stand: the sun and the vehicle's fill lights.</summary>
        public int LightCount => _lights.Count;

        public Light Sun { get; private set; }
        /// <summary>The ambient sound emitters; null for a hangar without sounds.</summary>
        public HalloweenHangarAudio Audio { get; private set; }
        public IReadOnlyList<HalloweenHangarStates> States { get; private set; } = new HalloweenHangarStates[0];
        public IReadOnlyList<PortedHangarScenario> Scenarios { get; private set; } = new PortedHangarScenario[0];

        /// <summary>Where the client stands the vehicle, in the stand's frame.</summary>
        public Vector3 SpawnPosition { get; private set; }

        /// <summary>The client's vehicle rotation; the stand turns its own model by it.</summary>
        public Quaternion SpawnRotation { get; private set; } = Quaternion.identity;

        /// <summary>The scene camera of the client's hangar file.</summary>
        public Vector3 CameraPosition { get; private set; }
        public Vector3 CameraForward { get; private set; } = Vector3.forward;
        public float CameraFieldOfView { get; private set; } = 70f;

        /// <summary>
        /// The near plane of that camera. The Miku hangar has a stray triangle
        /// half a metre in front of it that the client clips this way.
        /// </summary>
        public float CameraNear { get; private set; } = 1f;

        /// <summary>
        /// The client's World of Tanks Blitz logos the export replaces with the
        /// port's own art (scene.json "rebrand"), as the Panzer War port does.
        /// </summary>
        public IReadOnlyList<Rebrand> Rebrands { get; private set; } = new Rebrand[0];

        /// <summary>
        /// One replaced logo: the render object that shows it, what the client
        /// showed and what the port shows there, and the close-up camera the
        /// exporter places in front of it, in the scene's frame.
        /// </summary>
        public readonly struct Rebrand
        {
            public readonly string Name, Path, Client, Port;
            public readonly Vector3 ViewPosition, ViewForward;

            /// <summary>Horizontal, like the client's camera.</summary>
            public readonly float ViewFieldOfView;

            public Rebrand(string name, string path, string client, string port,
                Vector3 viewPosition, Vector3 viewForward, float viewFieldOfView)
            {
                Name = name;
                Path = path;
                Client = client;
                Port = port;
                ViewPosition = viewPosition;
                ViewForward = viewForward;
                ViewFieldOfView = viewFieldOfView;
            }
        }

        /// <summary>Clear colour behind the sky dome: the client's fog colour.</summary>
        public Color BackgroundColour { get; private set; }

        public IReadOnlyList<string> Report { get; private set; } = new string[0];
        public IReadOnlyList<string> Warnings { get; private set; } = new string[0];

        /// <summary>
        /// Builds <paramref name="export"/> (the Halloween hangar when null)
        /// under <paramref name="parent"/>. The root comes back inactive: the
        /// caller applies the environment and then activates it, which starts
        /// the animations, particles, state draws, scenarios and sounds the way
        /// loading the client's scene does.
        /// </summary>
        public static HalloweenHangarScene Create(Transform parent, PortedHangar export = null)
        {
            export = export ?? PortedHangar.Halloween2026;
            var root = new GameObject(export.Label + "_" + export.MapName);
            root.SetActive(false);
            root.transform.SetParent(parent, false);
            var scene = root.AddComponent<HalloweenHangarScene>();
            scene._export = export;
            try
            {
                scene.Build();
                return scene;
            }
            catch
            {
                scene.ReleaseObjects();
                Release(root);
                throw;
            }
        }

        private static void Release(Object obj)
        {
            if (obj == null) return;
            if (Application.isPlaying) Destroy(obj);
            else DestroyImmediate(obj);
        }

        // -------------------------------------------------------------- build
        private void Build()
        {
            var started = Time.realtimeSinceStartup;
            Built.Add(this);
            HH.Begin(_owned, _export);
            HalloweenHangarAssets.Begin(_loaded);
            HalloweenHangarMaterials.Reset();
            try
            {
                var scene = LoadScene();
                var ctx = Context(scene);
                var nodes = Nodes(scene, transform);
                var meshRecords = ReadMeshes(scene);
                Renderers(scene, ctx, nodes, meshRecords);
                Animations(scene, nodes);
                HalloweenHangarParticles.Build(new ParticleBuildContext
                {
                    materials = ctx,
                    shader = RequireShader(ParticleShaderName),
                    meshes = meshRecords,
                    node = i => i >= 0 && i < nodes.Length ? nodes[i] : null,
                });
                EffectCount = HalloweenHangarParticles.EffectCount;
                ParticleLayerCount = HalloweenHangarParticles.LayerCount;
                ParticleSystemCount = HalloweenHangarParticles.Systems.Count;
                HalloweenHangarParticles.Systems.Clear();
                StatesOf(scene, nodes);
                ScenariosOf(scene, nodes);
                Lighting(scene, ctx);
                Stand(scene);
                if (scene.Req("sounds").Count > 0)
                    Audio = HalloweenHangarAudio.Build(gameObject, scene, nodes);
                else
                    HH.Log("audio: the scene has no sound emitters");

                MaterialCount = HalloweenHangarMaterials.Count;
                HH.Log($"scene {HH.MapName}: {NodeCount} nodes, {RendererCount} renderers ({SkinnedRendererCount} skinned), " +
                       $"{MeshCount} meshes, {TriangleCount} triangles, {MaterialCount} materials (" +
                       string.Join(", ", HalloweenHangarMaterials.ShaderCounts.Select(p => $"{p.Key} {p.Value}")) + "), " +
                       $"{ParticleSystemCount} particle systems, {StateSwitcherCount} state switchers, {ScenarioCount} scenarios, " +
                       $"{(Audio != null ? Audio.EmitterCount : 0)} sound emitters, {(Time.realtimeSinceStartup - started) * 1000f:0} ms");
            }
            finally
            {
                Report = HH.Report.ToArray();
                Warnings = HH.Warnings.ToArray();
                HH.End();
                HalloweenHangarAssets.End();
                HalloweenHangarMaterials.Reset();
            }
        }

        private static HalloweenJson LoadScene()
        {
            var asset = HH.Load<TextAsset>(SceneResource);
            HalloweenJson scene;
            try
            {
                scene = HalloweenJson.Parse(asset.text);
            }
            finally
            {
                Resources.UnloadAsset(asset);
            }
            HH.Require(scene.Req("map").S() == HH.MapName, $"scene.json describes {scene.Req("map").S()}, not {HH.MapName}");
            return scene;
        }

        private static Shader RequireShader(string name)
        {
            var shader = Shader.Find(name);
            HH.Require(shader != null, "Missing shader " + name);
            HH.Require(shader.isSupported, $"Shader {name} is not supported on {SystemInfo.graphicsDeviceType}");
            return shader;
        }

        // ------------------------------------------------------ environment
        private MaterialContext Context(HalloweenJson scene)
        {
            var env = scene.Req("environment");
            var sun = env.Req("sun");
            var ibl = env.Req("ibl");
            var fog = env.Req("fog");
            var ctx = new MaterialContext
            {
                scene = scene,
                pbr = RequireShader(PbrShaderName),
                textured = RequireShader(TexturedShaderName),
                sky = RequireShader(SkyShaderName),
            };
            string diffuseName = ibl.Req("diffuse").S(), specularName = ibl.Req("specular").S();
            string reflectionName = env.Req("vehicle").Req("reflection").S();
            foreach (var record in scene.Req("cubemaps").Items)
            {
                string name = record.Req("name").S();
                HH.Require(!record["srgb"].B(), $"Cube map {name} is sRGB; the client samples the IBL maps linearly");
                var cube = HalloweenHangarAssets.BuildCubemap(record);
                if (name == diffuseName) ctx.diffuse = cube;
                else if (name == specularName) ctx.specular = cube;
                else if (name == reflectionName) _reflection = cube;
                else HH.Warn($"Cube map {name} is not used by the IBL component");
            }
            // Only the PBR shader samples the IBL maps and the DFG table; an
            // export without PBR materials ships just the vehicle's reflection.
            bool pbr = scene.Req("materials").Items.Any(m => m["shader"].S() == "PBR");
            if (pbr)
            {
                HH.Require(ctx.diffuse != null && ctx.specular != null, "The IBL cube maps are missing from scene.json");
                ctx.dfg = HalloweenHangarAssets.BuildDfg(scene);
            }
            HH.Require(_reflection != null, $"The vehicle's reflection cube map {reflectionName} is missing from scene.json");
            ctx.stub = HalloweenHangarAssets.StubTexture();

            ctx.toLight = sun.Req("toLight").V3().normalized;
            Vector3 color = sun.Req("color").V3();
            float intensity = sun.Req("intensity").F();
            ctx.sunRadiance = new Color(color.x * intensity, color.y * intensity, color.z * intensity, 1f);
            Vector3 fogColor = fog.Req("color").V3();
            ctx.fog = new Vector4(fogColor.x, fogColor.y, fogColor.z, fog.Req("density").F());
            ctx.environmentRotation = EnvironmentRotation(ibl.Req("rotationDava").Q());
            float vehicleRotation = env.Req("vehicle").Req("environmentRotation").F();
            HH.Require(Mathf.Abs(Mathf.DeltaAngle(vehicleRotation * 360f, ctx.environmentRotation * 360f)) < 0.01f,
                $"The vehicle's reflection was baked for an IBL rotation of {vehicleRotation} turns, the scene has {ctx.environmentRotation}");

            var shadow = new Dictionary<string, float>();
            foreach (var p in env["shadow"].Items)
                shadow[p.Req("name").S()] = p.Req("values")[0].F();
            float Shadow(string key, float fallback)
            {
                if (shadow.TryGetValue(key, out float v)) return v;
                HH.Warn($"shadow parameter {key} is missing, {fallback} is used");
                return fallback;
            }
            ctx.cutoffX = Shadow("cutoffSlope.x", ctx.cutoffX);
            ctx.cutoffY = Shadow("cutoffSlope.y", ctx.cutoffY);
            ctx.litNormalScale = Shadow("litNormalScale", ctx.litNormalScale);
            ctx.gateX = Shadow("LMGateFactor.x", ctx.gateX);
            ctx.gateY = Shadow("LMGateFactor.y", ctx.gateY);
            ctx.shadowColor = new Vector4(Shadow("shadowColor.r", ctx.shadowColor.x), Shadow("shadowColor.g", ctx.shadowColor.y),
                                          Shadow("shadowColor.b", ctx.shadowColor.z), Shadow("shadowColor.a", ctx.shadowColor.w));

            // Blitz fog globals: the DAVA exponential branch of BlitzFog.hlsl,
            // which the vehicle's treads read and the ported shaders gate on;
            // a scene with its fog switched off turns the fog off for both.
            bool fogOn = fog["enabled"].B(true);
            _fogColour = new Vector4(fogColor.x, fogColor.y, fogColor.z, 0f);
            _fogRange = new Vector4(0f, fog["end"].F(1000f), ctx.fogLimit, fogOn ? 2f : 0f);
            _fogDepth = new Vector4(0f, 0f, ctx.fog.w, 0f);
            _fogSunDirection = new Vector4(-ctx.toLight.x, -ctx.toLight.y, -ctx.toLight.z, 0f);
            BackgroundColour = new Color(fogColor.x, fogColor.y, fogColor.z, 1f);
            HH.Log($"environment: sun to {ctx.toLight:F3} radiance {ctx.sunRadiance}, fog {ctx.fog}, IBL rotation {ctx.environmentRotation:0.####} turns, " +
                   $"shadow cut-off ({ctx.cutoffX}, {ctx.cutoffY}), lit normal scale {ctx.litNormalScale}, LM gate ({ctx.gateX}, {ctx.gateY}), shadow colour {ctx.shadowColor}");
            return ctx;
        }

        // The IBL entity turns about the client's up axis only; the client's
        // environmentRotation is that angle in turns.
        private static float EnvironmentRotation(Quaternion dava)
        {
            HH.Require(Mathf.Abs(dava.x) < 1e-4f && Mathf.Abs(dava.y) < 1e-4f,
                $"The IBL rotation {dava} is not a turn about the client's up axis");
            float turns = 2f * Mathf.Atan2(dava.z, dava.w) / (2f * Mathf.PI);
            return turns - Mathf.Round(turns);
        }

        // ------------------------------------------------------------- nodes
        private Transform[] Nodes(HalloweenJson scene, Transform root)
        {
            var records = scene.Req("nodes").Items;
            var nodes = new Transform[records.Count];
            for (int i = 0; i < records.Count; i++)
                nodes[i] = new GameObject(records[i].Req("name").S()).transform;
            for (int i = 0; i < records.Count; i++)
            {
                var r = records[i];
                int parent = r.Req("parent").I(-1);
                HH.Require(parent >= -1 && parent < records.Count && parent != i, $"node {i}: parent {parent}");
                nodes[i].SetParent(parent >= 0 ? nodes[parent] : root, false);
                nodes[i].localPosition = r.Req("position").V3();
                nodes[i].localRotation = r.Req("rotation").Q();
                nodes[i].localScale = r.Req("scale").V3(Vector3.one);
            }
            int inactive = 0;
            for (int i = 0; i < records.Count; i++)
                if (!records[i].Req("active").B(true))
                {
                    nodes[i].gameObject.SetActive(false);
                    inactive++;
                }
            NodeCount = records.Count;
            HH.Log($"nodes: {records.Count} ({inactive} inactive)");
            return nodes;
        }

        // --------------------------------------------------------- renderers
        private static List<MeshRecord> ReadMeshes(HalloweenJson scene)
        {
            var asset = HH.Load<TextAsset>(MeshResource);
            List<MeshRecord> records;
            try
            {
                records = HalloweenHangarData.ReadMeshes(asset.bytes, MeshResource);
            }
            finally
            {
                Resources.UnloadAsset(asset);
            }
            HH.Require(records.Count == scene.Req("meshes").Count,
                $"meshes.bytes holds {records.Count} meshes, scene.json lists {scene.Req("meshes").Count}");
            return records;
        }

        private void Renderers(HalloweenJson scene, MaterialContext ctx, Transform[] nodes, List<MeshRecord> records)
        {
            var meshes = new Dictionary<int, Mesh>();
            Mesh MeshAsset(int index)
            {
                HH.Require(index >= 0 && index < records.Count, $"mesh {index} is missing");
                if (!meshes.TryGetValue(index, out var mesh))
                {
                    mesh = HH.Store(HalloweenHangarData.ToMesh(records[index]), $"Meshes/{index:D3}_{HH.Safe(records[index].name)}.asset");
                    meshes[index] = mesh;
                }
                return mesh;
            }

            int skinned = 0, shadowOnly = 0, triangles = 0;
            float worstBind = 0f;
            string worstBindPath = "";
            foreach (var r in scene.Req("renderers").Items)
            {
                int node = r.Req("node").I(-1);
                string path = r["path"].S("");
                HH.Require(node >= 0 && node < nodes.Length, $"{path}: renderer node {node}");
                var go = nodes[node].gameObject;
                var mesh = MeshAsset(r.Req("mesh").I(-1));
                int[] lights = r["lights"].Ints();
                int light0 = lights.Length > 0 ? lights[0] : -1, light1 = lights.Length > 1 ? lights[1] : -1;
                bool isSkinned = r.Req("skinned").B();
                int[] materialIndices = r.Req("materials").Ints();
                HH.Require(materialIndices.Length == mesh.subMeshCount,
                    $"{path}: {materialIndices.Length} materials for {mesh.subMeshCount} submeshes");
                var materials = materialIndices.Select(m => HalloweenHangarMaterials.Get(ctx, m, light0, light1, isSkinned)).ToArray();
                for (int s = 0; s < mesh.subMeshCount; s++)
                    triangles += (int)(mesh.GetIndexCount(s) / 3);

                Renderer renderer;
                if (isSkinned)
                {
                    int[] boneNodes = r.Req("bones").Ints();
                    var bindposes = mesh.bindposes;
                    HH.Require(boneNodes.Length == bindposes.Length,
                        $"{path}: {boneNodes.Length} bones for {bindposes.Length} bind poses");
                    var bones = new Transform[boneNodes.Length];
                    for (int b = 0; b < bones.Length; b++)
                    {
                        HH.Require(boneNodes[b] >= 0 && boneNodes[b] < nodes.Length, $"{path}: bone node {boneNodes[b]}");
                        bones[b] = nodes[boneNodes[b]];
                        Matrix4x4 rest = bones[b].worldToLocalMatrix * go.transform.localToWorldMatrix;
                        float d = MaxDifference(rest, bindposes[b]);
                        if (d > worstBind)
                        {
                            worstBind = d;
                            worstBindPath = $"{path} bone {bones[b].name}";
                        }
                    }
                    var smr = go.AddComponent<SkinnedMeshRenderer>();
                    smr.sharedMesh = mesh;
                    smr.bones = bones;
                    smr.rootBone = go.transform;
                    smr.quality = SkinQuality.Bone4;
                    smr.updateWhenOffscreen = true;
                    smr.skinnedMotionVectors = false;
                    renderer = smr;
                    skinned++;
                }
                else
                {
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    renderer = go.AddComponent<MeshRenderer>();
                }
                renderer.sharedMaterials = materials;
                int casting = r.Req("shadowCasting").I();
                HH.Require(casting >= 0 && casting <= 3, $"{path}: shadow casting mode {casting}");
                renderer.shadowCastingMode = (ShadowCastingMode)casting;
                if (casting == (int)ShadowCastingMode.ShadowsOnly)
                    shadowOnly++;
                renderer.receiveShadows = true;
                renderer.lightProbeUsage = LightProbeUsage.Off;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                renderer.motionVectorGenerationMode = MotionVectorGenerationMode.Camera;
            }
            RendererCount = scene.Req("renderers").Count;
            SkinnedRendererCount = skinned;
            MeshCount = meshes.Count;
            TriangleCount = triangles;
            HH.Log($"renderers: {RendererCount} ({skinned} skinned, {shadowOnly} shadow only), {meshes.Count} meshes; " +
                   $"the rest pose departs from the bind poses by at most {worstBind:0.####} ({worstBindPath})");
        }

        private static float MaxDifference(Matrix4x4 a, Matrix4x4 b)
        {
            float d = 0f;
            for (int i = 0; i < 16; i++)
                d = Mathf.Max(d, Mathf.Abs(a[i] - b[i]));
            return d;
        }

        // -------------------------------------------------------- animations
        private void Animations(HalloweenJson scene, Transform[] nodes)
        {
            if (scene.Req("clips").Count == 0 && scene.Req("animations").Count == 0)
            {
                HH.Log("animations: the scene has no animated nodes");
                return;
            }
            var asset = HH.Load<TextAsset>(ClipResource);
            List<ClipRecord> records;
            try
            {
                records = HalloweenHangarData.ReadClips(asset.bytes, ClipResource);
            }
            finally
            {
                Resources.UnloadAsset(asset);
            }
            var table = scene.Req("clips").Items;
            HH.Require(records.Count == table.Count, $"clips.bytes holds {records.Count} clips, scene.json lists {table.Count}");
            var clips = new AnimationClip[records.Count];
            for (int c = 0; c < records.Count; c++)
            {
                HH.Require(records[c].name == table[c].Req("name").S(), $"clip {c}: {records[c].name} / {table[c].Req("name").S()}");
                clips[c] = HH.Store(HalloweenHangarData.ToClip(records[c]), $"Clips/{c:D2}_{HH.Safe(records[c].name)}.anim");
                if (Mathf.Abs(clips[c].length - records[c].length) > 1e-3f)
                    HH.Warn($"clip {records[c].name}: its keys end at {clips[c].length:0.###} s, the client loops after {records[c].length:0.###} s");
            }
            int autoplay = 0, curves = 0, ambiguous = 0;
            foreach (var a in scene.Req("animations").Items)
            {
                int node = a.Req("node").I(-1), index = a.Req("clip").I(-1);
                string path = a["path"].S("");
                HH.Require(node >= 0 && node < nodes.Length && index >= 0 && index < clips.Length, $"{path}: animation of node {node}, clip {index}");
                var owner = nodes[node];
                foreach (var curve in records[index].curves)
                {
                    var target = Resolve(owner, curve.path, out bool twin);
                    HH.Require(target != null, $"{path}: clip {records[index].name} animates {curve.path}, which is not below the node");
                    if (twin)
                        ambiguous++;
                    curves++;
                }
                var animation = owner.gameObject.AddComponent<Animation>();
                animation.AddClip(clips[index], clips[index].name);
                animation.clip = clips[index];
                animation.playAutomatically = a["autoplay"].B(true);
                animation.wrapMode = a["loop"].B(true) ? WrapMode.Loop : WrapMode.Once;
                animation.cullingType = AnimationCullingType.AlwaysAnimate;
                if (animation.playAutomatically)
                    autoplay++;
                else
                    HH.Log($"{path}: {records[index].name} starts on a tap in the client; the stand keeps it at rest");
                if (a["repeats"].I(0) > 0)
                    HH.Warn($"{path}: {a["repeats"].I(0)} repeats are not ported, the clip loops");
            }
            if (ambiguous > 0)
                HH.Warn($"animations: {ambiguous} curve paths pass siblings of the same name; Unity binds the first");
            ClipCount = clips.Length;
            AnimationCount = scene.Req("animations").Count;
            HH.Log($"animations: {clips.Length} clips, {AnimationCount} animated nodes ({autoplay} autoplay), {curves} bound curves");
        }

        private static Transform Resolve(Transform root, string path, out bool ambiguous)
        {
            ambiguous = false;
            if (string.IsNullOrEmpty(path))
                return root;
            var current = root;
            foreach (string part in path.Split('/'))
            {
                Transform found = null;
                int count = 0;
                foreach (Transform child in current)
                    if (child.name == part)
                    {
                        if (found == null) found = child;
                        count++;
                    }
                if (found == null)
                    return null;
                ambiguous |= count > 1;
                current = found;
            }
            return current;
        }

        // ------------------------------------------------------------ states
        private void StatesOf(HalloweenJson scene, Transform[] nodes)
        {
            var switchers = new List<HalloweenHangarStates>();
            foreach (var s in scene.Req("states").Items)
            {
                string path = s["path"].S("");
                int node = s.Req("node").I(-1);
                HH.Require(node >= 0 && node < nodes.Length, $"{path}: state switcher node {node}");
                HH.Require(s["activatesState"].B(), $"{path}: the scenario does not activate a state");
                var owner = nodes[node];
                int[] indices = s.Req("states").Ints();
                int n = s.Req("randomN").I(-1);
                int defaultState = s["defaultState"].I(0);
                HH.Require(n >= 0, $"{path}: Random({n})");
                var states = new Transform[indices.Length];
                for (int k = 0; k < indices.Length; k++)
                {
                    HH.Require(indices[k] >= 0 && indices[k] < nodes.Length && nodes[indices[k]].parent == owner,
                        $"{path}: state {k} (node {indices[k]}) is not a child of the switcher");
                    states[k] = nodes[indices[k]];
                    HH.Require(states[k].gameObject.activeSelf == (k == defaultState),
                        $"{path}: state {states[k].name} starts {(states[k].gameObject.activeSelf ? "active" : "inactive")}");
                }
                var switcher = owner.gameObject.AddComponent<HalloweenHangarStates>();
                switcher.Configure(states, n);
                switchers.Add(switcher);
                HH.Log($"{path}: draws 0..{n} over {states.Length} states" +
                       (n >= states.Length ? $" ({n - states.Length + 1} of {n + 1} draws show none)" : ""));
            }
            States = switchers;
            StateSwitcherCount = switchers.Count;
            HH.Log($"states: {switchers.Count} switchers");
        }

        // --------------------------------------------------------- scenarios
        // The scenario graphs the exporter compiled; each runs on its node.
        // One tick at build time puts the clock and the tints in place before
        // the first frame (and for the edit-mode renders, which have no Update).
        private void ScenariosOf(HalloweenJson scene, Transform[] nodes)
        {
            var scenarios = new List<PortedHangarScenario>();
            int targets = 0, rotations = 0, materials = 0, clocks = 0;
            var now = PortedHangarScenario.LocalNow();
            foreach (var record in scene["scenarios"].Items)
            {
                string path = record["path"].S("");
                int node = record.Req("node").I(-1);
                HH.Require(node >= 0 && node < nodes.Length, $"{path}: scenario node {node}");
                var scenario = nodes[node].gameObject.AddComponent<PortedHangarScenario>();
                scenario.Configure(record, nodes);
                scenario.Tick(0f, now);
                scenarios.Add(scenario);
                targets += scenario.TargetCount;
                rotations += scenario.RotationCount;
                materials += scenario.MaterialSetterCount;
                if (scenario.UsesClock)
                    clocks++;
            }
            Scenarios = scenarios;
            HH.Log($"scenarios: {scenarios.Count} programs ({clocks} read the clock), {targets} visibility targets, " +
                   $"{rotations} rotations, {materials} material setters");
        }

        // ---------------------------------------------------------- lighting
        // The hangar's own shaders light themselves from their material
        // parameters; Unity's lights and environment serve the vehicle (and
        // the sun's shadow map the hangar shaders sample).
        private void Lighting(HalloweenJson scene, MaterialContext ctx)
        {
            var env = scene.Req("environment");
            var sun = env.Req("sun");

            var sunObject = new GameObject("Sun");
            sunObject.transform.SetParent(transform, false);
            Vector3 up = Mathf.Abs(ctx.toLight.y) > 0.99f ? Vector3.forward : Vector3.up;
            sunObject.transform.localRotation = Quaternion.LookRotation(-ctx.toLight, up);
            var light = sunObject.AddComponent<Light>();
            light.type = LightType.Directional;
            // Unity lights the vehicle with colour x intensity where the client
            // divides its diffuse sun by pi; the colours are the client's linear values.
            light.color = sun.Req("color").C(Color.white).gamma;
            light.intensity = sun.Req("intensity").F() / Mathf.PI;
            light.shadows = LightShadows.Soft;
            light.shadowStrength = 1f;
            Sun = light;
            _lights.Add(light);

            Vector3 spawn = scene.Req("spawn").Req("position").V3();
            foreach (int index in env["tankLights"].Ints())
            {
                var record = scene.Req("lights")[index];
                HH.Require(record.Exists, $"tank light {index} is missing");
                var go = new GameObject("TankLight_" + index);
                go.transform.SetParent(transform, false);
                go.transform.localPosition = record.Req("position").V3();
                go.AddComponent<ShadowlessLight>();
                var point = go.AddComponent<Light>();
                point.type = LightType.Point;
                point.color = record.Req("color").C(Color.white).gamma;
                point.range = record.Req("radius").F();
                point.intensity = PointIntensity(record, spawn);
                point.shadows = LightShadows.None;
                _lights.Add(point);
                HH.Log($"tank light {index}: point light at {go.transform.localPosition:F2}, range {point.range}, Unity intensity {point.intensity:0.##}");
            }

            // Ambient for the vehicle: the client's irradiance map at the
            // zenith, the horizon and the nadir, sampled by the exporter.
            var vehicle = env.Req("vehicle");
            _ambientSky = vehicle.Req("ambientSky").C().gamma;
            _ambientEquator = vehicle.Req("ambientEquator").C().gamma;
            _ambientGround = vehicle.Req("ambientGround").C().gamma;
            HH.Log($"lighting: sun {light.intensity:0.###} x {light.color}, ambient sky {_ambientSky} equator {_ambientEquator} " +
                   $"ground {_ambientGround}, reflection {_reflection.name}, exponential fog {ctx.fog.w}");
        }

        // The client's point light gives colour x 4 intensity / d^falloff to a
        // diffuse surface (4 pi intensity / d^falloff over pi); URP gives
        // colour x intensity / d^2 x (1 - (d / range)^4)^2. Equal at the spawn.
        public static float PointIntensity(HalloweenJson light, Vector3 at)
        {
            float d = Mathf.Max(0.5f, Vector3.Distance(light.Req("position").V3(), at));
            float range = light.Req("radius").F(), falloff = light.Req("falloff").F(), intensity = light.Req("intensity").F();
            float client = 4f * intensity / Mathf.Pow(d, falloff) * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(range * 0.95f, range, d)));
            float window = Mathf.Clamp01(1f - Mathf.Pow(d * d / (range * range), 2f));
            window *= window;
            HH.Require(window > 1e-3f, $"the spawn lies outside the tank light's range {range}");
            return client * d * d / window;
        }

        private void Stand(HalloweenJson scene)
        {
            var spawn = scene.Req("spawn");
            SpawnPosition = spawn.Req("position").V3();
            SpawnRotation = spawn.Req("rotation").Q();
            var camera = scene["camera"];
            if (camera.Exists)
            {
                CameraPosition = camera.Req("position").V3();
                CameraForward = camera.Req("forward").V3(Vector3.forward).normalized;
                CameraFieldOfView = camera["fov"].F(70f);
                CameraNear = camera["near"].F(1f);
            }
            HH.Log($"stand: vehicle at {SpawnPosition:F2} facing {SpawnRotation * Vector3.forward:F3}; scene camera at {CameraPosition:F2} looking {CameraForward:F3}");

            var rebrands = new List<Rebrand>();
            foreach (var item in scene["rebrand"].Items)
            {
                var view = item.Req("view");
                var rebrand = new Rebrand(item.Req("name").S(), item.Req("path").S(), item["client"].S(""), item["port"].S(""),
                    view.Req("position").V3(), view.Req("forward").V3(Vector3.forward).normalized, view.Req("fov").F());
                rebrands.Add(rebrand);
                HH.Log($"rebrand {rebrand.Name}: {rebrand.Path} shows {rebrand.Port} in place of the {rebrand.Client}");
            }
            Rebrands = rebrands;
        }

        // ----------------------------------------------------- render state
        /// <summary>
        /// Hands the vehicle's lighting, the reflection and the fog to Unity
        /// and the Blitz shaders. The state it replaces comes back with
        /// <see cref="ReleaseEnvironment"/>. A hangar applied while another
        /// one still holds the environment (a hangar screen rebuilt before
        /// the old one is gone) takes over the state that one replaced, so
        /// whichever goes last restores the lighting from before both.
        /// </summary>
        public void ApplyEnvironment()
        {
            if (_previous == null)
            {
                var holder = _environmentOwner;
                _previous = holder != null && holder != this && !holder._released && holder._previous != null
                    ? holder._previous
                    : RenderState.Capture();
            }
            _environmentOwner = this;
            _released = false;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = _ambientSky;
            RenderSettings.ambientEquatorColor = _ambientEquator;
            RenderSettings.ambientGroundColor = _ambientGround;
            RenderSettings.ambientIntensity = 1f;
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
            RenderSettings.customReflectionTexture = _reflection;
            RenderSettings.reflectionIntensity = 1f;
            RenderSettings.reflectionBounces = 1;
            RenderSettings.skybox = null;
            RenderSettings.sun = Sun;
            // The tank and the ported shaders take their fog from the Blitz
            // globals, so the graphics tab's fog switch reaches all of them.
            RenderSettings.fog = false;
            Shader.SetGlobalVector(SourceEnvironment.FogColourId, _fogColour);
            Shader.SetGlobalVector(SourceEnvironment.FogRangeId, _fogRange);
            Shader.SetGlobalVector(FogDepthId, _fogDepth);
            Shader.SetGlobalVector(FogHeightId, Vector4.zero);
            Shader.SetGlobalVector(SourceEnvironment.FogSkyId, Vector4.zero);
            Shader.SetGlobalVector(SourceEnvironment.FogSunId, Vector4.zero);
            Shader.SetGlobalVector(SourceEnvironment.FogSunDirectionId, _fogSunDirection);
            SourceEnvironment.ClearClientFog();
            SourceAdjustments.Apply(new BlitzLegacy.Core.Maps.LightingEnvironment());
            GraphicsQuality.Apply();
        }

        private static readonly int FogDepthId = Shader.PropertyToID("_BlitzFogDepth");
        private static readonly int FogHeightId = Shader.PropertyToID("_BlitzFogHeight");

        /// <summary>
        /// Gives back the render state <see cref="ApplyEnvironment"/> replaced,
        /// part by part, where nobody has replaced this hangar's own since.
        /// A hangar that handed the environment to a newer one leaves it
        /// alone: the same scene data lights both alike, so the value checks
        /// below cannot tell them apart.
        /// </summary>
        public void ReleaseEnvironment()
        {
            if (_released || _previous == null)
                return;
            _released = true;
            if (_environmentOwner != this)
                return;
            _environmentOwner = null;
            if (RenderSettings.customReflectionTexture == _reflection)
            {
                RenderSettings.defaultReflectionMode = _previous.ReflectionMode;
                RenderSettings.customReflectionTexture = _previous.CustomReflection;
                RenderSettings.reflectionIntensity = _previous.ReflectionIntensity;
                RenderSettings.reflectionBounces = _previous.ReflectionBounces;
            }
            if (RenderSettings.ambientMode == AmbientMode.Trilight && RenderSettings.ambientSkyColor == _ambientSky &&
                RenderSettings.ambientEquatorColor == _ambientEquator && RenderSettings.ambientGroundColor == _ambientGround)
            {
                RenderSettings.ambientMode = _previous.AmbientMode;
                RenderSettings.ambientSkyColor = _previous.AmbientSky;
                RenderSettings.ambientEquatorColor = _previous.AmbientEquator;
                RenderSettings.ambientGroundColor = _previous.AmbientGround;
                RenderSettings.ambientIntensity = _previous.AmbientIntensity;
            }
            if (RenderSettings.sun == null || RenderSettings.sun == Sun)
                RenderSettings.sun = _previous.Sun != null && _previous.Sun != Sun ? _previous.Sun : null;
            if (Shader.GetGlobalVector(SourceEnvironment.FogRangeId) == _fogRange)
            {
                Shader.SetGlobalVector(SourceEnvironment.FogColourId, _previous.FogColour);
                Shader.SetGlobalVector(SourceEnvironment.FogRangeId, _previous.FogRange);
                Shader.SetGlobalVector(FogDepthId, _previous.FogDepth);
                Shader.SetGlobalVector(SourceEnvironment.FogSunDirectionId, _previous.FogSunDirection);
            }
        }

        private sealed class RenderState
        {
            public AmbientMode AmbientMode;
            public Color AmbientSky, AmbientEquator, AmbientGround;
            public float AmbientIntensity;
            public DefaultReflectionMode ReflectionMode;
            public Texture CustomReflection;
            public float ReflectionIntensity;
            public int ReflectionBounces;
            public Light Sun;
            public Vector4 FogColour, FogRange, FogDepth, FogSunDirection;

            public static RenderState Capture()
            {
                return new RenderState
                {
                    AmbientMode = RenderSettings.ambientMode,
                    AmbientSky = RenderSettings.ambientSkyColor,
                    AmbientEquator = RenderSettings.ambientEquatorColor,
                    AmbientGround = RenderSettings.ambientGroundColor,
                    AmbientIntensity = RenderSettings.ambientIntensity,
                    ReflectionMode = RenderSettings.defaultReflectionMode,
                    CustomReflection = RenderSettings.customReflectionTexture,
                    ReflectionIntensity = RenderSettings.reflectionIntensity,
                    ReflectionBounces = RenderSettings.reflectionBounces,
                    Sun = RenderSettings.sun,
                    FogColour = Shader.GetGlobalVector(SourceEnvironment.FogColourId),
                    FogRange = Shader.GetGlobalVector(SourceEnvironment.FogRangeId),
                    FogDepth = Shader.GetGlobalVector(FogDepthId),
                    FogSunDirection = Shader.GetGlobalVector(SourceEnvironment.FogSunDirectionId),
                };
            }
        }

        // ------------------------------------------------------------ release
        private void ReleaseObjects()
        {
            Built.RemoveAll(scene => scene == null || scene == this);
            if (Audio != null)
                Audio.Release();
            foreach (var obj in _owned)
                Release(obj);
            _owned.Clear();
            // Textures loaded from Resources stay referenced by nothing once
            // the materials are gone; unloading them frees the memory now
            // instead of at the next scene change. A hangar built before this
            // one goes holds the very same objects and keeps them.
            if (_loaded.Count == 0)
                return;
            var held = new HashSet<Object>(Built.SelectMany(scene => scene._loaded));
            foreach (var obj in _loaded)
            {
                if (obj == null)
                    continue;
                if (held.Contains(obj))
                {
                    SharedTextureCount++;
                    continue;
                }
                Resources.UnloadAsset(obj);
                UnloadedTextureCount++;
            }
            _loaded.Clear();
            Debug.Log($"[Blitz] 11.21 hangar released: {UnloadedTextureCount} textures unloaded, " +
                      $"{SharedTextureCount} kept for the hangar still built");
        }

        /// <summary>
        /// Gives the render state back and frees what the build made: meshes,
        /// materials, cube maps, clips and the textures it loaded that no
        /// hangar still built draws with. Runs from OnDestroy, which a root
        /// that was never shown does not get, so owners call it before they
        /// destroy the hangar. Safe to repeat.
        /// </summary>
        public void Release()
        {
            ReleaseEnvironment();
            ReleaseObjects();
        }

        private void OnDestroy()
        {
            Release();
        }
    }
}
