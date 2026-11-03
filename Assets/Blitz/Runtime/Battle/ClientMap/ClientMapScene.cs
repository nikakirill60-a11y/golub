using System;
using System.Collections.Generic;
using System.Text;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>One DAVA material of a client scene, as client_map_scene.py writes it.</summary>
    [Serializable]
    public sealed class ClientMapMaterial
    {
        public string name;
        public string effect;
        public string kind;
        public int queue;
        public bool cull;
        public bool zWrite;
        public bool blend;
        public int srcBlend;
        public int dstBlend;
        public bool alphaTest;
        public float cutoff;
        public bool fog;
        public string albedo;
        public string lightmap;
        public float[] lightmapST;
        public string alphamask;
        public float[] flatColor;
        public float[] texShift;
        public float[] texShiftSpeed;
        public bool speedTree;
        public float[] leafColorMul;
        public float[] leafOcclusion;
        public bool wind;
        public string flowmap;
        public float[] flow;
        public string normalmap;
        public string cubemap;
        public float[] normalScale;
        public float[] normal0Shift;
        public float[] normal1Shift;
        public float[] fresnel;
        public float[] reflectionTint;

        // 11.20 materials: VERTEX_COLOR multiplies the output, a material
        // `fogColor` replaces the global one, and the lit effects carry
        // their Blinn-Phong terms.
        public bool vertexColor;
        public float[] fogColor;
        public float glossiness;
        public float specularity;

        // Godot scenes (Tools/port_godot_map_scene.py), linear colours.
        public float[] color;
        public float contrast = 1f;
        public string emission;
        public float[] lightmapSize;
        public float[] uvTransform;
        public bool unshaded;
        public bool billboard;
        public float[] originOffset;
        public float[] sway;
        public float[] swayDirection;
        public float[] waterShift;
        public string source;

        /// <summary>A Godot material: drawn by the BlitzLegacy/GodotMap shaders.</summary>
        public bool Godot
        {
            get { return kind != null && kind.StartsWith(ClientMapScene.GodotKindPrefix, StringComparison.Ordinal); }
        }

        /// <summary>Geometry a tank collides with: not sky, water, shadow hull,
        /// translucent surf or a camera-facing leaf card.</summary>
        public bool Solid
        {
            get
            {
                return (kind == ClientMapScene.SurfaceKind || kind == ClientMapScene.LitKind) &&
                    !blend && !speedTree;
            }
        }
    }

    /// <summary>One named node: an entity's batches as submeshes of one mesh.</summary>
    [Serializable]
    public sealed class ClientMapNodeInfo
    {
        public string name;
        public int vertexCount;
        public int attributes;
        public int[] materials;
        public float[] min;
        public float[] max;

        // Godot scenes: the gameplay object the node belongs to, what it is
        // to it (intact, wreck, static, trunk, leaves, lod, water, sky,
        // decoration), whether Godot starts it hidden, and the
        // map_object_vegetation.gd node whose distance picks sprites or lods.
        // A DAVA scene sets `hidden` on a render object authored without
        // its VISIBLE flag: collision only, never drawn.
        public string @object;
        public string role;
        public bool hidden;
        public string lodGroup;
        public float[] lodOrigin;

        // 11.20 scenes collide only with what a CollisionTypeComponent tags:
        // the node draws but a tank drives through it.
        public bool noCollision;
    }

    [Serializable]
    public sealed class ClientMapLayout
    {
        public string format;
        public ClientMapMaterial[] materials;
        public ClientMapNodeInfo[] nodes;

        // 11.20 scenes: the clips of the skinned props, the props themselves
        // and the generated vegetation (empty payload: none).
        public string animations;
        public ClientMapSkeletonInfo[] skeletons;
        public ClientMapVegetationInfo vegetation;
    }

    /// <summary>The collision geometry of a client node, when it differs from
    /// what it draws (sky, water, shadow hulls and leaf cards are left out).</summary>
    public sealed class ClientMapNode : MonoBehaviour
    {
        public Mesh CollisionMesh;
        public bool HasSolid;
        public bool CastsShadowVolume;
        public ClientMapNodeInfo Info;
        /// <summary>True when the node samples the Godot lightmap.</summary>
        public bool Lightmapped;
    }

    /// <summary>
    /// Builds a 4.8 client map scene from the binary payload of
    /// Tools/client_map_scene.py: every entity as one GameObject named like
    /// the OBJ groups of the older path, each DAVA batch a submesh drawn with
    /// the client's own effect. Owns every mesh and material it creates.
    /// </summary>
    public sealed class ClientMapScene : MonoBehaviour
    {
        public const string Magic = "BLTZMAP1";
        public const string SurfaceShader = "BlitzLegacy/ClientMap/Surface";
        public const string SkyShader = "BlitzLegacy/ClientMap/Sky";
        public const string WaterShader = "BlitzLegacy/ClientMap/Water";
        public const string ShadowVolumeShader = "BlitzLegacy/ClientMap/ShadowVolume";
        public const string LandscapeShader = "BlitzLegacy/ClientMap/Landscape";
        public const string LitShader = "BlitzLegacy/ClientMap/Lit";
        public const string VegetationShader = "BlitzLegacy/ClientMap/Vegetation";
        public const string SurfaceKind = "surface";
        public const string LitKind = "lit";
        public const string SkyKind = "sky";
        public const string WaterKind = "water";
        public const string ShadowKind = "shadow";

        public const string GodotKindPrefix = "godot_";
        public const string GodotObjectKind = "godot_object";
        public const string GodotWaterKind = "godot_water";
        public const string GodotVegetationKind = "godot_vegetation";
        public const string GodotSpriteKind = "godot_sprite";
        public const string GodotSkyKind = "godot_sky";
        public const string GodotObjectShader = "BlitzLegacy/GodotMap/Object";
        public const string GodotWaterShader = "BlitzLegacy/GodotMap/Water";
        public const string GodotVegetationShader = "BlitzLegacy/GodotMap/Vegetation";
        public const string GodotSpriteShader = "BlitzLegacy/GodotMap/Sprite";
        public const string GodotSkyShader = "BlitzLegacy/GodotMap/Sky";
        public const string GodotLandscapeShader = "BlitzLegacy/GodotMap/Landscape";

        /// <summary>Every shader a Godot scene builds at runtime.</summary>
        public static readonly string[] GodotShaders =
        {
            GodotObjectShader, GodotWaterShader, GodotVegetationShader,
            GodotSpriteShader, GodotSkyShader, GodotLandscapeShader,
        };

        private const int HasNormal = 1;
        private const int HasUv1 = 2;
        private const int HasColor = 4;
        private const int HasTangent = 8;
        private const int HasBillboard = 16;
        private const int HasSkin = 32;

        private readonly List<UnityEngine.Object> _owned = new List<UnityEngine.Object>();

        public ClientMapLayout Layout { get; private set; }
        public int NodeCount { get; private set; }

        /// <summary>Nodes that sample the Godot lightmap.</summary>
        public int LightmappedCount { get; private set; }

        /// <summary>The built nodes, in layout order.</summary>
        public List<ClientMapNode> Nodes { get; } = new List<ClientMapNode>();
        public int TriangleCount { get; private set; }

        /// <summary>The skinned props, each playing its client clip.</summary>
        public List<ClientMapSkeleton> Skeletons { get; } = new List<ClientMapSkeleton>();

        /// <summary>Whether a client scene payload exists for the resource.</summary>
        public static bool Exists(string resource)
        {
            return !string.IsNullOrEmpty(resource) &&
                Resources.Load<TextAsset>(resource + "_layout") != null;
        }

        public static ClientMapScene Build(Transform parent, string resource)
        {
            var layoutAsset = Resources.Load<TextAsset>(resource + "_layout");
            var meshAsset = Resources.Load<TextAsset>(resource + "_meshes");
            if (layoutAsset == null || meshAsset == null)
            {
                throw new InvalidOperationException("missing client map scene: " + resource);
            }

            var root = new GameObject("Objects");
            root.transform.SetParent(parent, false);
            var scene = root.AddComponent<ClientMapScene>();
            try
            {
                scene.Layout = JsonUtility.FromJson<ClientMapLayout>(layoutAsset.text);
                if (scene.Layout == null || scene.Layout.format != Magic)
                {
                    throw new InvalidOperationException("invalid client map layout: " + resource);
                }
                scene.BuildNodes(meshAsset);
                scene.BuildSkeletons();
            }
            catch
            {
                Release(root);
                throw;
            }

            Resources.UnloadAsset(layoutAsset);
            Resources.UnloadAsset(meshAsset);
            return scene;
        }

        private static void Release(UnityEngine.Object target)
        {
            if (Application.isPlaying) Destroy(target); else DestroyImmediate(target);
        }

        private void OnDestroy()
        {
            for (var i = 0; i < _owned.Count; i++)
            {
                if (_owned[i] != null) Release(_owned[i]);
            }
            _owned.Clear();
        }

        private void BuildNodes(TextAsset meshAsset)
        {
            var materials = new Material[Layout.materials.Length];
            var textures = new Dictionary<string, Texture>();
            for (var i = 0; i < materials.Length; i++)
            {
                materials[i] = CreateMaterial(Layout.materials[i], textures);
                _owned.Add(materials[i]);
            }

            var data = meshAsset.GetData<byte>();
            var reader = new PayloadReader(data);
            if (reader.Ascii(8) != Magic)
            {
                throw new InvalidOperationException("invalid client map payload magic");
            }
            if (reader.Int() != Layout.nodes.Length)
            {
                throw new InvalidOperationException("client map node count mismatch");
            }

            for (var n = 0; n < Layout.nodes.Length; n++)
            {
                BuildNode(Layout.nodes[n], ref reader, materials);
            }

            if (reader.Offset != data.Length)
            {
                throw new InvalidOperationException("trailing client map payload bytes");
            }
        }

        /// <summary>
        /// Hangs the per-joint nodes of every skinned prop under its joints.
        /// The porter baked each joint's inverse bind matrix into its node,
        /// so a node sits at identity under its joint.
        /// </summary>
        private void BuildSkeletons()
        {
            if (Layout.skeletons == null || Layout.skeletons.Length == 0)
            {
                return;
            }

            var clips = ClientMapSkeleton.LoadClips(Layout.animations);
            var byName = new Dictionary<string, ClientMapNode>(StringComparer.Ordinal);
            for (var i = 0; i < Nodes.Count; i++)
            {
                byName[Nodes[i].Info.name] = Nodes[i];
            }

            for (var i = 0; i < Layout.skeletons.Length; i++)
            {
                Skeletons.Add(ClientMapSkeleton.Build(transform, Layout.skeletons[i], clips, byName));
            }
        }

        private void BuildNode(ClientMapNodeInfo info, ref PayloadReader reader, Material[] materials)
        {
            var count = reader.Int();
            var flags = reader.Int();
            var submeshes = reader.Int();
            if (count != info.vertexCount || flags != info.attributes ||
                submeshes != info.materials.Length)
            {
                throw new InvalidOperationException("client map node mismatch: " + info.name);
            }

            var mesh = new Mesh
            {
                name = info.name,
                indexFormat = count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16,
            };
            _owned.Add(mesh);
            var positions = reader.Array<Vector3>(count);
            mesh.SetVertices(positions);
            Vector3[] normals = null;
            if ((flags & HasNormal) != 0)
            {
                normals = reader.Array<Vector3>(count);
                mesh.SetNormals(normals);
            }
            mesh.SetUVs(0, reader.Array<Vector2>(count));
            if ((flags & HasUv1) != 0) mesh.SetUVs(1, reader.Array<Vector2>(count));
            if ((flags & HasColor) != 0) mesh.SetColors(reader.Array<Color32>(count));
            if ((flags & HasTangent) != 0) mesh.SetTangents(reader.Array<Vector4>(count));
            var billboardReach = 0f;
            if ((flags & HasBillboard) != 0)
            {
                var billboard = reader.Array<Vector2>(count);
                mesh.SetUVs(2, billboard);
                // A Godot billboard keeps its per-axis scale in the normal.
                var godotScale = normals != null && GodotNode(info, materials);
                for (var i = 0; i < billboard.Length; i++)
                {
                    var scale = godotScale
                        ? Mathf.Max(Mathf.Abs(normals[i].x), Mathf.Abs(normals[i].y))
                        : 1f;
                    billboardReach = Mathf.Max(billboardReach, billboard[i].magnitude * scale);
                }
            }

            if ((flags & HasSkin) != 0)
            {
                // Soft-skinned props (World of Ducks' sea kale and buoys):
                // four joints and weights a vertex, bound by the skeleton.
                var bones = reader.Array<int>(count * 4);
                var weights = reader.Array<float>(count * 4);
                var boneWeights = new BoneWeight[count];
                for (var i = 0; i < count; i++)
                {
                    boneWeights[i] = new BoneWeight
                    {
                        boneIndex0 = bones[i * 4], weight0 = weights[i * 4],
                        boneIndex1 = bones[i * 4 + 1], weight1 = weights[i * 4 + 1],
                        boneIndex2 = bones[i * 4 + 2], weight2 = weights[i * 4 + 2],
                        boneIndex3 = bones[i * 4 + 3], weight3 = weights[i * 4 + 3],
                    };
                }
                mesh.boneWeights = boneWeights;
            }

            mesh.subMeshCount = submeshes;
            var shared = new Material[submeshes];
            var solid = new List<int>();
            var allSolid = true;
            var sky = false;
            var castsVolume = false;
            var godot = false;
            var lightmapped = false;
            for (var s = 0; s < submeshes; s++)
            {
                var materialIndex = reader.Int();
                var indexCount = reader.Int();
                if (materialIndex != info.materials[s] || materialIndex < 0 ||
                    materialIndex >= materials.Length || indexCount % 3 != 0)
                {
                    throw new InvalidOperationException("client map submesh mismatch: " + info.name);
                }

                var indices = reader.Array<int>(indexCount);
                mesh.SetIndices(indices, MeshTopology.Triangles, s, false);
                shared[s] = materials[materialIndex];
                var source = Layout.materials[materialIndex];
                sky |= source.kind == SkyKind;
                castsVolume |= source.kind == ShadowKind;
                godot |= source.Godot;
                lightmapped |= source.Godot && !string.IsNullOrEmpty(source.lightmap);
                if (source.Solid)
                {
                    for (var i = 0; i < indices.Length; i++) solid.Add(indices[i]);
                }
                else
                {
                    allSolid = false;
                }
                TriangleCount += indexCount / 3;
            }

            mesh.RecalculateBounds();
            if (sky || godot && info.role == SkyRole)
            {
                // Drawn around the camera at the far plane: never cull it.
                mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100000f);
            }
            else if (billboardReach > 0f)
            {
                var bounds = mesh.bounds;
                bounds.Expand(billboardReach * 2f);
                mesh.bounds = bounds;
            }

            var go = new GameObject(info.name);
            go.transform.SetParent(transform, false);
            Renderer renderer;
            if ((flags & HasSkin) != 0)
            {
                // ClientMapSkeleton.Build gives it bones and bind poses.
                var skinned = go.AddComponent<SkinnedMeshRenderer>();
                skinned.sharedMesh = mesh;
                skinned.updateWhenOffscreen = false;
                skinned.quality = SkinQuality.Bone4;
                renderer = skinned;
            }
            else
            {
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                renderer = go.AddComponent<MeshRenderer>();
            }
            renderer.sharedMaterials = shared;
            // Only the shadow hulls have a ShadowCaster pass: the client's
            // artwork never casts, its shadows are baked or volumes.
            renderer.shadowCastingMode = castsVolume ? ShadowCastingMode.On : ShadowCastingMode.Off;
            renderer.receiveShadows = true;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

            var node = go.AddComponent<ClientMapNode>();
            node.Info = info;
            Nodes.Add(node);
            if (godot)
            {
                // Godot draws no realtime shadow on this map (the sun is
                // baked and shadow_enabled is off), and the gameplay table
                // brings its own colliders, so the node only draws.
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.enabled = !info.hidden;
                node.Lightmapped = lightmapped;
                if (lightmapped) LightmappedCount++;
                NodeCount++;
                return;
            }
            if (info.noCollision)
            {
                solid.Clear();
                allSolid = false;
            }
            node.HasSolid = solid.Count > 0;
            node.CastsShadowVolume = castsVolume;
            // A render object the client authors without its VISIBLE flag
            // (Lost Temple's invisible walls) is never drawn but still
            // collides, so only the renderer goes.
            if (info.hidden)
            {
                renderer.enabled = false;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
            }
            if (allSolid)
            {
                node.CollisionMesh = mesh;
            }
            else if (solid.Count > 0)
            {
                var collision = new Mesh
                {
                    name = info.name + "_collision",
                    indexFormat = mesh.indexFormat,
                };
                _owned.Add(collision);
                collision.SetVertices(positions);
                collision.SetIndices(solid, MeshTopology.Triangles, 0, true);
                node.CollisionMesh = collision;
            }
            NodeCount++;
        }

        public const string SkyRole = "sky";

        private bool GodotNode(ClientMapNodeInfo info, Material[] materials)
        {
            for (var i = 0; i < info.materials.Length; i++)
            {
                if (Layout.materials[info.materials[i]].Godot) return true;
            }
            return false;
        }

        private Material CreateMaterial(ClientMapMaterial source, Dictionary<string, Texture> textures)
        {
            if (source.Godot)
            {
                return CreateGodotMaterial(source, textures);
            }

            var shaderName = source.kind == SkyKind ? SkyShader
                : source.kind == WaterKind ? WaterShader
                : source.kind == ShadowKind ? ShadowVolumeShader
                : source.kind == LitKind ? LitShader
                : SurfaceShader;
            var shader = Shader.Find(shaderName);
            if (shader == null)
            {
                throw new InvalidOperationException("missing client map shader: " + shaderName);
            }

            var material = new Material(shader) { name = source.name };
            material.renderQueue = source.queue;
            if (source.kind == ShadowKind)
            {
                return material;
            }

            material.SetFloat("_Cull", source.cull ? (float)CullMode.Back : (float)CullMode.Off);
            if (source.kind == SkyKind)
            {
                material.SetTexture("_Albedo", Texture(source.albedo, textures));
                material.SetTexture("_Flowmap", Texture(source.flowmap, textures));
                material.SetVector("_Flow", Vector(source.flow, 0f));
                return material;
            }

            material.SetFloat("_SrcBlend", source.blend ? source.srcBlend : (float)BlendMode.One);
            material.SetFloat("_DstBlend", source.blend ? source.dstBlend : (float)BlendMode.Zero);
            material.SetFloat("_ZWrite", source.zWrite ? 1f : 0f);
            if (source.blend)
            {
                material.SetOverrideTag("RenderType", "Transparent");
            }

            if (source.kind == WaterKind)
            {
                material.SetTexture("_NormalMap", Texture(source.normalmap, textures));
                var cube = Resources.Load<Cubemap>(source.cubemap);
                if (cube == null)
                {
                    throw new InvalidOperationException("missing client map cubemap: " + source.cubemap);
                }
                material.SetTexture("_Cube", cube);
                material.SetVector("_NormalScale", Vector(source.normalScale, 1f));
                material.SetVector("_NormalShift", new Vector4(
                    source.normal0Shift[0], source.normal0Shift[1],
                    source.normal1Shift[0], source.normal1Shift[1]));
                material.SetVector("_Fresnel", Vector(source.fresnel, 0f));
                material.SetVector("_ReflectionTint", Vector(source.reflectionTint, 1f));
                material.SetVector("_Options", new Vector4(0f, 0f, 0f, source.fog ? 1f : 0f));
                return material;
            }

            material.SetTexture("_Albedo", Texture(source.albedo, textures));
            if (!string.IsNullOrEmpty(source.lightmap))
            {
                material.SetTexture("_Lightmap", Texture(source.lightmap, textures));
                material.EnableKeyword("_CLIENT_LIGHTMAP");
            }
            if (!string.IsNullOrEmpty(source.alphamask))
            {
                material.SetTexture("_AlphaMask", Texture(source.alphamask, textures));
            }
            if (source.alphaTest)
            {
                material.EnableKeyword("_CLIENT_ALPHATEST");
            }
            material.SetVector("_LightmapST", Vector(source.lightmapST, 0f));
            material.SetVector("_FlatColor", Vector(source.flatColor, 1f));
            material.SetVector("_TexShift", new Vector4(
                source.texShift[0], source.texShift[1],
                source.texShiftSpeed[0], source.texShiftSpeed[1]));
            material.SetVector("_LeafColor", Vector(source.leafColorMul, 0.5f));
            material.SetVector("_LeafOcclusion", Vector(source.leafOcclusion, 0f));
            material.SetFloat("_Cutoff", source.cutoff);
            material.SetVector("_Options", new Vector4(
                string.IsNullOrEmpty(source.alphamask) ? 0f : 1f,
                source.speedTree ? 1f : 0f,
                source.blend ? 1f : 0f,
                source.fog ? 1f : 0f));
            var customFog = source.fogColor != null && source.fogColor.Length >= 3;
            material.SetVector("_Options2", new Vector4(
                source.vertexColor ? 1f : 0f, customFog ? 1f : 0f, 0f, 0f));
            if (customFog)
            {
                material.SetVector("_FogColor", Vector(source.fogColor, 1f));
            }
            if (source.kind == LitKind)
            {
                // lit-materials-fp: metalFresnelReflectance keeps its 0.5
                // default on every lit material of the client.
                material.SetVector("_Lit", new Vector4(
                    source.glossiness, source.specularity, LitFresnelReflectance, 0f));
            }
            return material;
        }

        /// <summary>metalFresnelReflectance of the 11.20 lit effects.</summary>
        public const float LitFresnelReflectance = 0.5f;

        /// <summary>One material of a Godot scene, see GodotMap.hlsl.</summary>
        private static Material CreateGodotMaterial(ClientMapMaterial source, Dictionary<string, Texture> textures)
        {
            var shaderName = source.kind == GodotObjectKind ? GodotObjectShader
                : source.kind == GodotWaterKind ? GodotWaterShader
                : source.kind == GodotVegetationKind ? GodotVegetationShader
                : source.kind == GodotSpriteKind ? GodotSpriteShader
                : source.kind == GodotSkyKind ? GodotSkyShader
                : null;
            if (shaderName == null)
            {
                throw new InvalidOperationException("unknown Godot material kind: " + source.kind);
            }

            var shader = Shader.Find(shaderName);
            if (shader == null)
            {
                throw new InvalidOperationException("missing Godot map shader: " + shaderName);
            }

            var material = new Material(shader) { name = source.name };
            material.renderQueue = source.queue;
            material.SetTexture("_Albedo", Texture(source.albedo, textures));
            material.SetVector("_Color", Vector(source.color, 1f));
            material.SetFloat("_Fog", source.fog ? 1f : 0f);
            material.SetFloat("_Cull", source.cull ? (float)CullMode.Back : (float)CullMode.Off);
            var uv = source.uvTransform != null && source.uvTransform.Length == 4
                ? new Vector4(source.uvTransform[0], source.uvTransform[1],
                    source.uvTransform[2], source.uvTransform[3])
                : new Vector4(1f, 1f, 0f, 0f);
            material.SetVector("_UvTransform", uv);
            material.SetFloat("_Cutoff", source.cutoff);

            if (source.kind == GodotObjectKind)
            {
                material.SetFloat("_Contrast", source.contrast);
                var emissive = !string.IsNullOrEmpty(source.emission);
                material.SetFloat("_Emissive", emissive ? 1f : 0f);
                if (emissive)
                {
                    material.SetTexture("_Emission", Texture(source.emission, textures));
                }
                if (!string.IsNullOrEmpty(source.lightmap))
                {
                    material.SetTexture("_Lightmap", Texture(source.lightmap, textures));
                    var w = source.lightmapSize[0];
                    var h = source.lightmapSize[1];
                    material.SetVector("_LightmapSize", new Vector4(w, h, 1f / w, 1f / h));
                    material.EnableKeyword("_GODOT_LIGHTMAP");
                }
            }
            else if (source.kind == GodotWaterKind)
            {
                material.SetVector("_WaterShift", Vector(source.waterShift, 0f));
            }
            else if (source.kind == GodotVegetationKind)
            {
                material.SetVector("_OriginOffset", Vector(source.originOffset, 0f));
                material.SetVector("_Sway", Vector(source.sway, 1f));
                material.SetVector("_SwayDirection", Vector(source.swayDirection, 0f));
            }
            else if (source.kind == GodotSpriteKind)
            {
                material.SetFloat("_SrcBlend", source.blend ? source.srcBlend : (float)BlendMode.One);
                material.SetFloat("_DstBlend", source.blend ? source.dstBlend : (float)BlendMode.Zero);
                material.SetFloat("_ZWrite", source.zWrite ? 1f : 0f);
                if (source.billboard) material.EnableKeyword("_GODOT_BILLBOARD");
                if (source.alphaTest) material.EnableKeyword("_GODOT_ALPHATEST");
            }
            return material;
        }

        private static Texture Texture(string resource, Dictionary<string, Texture> cache)
        {
            if (string.IsNullOrEmpty(resource))
            {
                return Texture2D.whiteTexture;
            }

            Texture texture;
            if (cache.TryGetValue(resource, out texture))
            {
                return texture;
            }

            texture = Resources.Load<Texture2D>(resource);
            if (texture == null)
            {
                throw new InvalidOperationException("missing client map texture: " + resource);
            }
            cache[resource] = texture;
            return texture;
        }

        private static Vector4 Vector(float[] values, float fill)
        {
            var result = new Vector4(fill, fill, fill, fill);
            if (values == null) return result;
            for (var i = 0; i < values.Length && i < 4; i++) result[i] = values[i];
            return result;
        }

        /// <summary>Little-endian cursor over the payload, without copies.</summary>
        private struct PayloadReader
        {
            private readonly NativeArray<byte> _data;
            public int Offset;

            public PayloadReader(NativeArray<byte> data)
            {
                _data = data;
                Offset = 0;
            }

            public string Ascii(int length)
            {
                var bytes = new byte[length];
                NativeArray<byte>.Copy(_data, Offset, bytes, 0, length);
                Offset += length;
                return Encoding.ASCII.GetString(bytes);
            }

            public int Int()
            {
                var value = _data[Offset] | (_data[Offset + 1] << 8) |
                    (_data[Offset + 2] << 16) | (_data[Offset + 3] << 24);
                Offset += 4;
                return value;
            }

            public T[] Array<T>(int count) where T : struct
            {
                var size = Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<T>();
                var bytes = count * size;
                if (count < 0 || Offset + bytes > _data.Length)
                {
                    throw new InvalidOperationException("client map payload is truncated");
                }
                var result = _data.GetSubArray(Offset, bytes).Reinterpret<T>(1).ToArray();
                Offset += bytes;
                return result;
            }
        }
    }
}
