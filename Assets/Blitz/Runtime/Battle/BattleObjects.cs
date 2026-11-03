using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Maps;
using BlitzLegacy.Runtime.Assets;
using UnityEngine;
using UnityEngine.Rendering;
using BlitzLegacy.Runtime.Lighting;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// Rebuilds every exported map root object. Ordinary props keep one shared
    /// renderer; trees keep their nested visual parts and movable colliders;
    /// each Godot MultiMesh becomes one shared Unity mesh, so thousands of
    /// authored billboards do not become thousands of GameObjects.
    ///
    /// The battle uses Unity realtime lighting now. The source-bake branch stays
    /// available only as an explicit diagnostic/reference mode; mixing it with
    /// realtime shadows would draw two unrelated sets of darkness.
    /// </summary>
    public sealed class BattleObjects
    {
        /// <summary>
        /// Ported from maps/components/map_object.gdshader. URP Lit used to
        /// draw the props, and it reads Unity's built-in linear fog, which
        /// ignores the source fog density and washed the distant Rudniki
        /// backdrop out to white.
        /// </summary>
        public const string LitShaderName = "BlitzLegacy/Map/Object";
        /// <summary>
        /// The 4.8 client draws its sky dome as flat artwork with no lighting
        /// and no fog, so the dome is the one map mesh that must not go
        /// through the lit shader above.
        /// </summary>
        public const string UnlitShaderName = "Universal Render Pipeline/Unlit";

        public const string VegetationShaderName = "BlitzLegacy/Map/Vegetation";
        public const string WaterShaderName = "BlitzLegacy/Map/Water";

        private readonly Dictionary<string, Mesh> _meshes =
            new Dictionary<string, Mesh>(StringComparer.Ordinal);

        private readonly Dictionary<string, Mesh> _billboards =
            new Dictionary<string, Mesh>(StringComparer.Ordinal);

        private readonly Dictionary<string, Mesh> _colliders =
            new Dictionary<string, Mesh>(StringComparer.Ordinal);

        private readonly Dictionary<string, Material> _materials =
            new Dictionary<string, Material>(StringComparer.Ordinal);

        /// <summary>The rebuilt 4.8 client materials, keyed by source name.</summary>
        private readonly Dictionary<string, Material> _clientMaterials =
            new Dictionary<string, Material>(StringComparer.Ordinal);

        /// <summary>The same families again, sampling the baked atlas.</summary>
        private readonly Dictionary<string, Material> _bakedMaterials =
            new Dictionary<string, Material>(StringComparer.Ordinal);

        private readonly List<BattleAiCover> _aiCovers =
            new List<BattleAiCover>();

        private MapObjectTable _table;
        private MapMeshLibrary _library;
        private MapShapeLibrary _shapes;
        private BakedLighting _lighting;

        /// <summary>Godot render scene nodes by gameplay object name.</summary>
        private Dictionary<string, List<ClientMapNode>> _sceneNodes;
        private readonly Dictionary<string, Transform> _sceneTrunks =
            new Dictionary<string, Transform>(StringComparer.Ordinal);

        /// <summary>The Godot render scene drawing the map, when there is one.</summary>
        public ClientMapScene Scene { get; private set; }

        /// <summary>The bush and crown distance switch of that scene.</summary>
        public BattleVegetationLods VegetationLods { get; private set; }

        public GameObject Root { get; private set; }

        /// <summary>Source navmesh obstacles available to the hiding logic.</summary>
        public List<BattleAiCover> AiCovers { get { return _aiCovers; } }

        /// <summary>Source root objects, not renderer children.</summary>
        public int InstanceCount { get; private set; }

        public int RendererCount { get; private set; }

        public int ColliderCount { get; private set; }

        public int TriggerCount { get; private set; }

        public int DestructibleCount { get; private set; }

        /// <summary>Client border panels dropped from the scene.</summary>
        public int BorderPanelCount { get; private set; }

        public int TreeCount { get; private set; }

        public int VegetationCount { get; private set; }

        public int WaterCount { get; private set; }

        public int DecorationCount { get; private set; }

        public int MultiMeshRendererCount { get; private set; }

        public int BillboardQuadCount { get; private set; }

        /// <summary>Triangles drawn once the map is standing.</summary>
        public int TriangleCount { get; private set; }

        /// <summary>Reads an object table without touching the scene.</summary>
        /// <summary>Props shaded by the atlas rather than by a realtime light.</summary>
        public int LightmappedCount { get; private set; }

        /// <summary>Invisible CollisionPlain floor tiles built as solid slabs.</summary>
        public int FloorSlabCount { get; private set; }

        /// <summary>
        /// True for the production battle path; false only for a deliberate
        /// source-bake diagnostic build.
        /// </summary>
        public bool UsesRealtimeLighting
        {
            get { return _lighting == null; }
        }

        public static MapObjectTable LoadTable(string mapId)
        {
            var json = BlitzResources.LoadText("map_" + mapId + "_objects");
            if (string.IsNullOrEmpty(json))
            {
                Debug.LogError("[Blitz] missing object table: map_" + mapId + "_objects");
                return null;
            }

            return MapObjectTable.Parse(json);
        }

        public static MapMeshLibrary LoadMeshes(MapObjectTable table)
        {
            var asset = Resources.Load<TextAsset>(table.Meshes.Resource);
            if (asset == null)
            {
                Debug.LogError("[Blitz] missing object meshes: " + table.Meshes.Resource);
                return null;
            }

            return MapMeshLibrary.Parse(asset.bytes);
        }

        public static MapShapeLibrary LoadShapes(MapObjectTable table)
        {
            var asset = Resources.Load<TextAsset>(table.Shapes.Resource);
            if (asset == null)
            {
                Debug.LogError("[Blitz] missing object shapes: " + table.Shapes.Resource);
                return null;
            }

            return MapShapeLibrary.Parse(asset.bytes);
        }

        /// <summary>
        /// Builds the props. Production passes <paramref name="useBakedLighting"/>
        /// as false so the authored albedo is lit and shadowed by Unity. Keeping
        /// the opt-in source bake makes the old render reproducible for an A/B
        /// without allowing it to leak into the normal battle.
        /// </summary>
        public static BattleObjects Build(
            Transform parent,
            MapObjectTable table,
            MapMeshLibrary meshes,
            MapShapeLibrary shapes,
            BakedLighting lighting,
            bool useBakedLighting = true,
            string scene = null)
        {
            if (parent == null || table == null || meshes == null || shapes == null)
            {
                throw new ArgumentNullException("map object build input");
            }

            // Godot's tank mask excludes layer 9 destructibles. Their separate
            // Area3D notices the physical body and breaks them before they can
            // act like an immovable wall.
            Physics.IgnoreLayerCollision(
                BattleLayers.Physics, BattleLayers.Destructibles, true);

            // Bushes and leaf volumes are StaticBody3D nodes on Godot layer 6
            // with collision_mask = 0. The tank only scans layers 1 and 2, so
            // those volumes are visibility/foliage data and never stop it.
            // Unity's symmetric matrix would otherwise make every one solid.
            Physics.IgnoreLayerCollision(
                BattleLayers.Physics, BattleLayers.Foliage, true);

            var objects = new BattleObjects
            {
                _table = table,
                _library = meshes,
                _shapes = shapes,
                _lighting = useBakedLighting ? lighting : null,
            };

            objects.Root = new GameObject("Objects");
            objects.Root.transform.SetParent(parent, false);
            if (!string.IsNullOrEmpty(scene))
            {
                objects.LoadScene(scene);
            }

            for (var i = 0; i < table.Objects.Count; i++)
            {
                objects.Spawn(table.Objects[i]);
            }

            if (objects.Scene != null)
            {
                objects.FinishScene();
            }

            return objects;
        }

        /// <summary>
        /// Instantiates a client DAVA scene exported as a Unity model. The
        /// historical map has no Godot object table in the port, so its
        /// authored geometry is kept in one imported asset while gameplay
        /// colliders are attached to the imported meshes here.
        /// </summary>
        public static BattleObjects BuildExternal(Transform parent, string resource)
        {
            return BuildExternal(parent, resource, null);
        }

        /// <summary>
        /// Instantiates the client DAVA scene and binds its actual 4.8.0
        /// destructible state groups to projectile/ram gameplay.
        /// </summary>
        public static BattleObjects BuildExternal(
            Transform parent,
            string resource,
            IList<ExternalMapDestructible> destructibles)
        {
            return BuildExternal(parent, resource, destructibles, null);
        }

        /// <summary>
        /// Instantiates the client DAVA scene, binds its destructibles and
        /// turns its CollisionType 8 trees into <see cref="BattleTree"/>s that
        /// topple under a tank, as the 4.8.0 client and the Godot maps do.
        /// </summary>
        public static BattleObjects BuildExternal(
            Transform parent,
            string resource,
            IList<ExternalMapDestructible> destructibles,
            IList<ExternalMapTree> trees)
        {
            if (parent == null || string.IsNullOrEmpty(resource))
            {
                throw new ArgumentNullException("external map build input");
            }

            // A map ported with its DAVA materials ships the binary client
            // scene instead of an imported model; it builds its own nodes
            // and materials, so only the gameplay binding below is shared.
            var clientScene = ClientMapScene.Exists(resource);
            GameObject prefab = null;
            if (!clientScene)
            {
                prefab = Resources.Load<GameObject>(resource);
                if (prefab == null)
                {
                    throw new InvalidOperationException(
                        "missing external map scene: " + resource);
                }
            }

            Physics.IgnoreLayerCollision(
                BattleLayers.Physics, BattleLayers.Foliage, true);
            var objects = new BattleObjects
            {
                Root = clientScene
                    ? ClientMapScene.Build(parent, resource).gameObject
                    : UnityEngine.Object.Instantiate(prefab, parent, false),
            };
            objects.Root.name = "Objects";
            objects.InstanceCount = 1;

            var treeGroups = new HashSet<string>(StringComparer.Ordinal);
            if (trees != null)
            {
                for (var i = 0; i < trees.Count; i++)
                {
                    treeGroups.Add(trees[i].Group);
                }
            }

            var renderers = DiscardMapBorder(
                objects.Root.GetComponentsInChildren<MeshRenderer>(true), objects);
            for (var i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                var intactInfo = ExternalForGroup(
                    destructibles, renderer.gameObject.name, false);
                var isWreck = ExternalForGroup(
                    destructibles, renderer.gameObject.name, true) != null;
                var passThrough = IsPassThroughName(renderer.gameObject.name);
                // A falling tree is foliage whatever it is called (palm01,
                // akacia01): tanks drive into it and knock it over instead
                // of stopping at the trunk as at a static post.
                var foliage = IsFoliageName(renderer.gameObject.name) ||
                    treeGroups.Contains(renderer.gameObject.name);
                SetLayerRecursively(
                    renderer.gameObject,
                    isWreck ? BattleLayers.Static :
                    intactInfo != null ? BattleLayers.Destructibles :
                    foliage ? BattleLayers.Foliage : BattleLayers.Static);

                var clientNode = clientScene
                    ? renderer.GetComponent<ClientMapNode>() : null;
                if (clientNode == null)
                {
                    // The OBJ importer can put several client materials on one
                    // renderer. Convert every slot; converting only sharedMaterial
                    // left secondary tower/house batches on Unity's black default.
                    var sky = IsSkyName(renderer.gameObject.name);
                    var sharedMaterials = renderer.sharedMaterials;
                    for (var materialIndex = 0;
                         materialIndex < sharedMaterials.Length;
                         materialIndex++)
                    {
                        if (sharedMaterials[materialIndex] != null)
                        {
                            sharedMaterials[materialIndex] = objects.ClientMaterial(
                                sharedMaterials[materialIndex], sky);
                        }
                    }
                    renderer.sharedMaterials = sharedMaterials;

                    renderer.shadowCastingMode = passThrough
                        ? ShadowCastingMode.Off : ShadowCastingMode.On;
                    renderer.receiveShadows = !passThrough;
                }
                if (isWreck)
                {
                    // Switch state 1 is a wreck, never visible before the prop
                    // takes damage. BattleDestructible uses its mesh/materials
                    // when it swaps the intact renderer.
                    renderer.enabled = false;
                    continue;
                }

                objects.RendererCount++;
                var filter = renderer.GetComponent<MeshFilter>();
                var mesh = filter == null ? null : filter.sharedMesh;
                if (mesh == null)
                {
                    continue;
                }

                if (clientNode != null)
                {
                    // The client scene keeps sky, water, shadow hulls and
                    // leaf cards out of the collision mesh it prepared.
                    objects.TriangleCount += (int)CountTriangles(mesh);
                    if (clientNode.CollisionMesh != null &&
                        renderer.GetComponent<Collider>() == null)
                    {
                        if (IsFloorPlainName(renderer.gameObject.name))
                        {
                            AddFloorSlab(renderer.gameObject, clientNode.CollisionMesh);
                            objects.FloorSlabCount++;
                            objects.ColliderCount++;
                            continue;
                        }
                        var nodeCollider = renderer.gameObject.AddComponent<MeshCollider>();
                        nodeCollider.convex = !foliage &&
                            CooksConvex(clientNode.CollisionMesh, renderer.transform.lossyScale);
                        nodeCollider.sharedMesh = clientNode.CollisionMesh;
                        objects.ColliderCount++;
                    }
                    continue;
                }

                objects.TriangleCount += mesh.triangles.Length / 3;
                if (IsSkyName(renderer.gameObject.name))
                {
                    PushSkyToTheHorizon(renderer.transform, mesh);
                }
                if (!passThrough && renderer.GetComponent<Collider>() == null)
                {
                    var collider = renderer.gameObject.AddComponent<MeshCollider>();
                    collider.convex = !foliage &&
                        CooksConvex(mesh, renderer.transform.lossyScale);
                    collider.sharedMesh = mesh;
                    objects.ColliderCount++;
                }
            }

            if (destructibles != null)
            {
                for (var i = 0; i < destructibles.Count; i++)
                {
                    var info = destructibles[i];
                    if (IsMapBorderName(info.IntactGroup))
                    {
                        continue;
                    }

                    var intactRenderer = FindExternalRenderer(
                        renderers, info.IntactGroup);
                    if (intactRenderer == null)
                    {
                        throw new InvalidOperationException(
                            "client destructible intact group was not imported: " +
                            info.IntactGroup);
                    }

                    var intactFilter = intactRenderer.GetComponent<MeshFilter>();
                    if (intactFilter == null || intactFilter.sharedMesh == null)
                    {
                        throw new InvalidOperationException(
                            "client destructible has no intact mesh: " + info.IntactGroup);
                    }

                    var intactCollider = intactRenderer.GetComponent<MeshCollider>();
                    if (intactCollider == null)
                    {
                        intactCollider = intactRenderer.gameObject.AddComponent<MeshCollider>();
                        intactCollider.convex = CooksConvex(
                            intactFilter.sharedMesh,
                            intactRenderer.transform.lossyScale);
                        intactCollider.sharedMesh = intactFilter.sharedMesh;
                        objects.ColliderCount++;
                    }

                    var wreckRenderer = FindExternalRenderer(
                        renderers, info.WreckGroup);
                    var wreckFilter = wreckRenderer != null
                        ? wreckRenderer.GetComponent<MeshFilter>() : null;
                    var wreckMesh = wreckFilter != null ? wreckFilter.sharedMesh : null;
                    var wreckMaterials = wreckRenderer != null
                        ? wreckRenderer.sharedMaterials : null;

                    var destructible = intactRenderer.gameObject
                        .AddComponent<BattleDestructible>();
                    destructible.BindExternal(
                        info.Name,
                        info.Health,
                        intactFilter,
                        intactRenderer,
                        intactCollider,
                        wreckMesh,
                        wreckMaterials);
                    AddRamTrigger(intactRenderer, intactFilter, destructible, objects);
                    objects.DestructibleCount++;
                }
            }

            if (trees != null && trees.Count > 0)
            {
                var byName = new Dictionary<string, MeshRenderer>(StringComparer.Ordinal);
                for (var i = 0; i < renderers.Length; i++)
                {
                    if (renderers[i] != null)
                    {
                        byName[renderers[i].gameObject.name] = renderers[i];
                    }
                }

                for (var i = 0; i < trees.Count; i++)
                {
                    MeshRenderer treeRenderer;
                    if (!byName.TryGetValue(trees[i].Group, out treeRenderer))
                    {
                        throw new InvalidOperationException(
                            "client tree group was not imported: " + trees[i].Group);
                    }

                    SpawnExternalTree(objects, treeRenderer, trees[i]);
                }
            }

            return objects;
        }

        /// <summary>map_object_tree.gd's default fall_speed.</summary>
        public const float ExternalTreeFallSpeed = 0.2f;

        /// <summary>The trunk sensor never gets thinner or thicker than this.</summary>
        private const float ExternalTreeMinRadius = 0.3f;
        private const float ExternalTreeMaxRadius = 1.2f;

        /// <summary>How tall the trunk sensor reaches above the tree's foot.</summary>
        private const float ExternalTreeSensorHeight = 4f;

        /// <summary>
        /// Re-roots a world-baked client tree node under a pivot at its foot,
        /// so <see cref="BattleTree"/> can tip it over exactly as it tips the
        /// Godot maps' trees, and gives it the layer-0/mask-2 trunk sensor of
        /// <c>tree_base.tscn</c>: a trigger on the tank's Physics layer.
        /// </summary>
        private static void SpawnExternalTree(
            BattleObjects objects, MeshRenderer renderer, ExternalMapTree info)
        {
            var foot = new Vector3(info.Origin[0], info.Origin[1], info.Origin[2]);
            var parent = renderer.transform.parent;
            var root = new GameObject("tree_" + renderer.gameObject.name);
            root.layer = BattleLayers.Foliage;
            root.transform.SetParent(parent, false);
            root.transform.position = foot;

            var trunk = new GameObject("trunk");
            trunk.layer = BattleLayers.Foliage;
            trunk.transform.SetParent(root.transform, false);
            renderer.transform.SetParent(trunk.transform, true);

            var tree = root.AddComponent<BattleTree>();
            tree.Bind(trunk.transform, ExternalTreeFallSpeed);

            var node = renderer.GetComponent<ClientMapNode>();
            var filter = renderer.GetComponent<MeshFilter>();
            var shape = node != null && node.CollisionMesh != null
                ? node.CollisionMesh
                : filter != null ? filter.sharedMesh : null;
            var radius = ExternalTreeTrunkRadius(shape, renderer.transform, foot);

            var sensor = new GameObject("trunk_sensor");
            sensor.layer = BattleLayers.Physics;
            sensor.transform.SetParent(root.transform, false);
            var capsule = sensor.AddComponent<CapsuleCollider>();
            capsule.direction = 1;
            capsule.radius = radius;
            capsule.height = Mathf.Max(ExternalTreeSensorHeight, radius * 2f);
            capsule.center = new Vector3(0f, capsule.height * 0.5f, 0f);
            capsule.isTrigger = true;
            sensor.AddComponent<BattleTreeTrigger>().Bind(tree);
            objects.ColliderCount++;
            objects.TriggerCount++;
            objects.TreeCount++;
        }

        /// <summary>
        /// The horizontal reach of the trunk geometry within the sensor's
        /// height above the foot; crowns and leaning tops are left out.
        /// </summary>
        public static float ExternalTreeTrunkRadius(
            Mesh shape, Transform space, Vector3 foot)
        {
            if (shape == null || space == null)
            {
                return ExternalTreeMinRadius;
            }

            var vertices = shape.vertices;
            var reach = 0f;
            for (var i = 0; i < vertices.Length; i++)
            {
                var point = space.TransformPoint(vertices[i]);
                var rise = point.y - foot.y;
                if (rise < -0.5f || rise > 1.5f)
                {
                    continue;
                }

                var dx = point.x - foot.x;
                var dz = point.z - foot.z;
                reach = Mathf.Max(reach, Mathf.Sqrt(dx * dx + dz * dz));
            }

            return Mathf.Clamp(reach, ExternalTreeMinRadius, ExternalTreeMaxRadius);
        }

        private static long CountTriangles(Mesh mesh)
        {
            long total = 0;
            for (var i = 0; i < mesh.subMeshCount; i++)
            {
                total += mesh.GetIndexCount(i) / 3;
            }
            return total;
        }

        private static ExternalMapDestructible ExternalForGroup(
            IList<ExternalMapDestructible> destructibles,
            string rendererName,
            bool wreck)
        {
            if (destructibles == null || string.IsNullOrEmpty(rendererName))
            {
                return null;
            }

            for (var i = 0; i < destructibles.Count; i++)
            {
                var group = wreck
                    ? destructibles[i].WreckGroup
                    : destructibles[i].IntactGroup;
                if (!string.IsNullOrEmpty(group) &&
                    rendererName.IndexOf(group, StringComparison.Ordinal) >= 0)
                {
                    return destructibles[i];
                }
            }

            return null;
        }

        private static MeshRenderer FindExternalRenderer(
            MeshRenderer[] renderers,
            string group)
        {
            if (string.IsNullOrEmpty(group))
            {
                return null;
            }

            for (var i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null &&
                    renderers[i].gameObject.name.IndexOf(
                        group, StringComparison.Ordinal) >= 0)
                {
                    return renderers[i];
                }
            }

            return null;
        }

        private static void AddRamTrigger(
            MeshRenderer renderer,
            MeshFilter filter,
            BattleDestructible destructible,
            BattleObjects objects)
        {
            var bounds = filter.sharedMesh.bounds;
            var area = new GameObject("ram_area");
            area.layer = BattleLayers.Physics;
            area.transform.SetParent(renderer.transform, false);
            area.transform.localPosition = bounds.center;
            var trigger = area.AddComponent<SphereCollider>();
            trigger.radius = Mathf.Max(bounds.extents.magnitude, 0.1f);
            trigger.isTrigger = true;
            area.AddComponent<BattleDestructibleTrigger>().Bind(destructible);
            objects.ColliderCount++;
            objects.TriggerCount++;
        }

        /// <summary>
        /// `.mtl` cannot express back-face culling or alpha testing, so the
        /// porter records the client effect in the material name:
        /// `Textured.Opaque.NoCull` becomes a `_nocull` suffix and any
        /// `*.Alphatest` effect a `_cutout` one. Without this every fence,
        /// plank and leaf card is drawn as an opaque, single-sided quad, which
        /// is what leaves a building looking open from one side.
        /// </summary>
        public const string TwoSidedMark = "_nocull";

        public const string CutoutMark = "_cutout";

        public const string BlendMark = "_blend";

        public static bool IsTwoSidedName(string name)
        {
            return !string.IsNullOrEmpty(name) &&
                name.IndexOf(TwoSidedMark, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool IsCutoutName(string name)
        {
            return !string.IsNullOrEmpty(name) &&
                name.IndexOf(CutoutMark, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool IsBlendedName(string name)
        {
            return !string.IsNullOrEmpty(name) &&
                name.IndexOf(BlendMark, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>The imported OBJ material with the authored effect on it.</summary>
        private Material ClientMaterial(Material source, bool sky)
        {
            var name = source.name;
            Material patched;
            if (_clientMaterials.TryGetValue(name, out patched))
            {
                return patched;
            }

            // The OBJ importer builds URP Lit materials, and URP Lit reads
            // Unity's built-in linear fog, which reaches pure fog colour at
            // its far distance. The terrain reads the authored DAVA curve
            // through BlitzFog instead, so leaving the props on URP Lit
            // whitened every distant building while the ground stayed clear.
            var shaderName = sky ? UnlitShaderName : LitShaderName;
            var shader = Shader.Find(shaderName);
            if (shader == null)
            {
                throw new InvalidOperationException(
                    "required map shader is unavailable: " + shaderName);
            }

            patched = new Material(shader) { name = name + " (client)" };
            patched.mainTexture = source.mainTexture;
            patched.SetColor("_BaseColor", Color.white);
            if (!sky)
            {
                if (IsTwoSidedName(name))
                {
                    patched.SetFloat("_Cull", (float)CullMode.Off);
                    patched.doubleSidedGI = true;
                }

                if (IsCutoutName(name))
                {
                    patched.SetFloat("_AlphaClip", 1f);
                    patched.SetFloat("_Cutoff", 0.5f);
                    patched.EnableKeyword("_ALPHATEST_ON");
                    patched.SetOverrideTag("RenderType", "TransparentCutout");
                    patched.renderQueue = (int)RenderQueue.AlphaTest;
                }

                if (IsBlendedName(name))
                {
                    patched.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                    patched.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                    patched.SetFloat("_ZWrite", 0f);
                    patched.SetFloat("_Blended", 1f);
                    patched.EnableKeyword("_ALPHAPREMULTIPLY_ON");
                    patched.SetOverrideTag("RenderType", "Transparent");
                    patched.renderQueue = (int)RenderQueue.Transparent;
                }
            }

            _clientMaterials[name] = patched;
            return patched;
        }

        /// <summary>
        /// The distance the Godot maps place their sky planes at. The client
        /// instead draws SkyFlattenSphere locked to the camera at the far
        /// plane, so its authored ~10 m dome has to be pushed out to the same
        /// horizon or it would sit in the middle of the battlefield.
        /// </summary>
        public const float SkyRadius = 5000f;

        public static bool IsSkyName(string name)
        {
            return !string.IsNullOrEmpty(name) &&
                name.IndexOf("sky", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Scales the authored dome about the map centre it is modelled around.
        /// Nothing is redrawn: the same mesh simply covers the whole sky.
        /// </summary>
        public static void PushSkyToTheHorizon(Transform sky, Mesh mesh)
        {
            if (sky == null || mesh == null)
            {
                return;
            }

            var extents = mesh.bounds.extents;
            var radius = Mathf.Max(extents.x, extents.z);
            if (radius <= 0.001f)
            {
                return;
            }

            var scale = SkyRadius / radius;
            sky.localScale = Vector3.one * scale;

            // Ordeal models its dome around the origin, but Karieri parks
            // SkyOldDeepHarizon off to one side of the map. Scaling by ~500
            // multiplies that offset too, which throws the sky kilometres past
            // the border, so the dome is pulled back over the map centre. Only
            // the horizontal axes move: the authored height of the horizon is
            // what both maps are already drawn against.
            var centre = mesh.bounds.center;
            var position = sky.localPosition;
            sky.localPosition = new Vector3(
                -centre.x * scale, position.y, -centre.z * scale);
        }

        /// <summary>
        /// Names the client gives to things a shell and a tank must pass
        /// through: the sky dome, the baked ground decals under props, the
        /// painted paths, the selection rings and the glow cards. Cooking a
        /// collider for any of them would put an invisible wall on the map.
        /// </summary>
        private static readonly string[] PassThroughNames =
        {
            "sky",
            "shadow",
            "glow",
            "path0",
            "select0",
            "watersurface",
        };

        /// <summary>
        /// The client fences the edge of the play area with chains of
        /// <c>env_ord_restriction</c> panels: a translucent pane about 15 m
        /// wide carrying a red no-entry sign on both faces. The boundary they
        /// advertise is gameplay state, not geometry — <c>BattleTank</c> clamps
        /// the hull to <c>MapDefinition.BorderMin/Max</c> — so the panels only
        /// stand between the camera and the map, and their colliders fenced
        /// off ground the tanks are allowed to use.
        /// </summary>
        private const string MapBorderName = "env_ord_restriction";

        /// <summary>
        /// Sea of Ducks (4.8.0) floats its WarDucks on invisible
        /// <c>CollisionPlain</c> tiles half a metre under the surf. Cooked as a
        /// triangle mesh such a tile is a one-sided sheet: the wheel rays and
        /// the hull went straight through it and the ducks sank to the sea
        /// bed. So the tile becomes a solid slab whose top face is the tile.
        /// </summary>
        public const string FloorPlainName = "collisionplain";

        /// <summary>Depth of a floor slab, below the deepest sea bed of the arena.</summary>
        public const float FloorSlabDepth = 20f;

        public static bool IsFloorPlainName(string name)
        {
            return !string.IsNullOrEmpty(name) &&
                name.IndexOf(FloorPlainName, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void AddFloorSlab(GameObject go, Mesh mesh)
        {
            var bounds = mesh.bounds;
            var up = go.transform.TransformVector(Vector3.up);
            var sign = up.y >= 0f ? 1f : -1f;
            var depth = FloorSlabDepth / Mathf.Max(Mathf.Abs(up.y), 1e-4f);
            var top = sign > 0f ? bounds.max.y : bounds.min.y;
            var box = go.AddComponent<BoxCollider>();
            box.size = new Vector3(bounds.size.x, depth, bounds.size.z);
            box.center = new Vector3(bounds.center.x, top - sign * depth * 0.5f, bounds.center.z);
        }

        /// <summary>Whether the group is one of those border panels.</summary>
        public static bool IsMapBorderName(string name)
        {
            return !string.IsNullOrEmpty(name) &&
                name.IndexOf(MapBorderName, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Drops the border panels before anything binds to them. They are
        /// removed here rather than filtered out of the exported scene so the
        /// client OBJ stays a faithful copy of 4.8.0, and removed rather than
        /// hidden because a disabled renderer would keep its collider and its
        /// destructible state group.
        /// </summary>
        private static MeshRenderer[] DiscardMapBorder(
            MeshRenderer[] renderers, BattleObjects objects)
        {
            var kept = new List<MeshRenderer>(renderers.Length);
            for (var i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null)
                {
                    continue;
                }

                if (IsMapBorderName(renderer.gameObject.name))
                {
                    objects.BorderPanelCount++;
                    Discard(renderer.gameObject);
                    continue;
                }

                kept.Add(renderer);
            }

            return kept.ToArray();
        }

        private static bool IsPassThroughName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            for (var i = 0; i < PassThroughNames.Length; i++)
            {
                if (name.IndexOf(PassThroughNames[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The biggest a prop may measure in any axis. Past this it is a
        /// landmark - a cliff, a great wall - and a convex hull of it would
        /// bulge over ground the tanks are meant to use.
        /// </summary>
        private const float ConvexPropSpan = 30f;

        /// <summary>
        /// The biggest bounding volume a prop may fill. A building is hollow
        /// and the tanks drive into it, so it keeps its authored shape.
        /// </summary>
        private const float ConvexPropVolume = 150f;

        /// <summary>
        /// A panel this thin - a plank fence, a concrete slab, a sign - is a
        /// wall however long it runs, and its hull is the wall itself.
        /// </summary>
        private const float ConvexPanelThickness = 1.5f;

        /// <summary>A flat card has no volume to cook a hull from.</summary>
        private const float ConvexCardThickness = 0.05f;

        /// <summary>
        /// Whether a prop's collider should be cooked convex.
        ///
        /// The client authors a prop as render geometry: a wooden fence is a
        /// row of separate planks, a crate stack is a dozen loose boxes, a
        /// rock is a dented shell. Cooked concave, that soup hands the hull
        /// dozens of thin interior ledges to catch on, and a tank that nudges
        /// one stops dead or climbs it instead of sliding off - the rocks and
        /// fences players report getting stuck in. A convex hull keeps the
        /// prop's silhouette and lets the hull slide the way the client does.
        /// Only geometry big enough to be architecture keeps the exact shape.
        /// </summary>
        public static bool IsSolidRock(string source)
        {
            if (string.IsNullOrEmpty(source)) return false;
            var file = System.IO.Path.GetFileNameWithoutExtension(source);
            // plentyOfStones is a group with intentional gaps, not one boulder.
            return file.StartsWith("stone_ss_", StringComparison.OrdinalIgnoreCase);
        }

        private static bool CooksConvex(Mesh mesh, Vector3 scale)
        {
            if (mesh == null)
            {
                return false;
            }

            var size = mesh.bounds.size;
            var x = Mathf.Abs(size.x * scale.x);
            var y = Mathf.Abs(size.y * scale.y);
            var z = Mathf.Abs(size.z * scale.z);
            var largest = Mathf.Max(x, Mathf.Max(y, z));
            var smallest = Mathf.Min(x, Mathf.Min(y, z));
            if (smallest < ConvexCardThickness || largest > ConvexPropSpan)
            {
                return false;
            }

            if (x * y * z > ConvexPropVolume && smallest > ConvexPanelThickness)
            {
                return false;
            }

            // A plane turned inside its own mesh space (Lost Temple's
            // invisible walls) has a thick bounding box but no volume, and
            // PhysX refuses to build a hull from coplanar points.
            return !IsFlat(mesh, scale);
        }

        /// <summary>
        /// Whether every vertex lies within ConvexCardThickness of the plane
        /// of the mesh's first non-degenerate triangle, after scaling.
        /// </summary>
        private static bool IsFlat(Mesh mesh, Vector3 scale)
        {
            var vertices = mesh.vertices;
            var triangles = mesh.triangles;
            for (var t = 0; t + 2 < triangles.Length; t += 3)
            {
                var a = Vector3.Scale(vertices[triangles[t]], scale);
                var b = Vector3.Scale(vertices[triangles[t + 1]], scale);
                var c = Vector3.Scale(vertices[triangles[t + 2]], scale);
                var normal = Vector3.Cross(b - a, c - a);
                if (normal.sqrMagnitude < 1e-8f)
                {
                    continue;
                }

                normal.Normalize();
                for (var i = 0; i < vertices.Length; i++)
                {
                    var offset = Vector3.Dot(Vector3.Scale(vertices[i], scale) - a, normal);
                    if (Mathf.Abs(offset) >= ConvexCardThickness)
                    {
                        return false;
                    }
                }

                return true;
            }

            // Only degenerate triangles: nothing a hull could enclose.
            return true;
        }

        private static bool IsFoliageName(string name)
        {
            return !string.IsNullOrEmpty(name) &&
                (name.IndexOf("tree", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 name.IndexOf("bush", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 name.IndexOf("grass", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 name.IndexOf("leaf", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 name.IndexOf("vegetation", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static void SetLayerRecursively(GameObject root, int layer)
        {
            root.layer = layer;
            for (var i = 0; i < root.transform.childCount; i++)
            {
                SetLayerRecursively(root.transform.GetChild(i).gameObject, layer);
            }
        }

        /// <summary>
        /// Meshes and materials built at runtime belong to no scene, so they
        /// outlive the battle unless they are released with it.
        /// </summary>
        public void Release()
        {
            _sceneNodes = null;
            _sceneTrunks.Clear();
            if (Root != null)
            {
                Discard(Root);
                Root = null;
            }

            foreach (var mesh in _meshes.Values)
            {
                Discard(mesh);
            }

            foreach (var mesh in _billboards.Values)
            {
                Discard(mesh);
            }

            foreach (var mesh in _colliders.Values)
            {
                Discard(mesh);
            }

            foreach (var material in _materials.Values)
            {
                Discard(material);
            }

            _meshes.Clear();
            _billboards.Clear();
            _colliders.Clear();
            _materials.Clear();
            _aiCovers.Clear();
            // The atlas materials belong to the bake, which releases them.
            _bakedMaterials.Clear();
        }

        /// <summary>
        /// Destroy is deferred to the end of the frame, which never arrives
        /// in an edit-mode test, so the tests would leak everything they build.
        /// </summary>
        private static void Discard(UnityEngine.Object target)
        {
            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(target);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        /// <summary>
        /// Builds the Godot render scene. Its nodes are named
        /// <c>object/path</c> after the gameplay entry they belong to, so each
        /// one is handed to that entry below instead of the table's meshes.
        /// </summary>
        private void LoadScene(string resource)
        {
            Scene = ClientMapScene.Build(Root.transform, resource);
            Scene.gameObject.name = "Scene";
            _sceneNodes = new Dictionary<string, List<ClientMapNode>>(StringComparer.Ordinal);
            for (var i = 0; i < Scene.Nodes.Count; i++)
            {
                var node = Scene.Nodes[i];
                var key = node.Info.@object ?? string.Empty;
                List<ClientMapNode> list;
                if (!_sceneNodes.TryGetValue(key, out list))
                {
                    list = new List<ClientMapNode>();
                    _sceneNodes.Add(key, list);
                }
                list.Add(node);
            }
            LightmappedCount = Scene.LightmappedCount;
            TriangleCount += Scene.TriangleCount;
        }

        public const string SceneTrunkPath = "trunk";

        /// <summary>
        /// Hangs an object's scene nodes under its gameplay body. A tree's
        /// trunk and everything below it follow the BattleTree trunk.
        /// Returns the nodes of the object (empty when it has none).
        /// </summary>
        private List<ClientMapNode> AttachScene(GameObject go, string name, Transform trunk)
        {
            List<ClientMapNode> nodes;
            if (!_sceneNodes.TryGetValue(name, out nodes))
            {
                return new List<ClientMapNode>();
            }

            if (trunk != null)
            {
                _sceneTrunks[name] = trunk;
            }

            for (var i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                var relative = RelativePath(node.Info.name);
                var followsTrunk = trunk != null &&
                    (relative == SceneTrunkPath || relative.StartsWith(SceneTrunkPath + "/", StringComparison.Ordinal));
                node.transform.SetParent(followsTrunk ? trunk : go.transform, true);
                node.gameObject.layer = go.layer;
                RendererCount++;
                if (node.Info.role == "leaves")
                {
                    MultiMeshRendererCount++;
                    BillboardQuadCount += node.Info.vertexCount / 4;
                }
                if (node.Info.role == "water") WaterCount++;
                if (node.Info.role == "decoration") DecorationCount++;
            }

            return nodes;
        }

        private static string RelativePath(string name)
        {
            var slash = name.IndexOf('/');
            return slash < 0 ? string.Empty : name.Substring(slash + 1);
        }

        private static Renderer[] RenderersWithRole(List<ClientMapNode> nodes, string role)
        {
            var result = new List<Renderer>();
            for (var i = 0; i < nodes.Count; i++)
            {
                if (nodes[i].Info.role == role)
                {
                    result.Add(nodes[i].GetComponent<MeshRenderer>());
                }
            }
            return result.ToArray();
        }

        /// <summary>
        /// The vegetation LOD groups, each measured from its
        /// map_object_vegetation.gd node, carried by the trunk when the group
        /// sits below one.
        /// </summary>
        private void FinishScene()
        {
            VegetationLods = Root.AddComponent<BattleVegetationLods>();
            var groups = new Dictionary<string, List<ClientMapNode>>(StringComparer.Ordinal);
            for (var i = 0; i < Scene.Nodes.Count; i++)
            {
                var node = Scene.Nodes[i];
                if (string.IsNullOrEmpty(node.Info.lodGroup))
                {
                    continue;
                }
                List<ClientMapNode> list;
                if (!groups.TryGetValue(node.Info.lodGroup, out list))
                {
                    list = new List<ClientMapNode>();
                    groups.Add(node.Info.lodGroup, list);
                }
                list.Add(node);
            }

            foreach (var pair in groups)
            {
                var first = pair.Value[0];
                var origin = new GameObject("lod_origin").transform;
                var p = first.Info.lodOrigin;
                origin.position = new Vector3(p[0], p[1], p[2]);
                Transform trunk;
                var relative = RelativePath(pair.Key);
                var parent = (relative == SceneTrunkPath ||
                              relative.StartsWith(SceneTrunkPath + "/", StringComparison.Ordinal)) &&
                    _sceneTrunks.TryGetValue(first.Info.@object, out trunk)
                    ? trunk
                    : first.transform.parent;
                origin.SetParent(parent, true);
                VegetationLods.Add(
                    origin,
                    RenderersWithRole(pair.Value, "leaves"),
                    RenderersWithRole(pair.Value, "lod"));
            }
        }

        private void Spawn(MapObjectInstance instance)
        {
            var go = new GameObject(instance.Name);
            go.layer = LayerFor(instance.Kind);
            go.transform.SetParent(Root.transform, false);
            Place(go.transform, instance.Origin, instance.Basis);
            InstanceCount++;

            switch (instance.Kind)
            {
                case MapObjectKind.Static:
                case MapObjectKind.Destructible:
                case MapObjectKind.Water:
                case MapObjectKind.Decoration:
                    SpawnSimple(go, instance);
                    break;
                case MapObjectKind.Tree:
                    SpawnTree(go, instance);
                    break;
                case MapObjectKind.Vegetation:
                    SpawnVegetation(go, instance);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            if (instance.Kind == MapObjectKind.Static && instance.NavigationObstacle)
            {
                var cover = BattleAiCover.Create(go, instance);
                if (cover != null)
                {
                    _aiCovers.Add(cover);
                }
            }
        }

        private void SpawnSimple(GameObject go, MapObjectInstance instance)
        {
            if (_sceneNodes != null)
            {
                SpawnSceneSimple(go, instance);
                return;
            }

            var source = _library.Get(instance.Mesh);
            var filter = go.AddComponent<MeshFilter>();
            var renderer = go.AddComponent<MeshRenderer>();
            var bakedPath = instance.BakedPath;
            if (_lighting != null && !string.IsNullOrEmpty(bakedPath) &&
                _lighting.Has(bakedPath))
            {
                // The bake owns this prop: it carries the lightmap unwrap the
                // object payload has no channel for, so the geometry comes
                // from the lighting payload instead.
                var entry = _lighting.Entry(bakedPath);
                filter.sharedMesh = _lighting.MeshFor(entry.Meshes[0]);
                renderer.sharedMaterial = BakedMaterialFor(instance.Material);
                _lighting.Bind(renderer, entry);
                LightmappedCount++;
            }
            else
            {
                filter.sharedMesh = RenderMesh(source);
                renderer.sharedMaterial = MaterialFor(instance.Material);
                // Water and the distant backdrop do not cast into gameplay;
                // regular props participate in the one realtime source light.
                renderer.shadowCastingMode =
                    instance.Kind == MapObjectKind.Water ||
                    instance.Kind == MapObjectKind.Decoration
                    ? ShadowCastingMode.Off
                    : ShadowCastingMode.On;
                renderer.receiveShadows = instance.Kind != MapObjectKind.Decoration;
            }

            RendererCount++;
            if (instance.Kind == MapObjectKind.Water) WaterCount++;
            if (instance.Kind == MapObjectKind.Decoration) DecorationCount++;
            TriangleCount += source.TriangleCount;

            MeshCollider collider = null;
            if (instance.Shape != null)
            {
                // Every simple prop that carries a shape is immovable, so the
                // source concave soups port straight across as non-convex meshes.
                collider = go.AddComponent<MeshCollider>();
                var collisionMesh = CollisionMesh(_shapes.Get(instance.Shape));
                // Rudniki boulders exceed the generic prop volume cutoff by
                // an order of magnitude. They are solid rocks, not hollow
                // buildings: keep a closed hull regardless of that cutoff.
                collider.convex = IsSolidRock(instance.Source)
                    || CooksConvex(collisionMesh, go.transform.lossyScale);
                collider.sharedMesh = collisionMesh;
                if (instance.Kind == MapObjectKind.Static &&
                    (IsSolidRock(instance.Source) || string.Equals(
                        System.IO.Path.GetFileNameWithoutExtension(instance.Source),
                        "plentyOfStones", StringComparison.OrdinalIgnoreCase)))
                    go.AddComponent<BattleRockSupport>();
                ColliderCount++;
            }

            if (!instance.IsDestructible)
            {
                return;
            }

            var wreckCollision = instance.ShapeDestroyed == null
                ? null
                : CollisionMesh(_shapes.Get(instance.ShapeDestroyed));
            var destructible = go.AddComponent<BattleDestructible>();
            destructible.Bind(
                instance,
                filter,
                renderer,
                collider,
                RenderMesh(_library.Get(instance.MeshDestroyed)),
                wreckCollision,
                MaterialFor(instance.Material));

            // colshape_area_3d reuses colshape_normal in the source. Ten
            // Rudniki props intentionally have no shape, so they get neither
            // the shell collider above nor an invented tank trigger here.
            if (collider != null)
            {
                var area = new GameObject("ram_area");
                area.layer = BattleLayers.Physics;
                area.transform.SetParent(go.transform, false);
                var trigger = area.AddComponent<MeshCollider>();
                trigger.sharedMesh = collider.sharedMesh;
                // Unity forbids concave trigger meshes. Convex cooking keeps the
                // source outline while the exact concave copy remains beside it
                // for projectile traces.
                trigger.convex = true;
                trigger.isTrigger = true;
                area.AddComponent<BattleDestructibleTrigger>().Bind(destructible);
                ColliderCount++;
                TriggerCount++;
            }

            DestructibleCount++;
        }

        /// <summary>
        /// SpawnSimple for a map drawn by its Godot render scene: the same
        /// colliders, ram trigger and destructible, the scene's nodes as the
        /// visuals (intact and wreck swap on Break).
        /// </summary>
        private void SpawnSceneSimple(GameObject go, MapObjectInstance instance)
        {
            var nodes = AttachScene(go, instance.Name, null);

            MeshCollider collider = null;
            if (instance.Shape != null)
            {
                collider = go.AddComponent<MeshCollider>();
                var collisionMesh = CollisionMesh(_shapes.Get(instance.Shape));
                collider.convex = IsSolidRock(instance.Source)
                    || CooksConvex(collisionMesh, go.transform.lossyScale);
                collider.sharedMesh = collisionMesh;
                if (instance.Kind == MapObjectKind.Static &&
                    (IsSolidRock(instance.Source) || string.Equals(
                        System.IO.Path.GetFileNameWithoutExtension(instance.Source),
                        "plentyOfStones", StringComparison.OrdinalIgnoreCase)))
                    go.AddComponent<BattleRockSupport>();
                ColliderCount++;
            }

            if (!instance.IsDestructible)
            {
                return;
            }

            var wreckCollision = instance.ShapeDestroyed == null
                ? null
                : CollisionMesh(_shapes.Get(instance.ShapeDestroyed));
            var destructible = go.AddComponent<BattleDestructible>();
            destructible.Bind(instance, null, null, collider, null, wreckCollision, null);
            destructible.BindSceneVisuals(
                RenderersWithRole(nodes, "intact"), RenderersWithRole(nodes, "wreck"));

            if (collider != null)
            {
                var area = new GameObject("ram_area");
                area.layer = BattleLayers.Physics;
                area.transform.SetParent(go.transform, false);
                var trigger = area.AddComponent<MeshCollider>();
                trigger.sharedMesh = collider.sharedMesh;
                trigger.convex = true;
                trigger.isTrigger = true;
                area.AddComponent<BattleDestructibleTrigger>().Bind(destructible);
                ColliderCount++;
                TriggerCount++;
            }

            DestructibleCount++;
        }

        private void SpawnVegetation(GameObject go, MapObjectInstance instance)
        {
            if (_sceneNodes != null)
            {
                AttachScene(go, instance.Name, null);
                for (var i = 0; i < instance.Colliders.Count; i++)
                {
                    SpawnPrimitive(go.transform, instance.Colliders[i], null);
                }
                VegetationCount++;
                return;
            }

            for (var i = 0; i < instance.Parts.Count; i++)
            {
                SpawnPart(go.transform, instance.Parts[i]);
            }

            for (var i = 0; i < instance.Colliders.Count; i++)
            {
                SpawnPrimitive(go.transform, instance.Colliders[i], null);
            }

            VegetationCount++;
        }

        private void SpawnTree(GameObject go, MapObjectInstance instance)
        {
            var trunk = new GameObject("trunk");
            trunk.layer = BattleLayers.Foliage;
            trunk.transform.SetParent(go.transform, false);

            if (_sceneNodes != null)
            {
                AttachScene(go, instance.Name, trunk.transform);
            }
            else
            {
                for (var i = 0; i < instance.Parts.Count; i++)
                {
                    var part = instance.Parts[i];
                    SpawnPart(part.FollowsTrunk ? trunk.transform : go.transform, part);
                }
            }

            var tree = go.AddComponent<BattleTree>();
            tree.Bind(trunk.transform, instance.FallSpeed);
            for (var i = 0; i < instance.Colliders.Count; i++)
            {
                var source = instance.Colliders[i];
                SpawnPrimitive(
                    source.FollowsTrunk ? trunk.transform : go.transform,
                    source,
                    tree);
            }

            TreeCount++;
        }

        private void SpawnPart(Transform parent, MapObjectPart part)
        {
            var go = new GameObject(part.Name);
            go.layer = BattleLayers.Foliage;
            go.transform.SetParent(parent, false);
            Place(go.transform, part.Origin, part.Basis);

            Mesh mesh;
            var triangles = 0;
            if (part.Mesh != null)
            {
                var source = _library.Get(part.Mesh);
                mesh = RenderMesh(source);
                triangles = source.TriangleCount;
            }
            else
            {
                var source = _table.GetMultiMesh(part.MultiMesh);
                mesh = BillboardMesh(source);
                triangles = source.Count * 2;
                BillboardQuadCount += source.Count;
                MultiMeshRendererCount++;
            }

            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            var material = MaterialFor(part.Material);
            renderer.sharedMaterial = material;
            // Alpha-tested leaves and billboard clusters use the custom
            // ShadowCaster pass, so their silhouettes reach the ground instead
            // of turning every tree into a floating unlit card.
            renderer.shadowCastingMode = ShadowCastingMode.On;
            renderer.receiveShadows = true;
            RendererCount++;
            TriangleCount += triangles;
        }

        private Collider SpawnPrimitive(
            Transform parent, MapPrimitiveCollider source, BattleTree tree)
        {
            var go = new GameObject(source.Name);
            // A tree's Area3D has layer 0 / mask 2 and must still see the tank;
            // bushes and leaf colliders live on layer 6 / mask 0 and must not.
            // Put only the directional Godot sensor on the tank's Unity layer,
            // leaving authored foliage volumes isolated by the matrix above.
            go.layer = source.IsTrigger
                ? BattleLayers.Physics
                : BattleLayers.Foliage;
            go.transform.SetParent(parent, false);
            Place(go.transform, source.Origin, source.Basis);

            Collider collider;
            switch (source.Kind)
            {
                case MapPrimitiveKind.Capsule:
                    var capsule = go.AddComponent<CapsuleCollider>();
                    capsule.radius = source.Radius;
                    capsule.height = Mathf.Max(source.Height, source.Radius * 2f);
                    capsule.direction = 1;
                    collider = capsule;
                    break;
                case MapPrimitiveKind.Sphere:
                    var sphere = go.AddComponent<SphereCollider>();
                    sphere.radius = source.Radius;
                    collider = sphere;
                    break;
                case MapPrimitiveKind.Box:
                    var box = go.AddComponent<BoxCollider>();
                    box.size = new Vector3(source.Size[0], source.Size[1], source.Size[2]);
                    collider = box;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            collider.isTrigger = source.IsTrigger;
            ColliderCount++;
            if (source.IsTrigger)
            {
                TriggerCount++;
                if (tree != null)
                {
                    go.AddComponent<BattleTreeTrigger>().Bind(tree);
                }
            }

            return collider;
        }

        private static int LayerFor(MapObjectKind kind)
        {
            if (kind == MapObjectKind.Destructible)
            {
                return BattleLayers.Destructibles;
            }

            if (kind == MapObjectKind.Tree || kind == MapObjectKind.Vegetation)
            {
                return BattleLayers.Foliage;
            }

            return BattleLayers.Static;
        }

        /// <summary>
        /// Every authored object basis is square, unmirrored and free of shear,
        /// so one rotation and the three column lengths reproduce it exactly.
        /// The hangar's baked instances need the very same placement, so the
        /// maths lives in SourcePlacement and both scenes share one copy.
        /// </summary>
        private static void Place(Transform target, float[] origin, float[] basis)
        {
            SourcePlacement.Place(target, origin, basis);
        }

        private Mesh RenderMesh(MapMesh source)
        {
            Mesh found;
            if (_meshes.TryGetValue(source.Name, out found))
            {
                return found;
            }

            var mesh = BuildMesh(source);
            _meshes.Add(source.Name, mesh);
            return mesh;
        }

        private Mesh BillboardMesh(MapMultiMesh source)
        {
            Mesh found;
            if (_billboards.TryGetValue(source.Name, out found))
            {
                return found;
            }

            var mesh = BuildBillboardMesh(source);
            _billboards.Add(source.Name, mesh);
            return mesh;
        }

        /// <summary>
        /// Mirrors one baked mesh into Unity space, the way
        /// <see cref="BattleTerrain.BuildMesh"/> mirrors the landscape.
        /// </summary>
        public static Mesh BuildMesh(MapMesh source)
        {
            var count = source.VertexCount;
            var vertices = new Vector3[count];
            var normals = new Vector3[count];
            var uv = new Vector2[count];
            for (var i = 0; i < count; i++)
            {
                var p = i * 3;
                var position = GodotSpace.Point(
                    source.Positions[p], source.Positions[p + 1], source.Positions[p + 2]);
                vertices[i] = new Vector3(position.X, position.Y, position.Z);

                var normal = GodotSpace.Point(
                    source.Normals[p], source.Normals[p + 1], source.Normals[p + 2]);
                normals[i] = new Vector3(normal.X, normal.Y, normal.Z);

                var t = i * 2;
                uv[i] = new Vector2(source.Uv[t], source.Uv[t + 1]);
            }

            var mesh = new Mesh { name = source.Name };
            if (count > 65535)
            {
                mesh.indexFormat = IndexFormat.UInt32;
            }

            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uv;
            mesh.triangles = Triangles(source.Indices);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// Expand a source QuadMesh MultiMesh into one camera-facing mesh. UV2
        /// stores each source instance centre; the vegetation shader rotates
        /// the four offsets around that centre, while normal Unity culling still
        /// sees bounds spanning the complete cluster.
        /// </summary>
        public static Mesh BuildBillboardMesh(MapMultiMesh source)
        {
            var vertices = new Vector3[source.Count * 4];
            var normals = new Vector3[vertices.Length];
            var uv = new Vector2[vertices.Length];
            var centres = new List<Vector3>(vertices.Length);
            var triangles = new int[source.Count * 6];
            var minimum = new Vector3(float.PositiveInfinity, float.PositiveInfinity,
                float.PositiveInfinity);
            var maximum = new Vector3(float.NegativeInfinity, float.NegativeInfinity,
                float.NegativeInfinity);

            for (var index = 0; index < source.Count; index++)
            {
                var transform = source.Transforms[index];
                var point = GodotSpace.Point(transform.Origin);
                var centre = new Vector3(point.X, point.Y, point.Z);
                Vec3 right, up, forward;
                GodotSpace.Basis(transform.Basis, out right, out up, out forward);
                var halfWidth = 0.5f * source.QuadSize[0] *
                    new Vector3(right.X, right.Y, right.Z).magnitude;
                var halfHeight = 0.5f * source.QuadSize[1] *
                    new Vector3(up.X, up.Y, up.Z).magnitude;
                var radius = Mathf.Sqrt(halfWidth * halfWidth + halfHeight * halfHeight);
                minimum = Vector3.Min(minimum, centre - Vector3.one * radius);
                maximum = Vector3.Max(maximum, centre + Vector3.one * radius);

                var vertex = index * 4;
                vertices[vertex] = centre + new Vector3(-halfWidth, -halfHeight, 0f);
                vertices[vertex + 1] = centre + new Vector3(-halfWidth, halfHeight, 0f);
                vertices[vertex + 2] = centre + new Vector3(halfWidth, -halfHeight, 0f);
                vertices[vertex + 3] = centre + new Vector3(halfWidth, halfHeight, 0f);
                normals[vertex] = normals[vertex + 1] =
                    normals[vertex + 2] = normals[vertex + 3] = Vector3.back;
                uv[vertex] = new Vector2(0f, 1f);
                uv[vertex + 1] = new Vector2(0f, 0f);
                uv[vertex + 2] = new Vector2(1f, 1f);
                uv[vertex + 3] = new Vector2(1f, 0f);
                centres.Add(centre);
                centres.Add(centre);
                centres.Add(centre);
                centres.Add(centre);

                var triangle = index * 6;
                triangles[triangle] = vertex;
                triangles[triangle + 1] = vertex + 1;
                triangles[triangle + 2] = vertex + 2;
                triangles[triangle + 3] = vertex + 2;
                triangles[triangle + 4] = vertex + 1;
                triangles[triangle + 5] = vertex + 3;
            }

            var mesh = new Mesh { name = source.Name };
            if (vertices.Length > 65535)
            {
                mesh.indexFormat = IndexFormat.UInt32;
            }

            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uv;
            mesh.SetUVs(1, centres);
            mesh.triangles = triangles;
            mesh.bounds = new Bounds((minimum + maximum) * 0.5f, maximum - minimum);
            return mesh;
        }

        private Mesh CollisionMesh(MapShape source)
        {
            Mesh found;
            if (_colliders.TryGetValue(source.Name, out found))
            {
                return found;
            }

            var mesh = BuildCollisionMesh(source);
            _colliders.Add(source.Name, mesh);
            return mesh;
        }

        /// <summary>Mirrors one baked collision shape into Unity space.</summary>
        public static Mesh BuildCollisionMesh(MapShape source)
        {
            var count = source.VertexCount;
            var vertices = new Vector3[count];
            var indices = new int[count];
            for (var i = 0; i < count; i++)
            {
                var p = i * 3;
                var point = GodotSpace.Point(
                    source.Points[p], source.Points[p + 1], source.Points[p + 2]);
                vertices[i] = new Vector3(point.X, point.Y, point.Z);
                indices[i] = i;
            }

            var mesh = new Mesh { name = source.Name };
            if (count > 65535)
            {
                mesh.indexFormat = IndexFormat.UInt32;
            }

            mesh.vertices = vertices;
            mesh.triangles = Triangles(indices);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Winding survives the mirror, see <see cref="GodotSpace.Triangle"/>.</summary>
        private static int[] Triangles(int[] indices)
        {
            var triangles = new int[indices.Length];
            for (var i = 0; i < indices.Length; i += 3)
            {
                int x, y, z;
                GodotSpace.Triangle(
                    indices[i], indices[i + 1], indices[i + 2], out x, out y, out z);
                triangles[i] = x;
                triangles[i + 1] = y;
                triangles[i + 2] = z;
            }

            return triangles;
        }

        /// <summary>The exporter records the map-specific bake path.</summary>
        private static string BakedPath(MapObjectInstance instance)
        {
            return instance.BakedPath;
        }

        private static Color SourceAlbedo(MapObjectMaterial source)
        {
            return BattleWorld.SourceColour(new Color(
                Mathf.Pow(source.Albedo[0], source.Contrast),
                Mathf.Pow(source.Albedo[1], source.Contrast),
                Mathf.Pow(source.Albedo[2], source.Contrast),
                source.Albedo[3]));
        }

        private Material BakedMaterialFor(string name)
        {
            Material found;
            if (_bakedMaterials.TryGetValue(name, out found))
            {
                return found;
            }

            var source = _table.GetMaterial(name);
            var texture = Resources.Load<Texture2D>(source.Texture);
            if (texture == null)
            {
                Debug.LogError("[Blitz] missing object texture: " + source.Texture);
            }

            var material = _lighting.CreateMaterial(
                name + "Baked", texture, SourceAlbedo(source));
            if (material == null)
            {
                return MaterialFor(name);
            }

            if (source.Uv1Scale != null)
            {
                material.SetTextureScale(
                    "_BaseMap", new Vector2(source.Uv1Scale[0], source.Uv1Scale[1]));
                material.SetTextureOffset(
                    "_BaseMap", new Vector2(source.Uv1Offset[0], source.Uv1Offset[1]));
            }

            _bakedMaterials.Add(name, material);
            return material;
        }

        private Material MaterialFor(string name)
        {
            Material found;
            if (_materials.TryGetValue(name, out found))
            {
                return found;
            }

            var source = _table.GetMaterial(name);
            var shader = source.IsWater
                ? Shader.Find(WaterShaderName)
                : source.IsVegetation
                    ? Shader.Find(VegetationShaderName)
                    : Shader.Find(LitShaderName);
            if (shader == null)
            {
                shader = Shader.Find(source.IsVegetation
                    ? "Universal Render Pipeline/Unlit"
                    : "Standard");
            }

            var material = new Material(shader) { name = name };
            var texture = Resources.Load<Texture2D>(source.Texture);
            if (texture == null)
            {
                Debug.LogError("[Blitz] missing object texture: " + source.Texture);
            }

            // The ported object shader raises the tint times the texture to
            // the contrast exponent itself, exactly as the source does, so it
            // takes the raw tint; a stock URP fallback has no such stage, so it
            // keeps the pre-raised colour instead.
            var ported = material.HasProperty("_Contrast");
            var albedo = ported
                ? BattleWorld.SourceColour(new Color(
                    source.Albedo[0], source.Albedo[1], source.Albedo[2], source.Albedo[3]))
                : SourceAlbedo(source);
            if (ported)
            {
                material.SetFloat("_Contrast", source.Contrast);
                if (material.HasProperty("_Emissive"))
                {
                    material.SetFloat("_Emissive", source.Emissive ? 1f : 0f);
                    if (source.Emissive && material.HasProperty("_EmissionMap"))
                    {
                        material.SetTexture(
                            "_EmissionMap",
                            Resources.Load<Texture2D>(source.EmissiveTexture));
                    }
                }
            }

            if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", texture);
            if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", texture);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", albedo);
            if (material.HasProperty("_Color")) material.SetColor("_Color", albedo);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0f);
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0f);
            if (material.HasProperty("_SpecularHighlights"))
            {
                material.SetFloat("_SpecularHighlights", 0f);
            }
            if (source.IsWater)
            {
                material.SetVector("_Shift1", new Vector4(
                    source.Shift1[0], source.Shift1[1], 0f, 0f));
                material.SetVector("_Shift2", new Vector4(
                    source.Shift2[0], source.Shift2[1], 0f, 0f));
            }

            if (source.Uv1Scale != null)
            {
                material.SetTextureScale(
                    "_BaseMap", new Vector2(source.Uv1Scale[0], source.Uv1Scale[1]));
                material.SetTextureOffset(
                    "_BaseMap", new Vector2(source.Uv1Offset[0], source.Uv1Offset[1]));
            }

            // Every source family repeats shared mesh/material pairs. Runtime
            // GPU instancing keeps 841 renderers from becoming 841 draw calls.
            material.enableInstancing = true;

            if (source.IsVegetation)
            {
                var origin = GodotSpace.Point(source.OriginOffset);
                var sway = GodotSpace.Point(source.SwayDirection);
                material.SetFloat("_Cutoff", source.AlphaClip);
                material.SetFloat("_Billboard", source.Billboard ? 1f : 0f);
                material.SetVector("_OriginOffset",
                    new Vector4(origin.X, origin.Y, origin.Z, 0f));
                material.SetFloat("_SwayAmount", source.SwayAmount);
                material.SetFloat("_SwayScale", source.SwayScale);
                material.SetFloat("_SwayDuration", Mathf.Max(source.SwayDuration, 0.001f));
                material.SetVector("_SwayDirection",
                    new Vector4(sway.X, sway.Y, sway.Z, 0f));
                material.doubleSidedGI = true;
                material.renderQueue = (int)RenderQueue.AlphaTest;
            }

            _materials.Add(name, material);
            return material;
        }
    }
}
