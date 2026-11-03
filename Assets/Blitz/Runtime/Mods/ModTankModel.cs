using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Mods;
using UnityEngine;

namespace BlitzLegacy.Runtime.Mods
{
    /// <summary>
    /// Builds the meshes a mod ships as a .glb into the same shapes the
    /// shipped vehicles arrive in: one visible object per authored node, one
    /// armour mesh with a submesh per plate, and the invisible physics hull.
    ///
    /// Tools/port_godot_hangar.py does this offline for the vehicles in the
    /// build and hands Unity an OBJ. That importer negates X and reverses the
    /// winding, so the runtime works in a space no mod can reach on its own.
    /// Everything built here goes through the same two flips, which is what
    /// lets the hangar, the battle mirror and the armour overlay treat a mod
    /// exactly like a card that shipped with the game.
    /// </summary>
    public static class ModTankModel
    {
        private sealed class Build
        {
            public GameObject Visible;
            public GameObject Armor;
            public GameObject Physics;
            public string NodesJson = string.Empty;
            public string ArmorJson = string.Empty;
        }

        private static readonly Dictionary<string, Build> Builds =
            new Dictionary<string, Build>(StringComparer.OrdinalIgnoreCase);

        private static GameObject _templates;

        /// <summary>Drops every built mesh, so a reinstall is picked up.</summary>
        public static void Forget()
        {
            Builds.Clear();
            if (_templates != null)
            {
                UnityEngine.Object.DestroyImmediate(_templates);
                _templates = null;
            }
        }

        /// <summary>The visible model of a mod tank, or null.</summary>
        public static GameObject Visible(string tankName)
        {
            var build = Of(tankName);
            return build == null ? null : build.Visible;
        }

        /// <summary>The armour plates of a mod tank, or null.</summary>
        public static GameObject Armor(string tankName)
        {
            var build = Of(tankName);
            return build == null ? null : build.Armor;
        }

        /// <summary>The physics hull of a mod tank, or null.</summary>
        public static GameObject Physics(string tankName)
        {
            var build = Of(tankName);
            return build == null ? null : build.Physics;
        }

        /// <summary>The node rig of a mod tank, or an empty string.</summary>
        public static string NodesJson(string tankName)
        {
            var build = Of(tankName);
            return build == null ? string.Empty : build.NodesJson;
        }

        /// <summary>The armour table of a mod tank, or an empty string.</summary>
        public static string ArmorJson(string tankName)
        {
            var build = Of(tankName);
            return build == null ? string.Empty : build.ArmorJson;
        }

        // ------------------------------------------------------------------

        private static Build Of(string tankName)
        {
            if (string.IsNullOrEmpty(tankName)) { return null; }

            Build build;
            if (Builds.TryGetValue(tankName, out build))
            {
                return build != null && build.Visible != null ? build : null;
            }

            build = null;
            var tank = ModRuntime.TankAssets(tankName);
            if (tank != null && tank.HasModel)
            {
                try
                {
                    // A model that takes the game down while it builds switches
                    // its mod off for the next start.
                    using (ModRuntime.Guard.Loading(ModRuntime.TankModId(tankName)))
                    {
                        build = Create(tank);
                    }
                }
                catch (Exception error)
                {
                    Debug.LogWarning("Mods: " + tankName + " model: " + error.Message);
                    build = null;
                }
            }

            Builds[tankName] = build;
            return build;
        }

        private static Build Create(ModVehicleAssets tank)
        {
            var file = GlbFile.Load(tank.ModelPath);
            var build = new Build
            {
                NodesJson = string.IsNullOrEmpty(tank.NodesPath) ? ModTankRig.NodesJson(file) : System.IO.File.ReadAllText(tank.NodesPath),
                ArmorJson = string.IsNullOrEmpty(tank.ArmorPath) ? ModTankRig.ArmorJson(file) : System.IO.File.ReadAllText(tank.ArmorPath),
            };

            build.Visible = BuildVisible(file, tank.Id);
            build.Armor = BuildArmor(file, tank.Id);
            build.Physics = BuildPhysics(file, tank.Id);
            return build;
        }

        /// <summary>One child per authored node, exactly like the OBJ objects.</summary>
        private static GameObject BuildVisible(GlbFile file, string id)
        {
            var root = Template(id);
            var built = 0;
            for (var i = 0; i < file.Nodes.Count; i++)
            {
                var node = file.Nodes[i];
                if (!node.HasGeometry || !ModTankRig.IsVisible(node.Name)) { continue; }

                var mesh = MeshOf(node.Name, new List<GlbNode> { node }, false);
                if (mesh == null) { continue; }

                var child = new GameObject(node.Name);
                child.transform.SetParent(root.transform, false);
                child.AddComponent<MeshFilter>().sharedMesh = mesh;
                child.AddComponent<MeshRenderer>();
                built++;
            }

            if (built == 0)
            {
                UnityEngine.Object.DestroyImmediate(root);
                return null;
            }

            return root;
        }

        /// <summary>One mesh, one submesh per plate, in armour table order.</summary>
        private static GameObject BuildArmor(GlbFile file, string id)
        {
            var plates = ModTankRig.ArmorNodes(file);
            if (plates.Count == 0) { return null; }

            var mesh = MeshOf(id + "_armor", plates, true);
            if (mesh == null) { return null; }

            var root = Template(id + "_armor");
            root.AddComponent<MeshFilter>().sharedMesh = mesh;
            root.AddComponent<MeshRenderer>();
            return root;
        }

        private static GameObject BuildPhysics(GlbFile file, string id)
        {
            var hulls = new List<GlbNode>();
            for (var i = 0; i < file.Nodes.Count; i++)
            {
                if (file.Nodes[i].HasGeometry && ModTankRig.IsPhysicsHull(file.Nodes[i].Name))
                {
                    hulls.Add(file.Nodes[i]);
                }
            }

            if (hulls.Count == 0) { return null; }

            var mesh = MeshOf(id + "_phys", hulls, false);
            if (mesh == null) { return null; }

            var root = Template(id + "_phys");
            root.AddComponent<MeshFilter>().sharedMesh = mesh;
            root.AddComponent<MeshRenderer>();
            return root;
        }

        /// <summary>
        /// Merges nodes into one mesh. <paramref name="split"/> keeps one
        /// submesh per node, which is how the armour table addresses a plate.
        /// </summary>
        private static Mesh MeshOf(string name, List<GlbNode> nodes, bool split)
        {
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var groups = new List<List<int>>();
            var shared = split ? null : new List<int>();
            if (!split) { groups.Add(shared); }

            for (var n = 0; n < nodes.Count; n++)
            {
                var triangles = split ? new List<int>() : shared;
                if (split) { groups.Add(triangles); }

                var primitives = nodes[n].Primitives;
                for (var p = 0; p < primitives.Count; p++)
                {
                    var primitive = primitives[p];
                    var offset = vertices.Count;
                    var count = primitive.VertexCount;
                    for (var v = 0; v < count; v++)
                    {
                        // Unity's OBJ importer negates X; the mods have to land
                        // in the very same space as the shipped models.
                        vertices.Add(new Vector3(
                            -primitive.Positions[v * 3],
                            primitive.Positions[(v * 3) + 1],
                            primitive.Positions[(v * 3) + 2]));
                        normals.Add(primitive.Normals.Length > (v * 3) + 2
                            ? new Vector3(
                                -primitive.Normals[v * 3],
                                primitive.Normals[(v * 3) + 1],
                                primitive.Normals[(v * 3) + 2])
                            : Vector3.up);
                        uvs.Add(primitive.Uvs.Length > (v * 2) + 1
                            ? new Vector2(
                                primitive.Uvs[v * 2], 1f - primitive.Uvs[(v * 2) + 1])
                            : Vector2.zero);
                    }

                    // Negating one axis reverses the orientation of every face,
                    // so the winding flips back with it.
                    for (var i = 0; i + 2 < primitive.Indices.Length; i += 3)
                    {
                        triangles.Add(offset + primitive.Indices[i + 2]);
                        triangles.Add(offset + primitive.Indices[i + 1]);
                        triangles.Add(offset + primitive.Indices[i]);
                    }
                }
            }

            if (vertices.Count == 0) { return null; }

            var mesh = new Mesh { name = name };
            if (vertices.Count > 65000)
            {
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            }

            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = groups.Count;
            for (var i = 0; i < groups.Count; i++)
            {
                mesh.SetTriangles(groups[i], i);
            }

            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// A template lives under one inactive holder, so it never renders and
        /// never survives into a scene on its own, yet an Instantiate of it
        /// comes out active exactly like a Resources prefab.
        /// </summary>
        private static GameObject Template(string name)
        {
            if (_templates == null)
            {
                _templates = new GameObject("~ModTankTemplates");
                _templates.SetActive(false);
                _templates.hideFlags = HideFlags.HideAndDontSave;
                if (Application.isPlaying)
                {
                    UnityEngine.Object.DontDestroyOnLoad(_templates);
                }
            }

            var template = new GameObject(name);
            template.transform.SetParent(_templates.transform, false);
            return template;
        }
    }
}

