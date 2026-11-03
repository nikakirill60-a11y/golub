using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Maps;
using BlitzLegacy.Runtime.Assets;
using UnityEngine;
using UnityEngine.Rendering;

namespace BlitzLegacy.Runtime.Lighting
{
    /// <summary>
    /// One scene's baked lighting: the atlas strip, the unwrapped meshes and
    /// the rect every instance owns, as Tools/port_godot_lighting.py exported
    /// them from the source LightmapGI.
    ///
    /// The hangar reproduces its source bake, and the battle keeps the same
    /// loader only for explicit reference renders. A caller must choose this or
    /// realtime lighting: adding both makes the frame draw every shadow twice.
    /// </summary>
    public sealed class BakedLighting
    {
        /// <summary>
        /// Built at runtime, so a player build only keeps it when
        /// ProjectBootstrap retains it by name.
        /// </summary>
        public const string ShaderName = "BlitzLegacy/Map/Lightmapped Object";

        private static readonly int LightmapProperty = Shader.PropertyToID("_Lightmap");
        private static readonly int ExposureProperty = Shader.PropertyToID("_Exposure");

        private static readonly int EmissionProperty = Shader.PropertyToID("_EmissionMap");
        private static readonly int EmissiveProperty = Shader.PropertyToID("_Emissive");
        private static readonly int ContrastProperty = Shader.PropertyToID("_Contrast");

        private static readonly int PlacementProperty =
            Shader.PropertyToID("_LightmapScaleOffset");

        private readonly Dictionary<string, Mesh> _meshes =
            new Dictionary<string, Mesh>(StringComparer.Ordinal);

        private readonly List<Material> _materials = new List<Material>();

        private readonly Dictionary<string, Material> _byMaterialKey =
            new Dictionary<string, Material>(StringComparer.Ordinal);

        private readonly MaterialPropertyBlock _placement = new MaterialPropertyBlock();

        private LightingDefinition _definition;
        private LightmapMeshLibrary _library;
        private Texture2D _atlas;

        public LightingDefinition Definition
        {
            get { return _definition; }
        }

        public Texture2D Atlas
        {
            get { return _atlas; }
        }

        /// <summary>Instances bound to the atlas so far, for the build log.</summary>
        public int BoundCount { get; private set; }

        public int MeshCount
        {
            get { return _meshes.Count; }
        }

        public int TriangleCount { get; private set; }

        /// <summary>Materials built from the manifest so far, for the build log.</summary>
        public int MaterialCount
        {
            get { return _byMaterialKey.Count; }
        }

        /// <summary>
        /// Reads only the manifest. Realtime-lit scenes need its environment and
        /// sun but must not load the atlas or unwrapped meshes they never draw.
        /// </summary>
        public static LightingDefinition LoadDefinition(string id)
        {
            var json = BlitzResources.LoadText("lighting_" + id);
            if (string.IsNullOrEmpty(json))
            {
                Debug.LogError("[Blitz] missing lighting manifest: lighting_" + id);
                return null;
            }

            return LightingDefinition.Parse(json);
        }

        /// <summary>
        /// Reads a scene's complete bake, or returns null when any part of it is
        /// missing: a half-loaded bake would light part of the scene and leave
        /// the rest black, which is harder to diagnose than no bake at all.
        /// </summary>
        public static BakedLighting Load(string id)
        {
            var definition = LoadDefinition(id);
            if (definition == null)
            {
                return null;
            }

            var payload = Resources.Load<TextAsset>(definition.Meshes.Resource);
            if (payload == null)
            {
                Debug.LogError("[Blitz] missing lightmap meshes: " + definition.Meshes.Resource);
                return null;
            }

            var atlas = Resources.Load<Texture2D>(definition.Lightmap.Atlas);
            if (atlas == null)
            {
                Debug.LogError("[Blitz] missing lightmap atlas: " + definition.Lightmap.Atlas);
                return null;
            }

            return new BakedLighting
            {
                _definition = definition,
                _library = LightmapMeshLibrary.Parse(payload.bytes),
                _atlas = atlas,
            };
        }

        public bool Has(string path)
        {
            return _definition != null && _definition.HasEntry(path);
        }

        public LightingEntry Entry(string path)
        {
            return _definition.Entry(path);
        }

        /// <summary>
        /// Mirrors one unwrapped mesh into Unity space, the way
        /// <see cref="BlitzLegacy.Runtime.Battle.BattleObjects.BuildMesh"/>
        /// mirrors the object payload. The primary V is converted back from
        /// Godot's top-down texture space, while the lightmap unwrap stays on
        /// the second channel in source space: its atlas transform already
        /// mirrors V when the shader samples it.
        /// </summary>
        public Mesh MeshFor(string name)
        {
            Mesh found;
            if (_meshes.TryGetValue(name, out found))
            {
                return found;
            }

            var mesh = BuildMesh(_library.Get(name));
            _meshes.Add(name, mesh);
            TriangleCount += mesh.triangles.Length / 3;
            return mesh;
        }

        public static Mesh BuildMesh(LightmapMesh source)
        {
            var count = source.VertexCount;
            var vertices = new Vector3[count];
            var normals = new Vector3[count];
            var uv = new Vector2[count];
            var uv2 = new Vector2[count];
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
                // Godot's glTF importer has already inverted primary V. The
                // source PNG bytes are copied into Unity unchanged, so undo
                // that importer conversion or the hangar floor samples a
                // black region of Hangar2 across whole UV islands.
                uv[i] = new Vector2(source.Uv[t], 1f - source.Uv[t + 1]);
                // Lightmap V is converted by AtlasScaleOffset instead. Flipping
                // it here as well would mirror every baked shadow twice.
                uv2[i] = new Vector2(source.Uv2[t], source.Uv2[t + 1]);
            }

            var mesh = new Mesh { name = source.Name };
            if (count > 65535)
            {
                mesh.indexFormat = IndexFormat.UInt32;
            }

            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uv;
            mesh.uv2 = uv2;
            mesh.triangles = Triangles(source.Indices);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// A material that samples the atlas. The albedo side stays the
        /// caller's business, because the source keeps its own material per
        /// prop family and only the lighting comes from here.
        /// </summary>
        public Material CreateMaterial(string name, Texture2D albedo, Color tint)
        {
            var shader = Shader.Find(ShaderName);
            if (shader == null)
            {
                Debug.LogError("[Blitz] missing shader: " + ShaderName);
                return null;
            }

            var material = new Material(shader) { name = name };
            if (albedo != null)
            {
                material.SetTexture("_BaseMap", albedo);
            }

            material.SetColor("_BaseColor", tint);
            material.SetTexture(LightmapProperty, _atlas);
            material.SetFloat(
                ExposureProperty, _definition.Environment.TonemapExposure);
            // Whole families of props share one mesh and one material.
            material.enableInstancing = true;
            _materials.Add(material);
            return material;
        }

        /// <summary>
        /// The material a baked instance draws with, exactly as the manifest
        /// exported it from the source. One Unity material per source material,
        /// so a whole prop family still batches, and the port stops rather than
        /// guessing when the manifest names one it did not export.
        /// </summary>
        public Material MaterialFor(LightingEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.Material))
            {
                throw new ArgumentException(
                    "baked instance carries no ported material");
            }

            Material found;
            if (_byMaterialKey.TryGetValue(entry.Material, out found))
            {
                return found;
            }

            var source = _definition.Material(entry.Material);
            // Godot stores these as linear values and the shader multiplies the
            // texture by them, so they travel raw rather than through the sRGB
            // conversion a light colour needs.
            var tint = new Color(
                source.Albedo[0], source.Albedo[1], source.Albedo[2], source.Albedo[3]);
            var material = CreateMaterial(source.Key, LoadTexture(source.Texture), tint);
            if (material == null)
            {
                return null;
            }

            if (source.Emissive)
            {
                material.SetTexture(EmissionProperty, LoadTexture(source.EmissiveTexture));
                material.SetFloat(EmissiveProperty, 1f);
            }

            material.SetFloat(ContrastProperty, source.Contrast);
            material.mainTextureScale =
                new Vector2(source.Uv1Scale[0], source.Uv1Scale[1]);
            material.mainTextureOffset =
                new Vector2(source.Uv1Offset[0], source.Uv1Offset[1]);
            _byMaterialKey.Add(entry.Material, material);
            return material;
        }

        /// <summary>
        /// Points one renderer at its rect of the strip and drops its realtime
        /// shadow: the source sun bakes its shadows into the atlas and casts
        /// none at runtime, so baked geometry that still cast one would darken
        /// the scene twice.
        ///
        /// The source nodes do keep cast_shadow on, and the port deliberately
        /// does not carry that flag over: it only feeds lights that shade in
        /// realtime, and every light covering this geometry is marked baked.
        /// Honouring it would re-cast the shadows the atlas already holds.
        /// </summary>
        public void Bind(Renderer renderer, LightingEntry entry)
        {
            if (renderer == null || entry == null)
            {
                throw new ArgumentNullException("baked lighting bind input");
            }

            // gi_mode 1 is Godot's STATIC, meaning the bake covers this
            // instance. Anything else would mean the source lit it some other
            // way, and sampling the atlas for it would be wrong rather than
            // merely dim, so stop instead of drawing a lie.
            if (entry.GiMode != 1)
            {
                throw new ArgumentException(
                    "baked instance '" + entry.Path +
                    "' is not static, gi_mode=" + entry.GiMode);
            }

            var placement = entry.AtlasScaleOffset;
            _placement.SetVector(
                PlacementProperty,
                new Vector4(placement[0], placement[1], placement[2], placement[3]));
            renderer.SetPropertyBlock(_placement);
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            BoundCount++;
        }

        /// <summary>
        /// Meshes and materials built at runtime belong to no scene, so they
        /// outlive the world unless they are released with it.
        /// </summary>
        public void Release()
        {
            foreach (var mesh in _meshes.Values)
            {
                Discard(mesh);
            }

            for (var i = 0; i < _materials.Count; i++)
            {
                Discard(_materials[i]);
            }

            _meshes.Clear();
            _materials.Clear();
            _byMaterialKey.Clear();
            BoundCount = 0;
            TriangleCount = 0;
        }

        private static Texture2D LoadTexture(string path)
        {
            var texture = Resources.Load<Texture2D>(path);
            if (texture == null)
            {
                Debug.LogError("[Blitz] missing ported texture: " + path);
            }

            return texture;
        }

        private static void Discard(UnityEngine.Object target)
        {
            if (target == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(target);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

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
    }
}
