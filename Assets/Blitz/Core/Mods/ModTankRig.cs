using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Core.Mods
{
    /// <summary>
    /// Turns a mod's .glb scene into the two tables the runtime reads beside a
    /// shipped vehicle: the node rig and the armour plates.
    ///
    /// Godot builds both at load time. <c>sort_armor</c> walks the model for
    /// meshes whose names carry <c>armor</c>, <c>screen</c> or a gun mask and
    /// reads the thickness out of the last token of the name;
    /// <c>rotate_turret</c> and <c>rotate_gun</c> spin the <c>turret01</c> and
    /// <c>gun01</c> nodes. Tools/port_godot_hangar.py does that walk once for
    /// the shipped tanks and writes Resources/Data/&lt;id&gt;_nodes.json and
    /// &lt;id&gt;_armor.json. A mod arrives after the build, so the same walk
    /// happens here and produces the very same JSON.
    ///
    /// Everything is written in the space Unity's OBJ importer leaves the
    /// shipped models in, which negates X and keeps Y and Z. Staying in one
    /// space is what lets the battle mirror and the hangar overlay treat a mod
    /// exactly like a shipped card.
    /// </summary>
    public static class ModTankRig
    {
        public const string HullMount = "hull";
        public const string TracksModule = "tracks";

        /// <summary>A source point as Unity's OBJ import leaves it.</summary>
        public static float[] ImportPoint(float[] source)
        {
            return new[] { -source[0], source[1], source[2] };
        }

        /// <summary>True for the meshes Godot hides and turns into armour.</summary>
        public static bool IsArmor(string name)
        {
            var lower = Lower(name);
            return lower.Contains("_armor") || lower.Contains("_screen")
                || lower.Contains("_screem") || lower.Contains("gun_mask")
                || lower.Contains("_module_");
        }

        /// <summary>The invisible collision hull Godot drives the tank with.</summary>
        public static bool IsPhysicsHull(string name)
        {
            return Lower(name).EndsWith("hull_phys", StringComparison.Ordinal);
        }

        /// <summary>Markers and imported collision leftovers never get drawn.</summary>
        public static bool IsMarker(string name)
        {
            var lower = Lower(name);
            return lower.Contains("shoot_pos") || lower.Contains("havok")
                || lower.Contains("_batch_") || lower.StartsWith("mesh_", StringComparison.Ordinal);
        }

        /// <summary>True for the meshes the player is meant to see.</summary>
        public static bool IsVisible(string name)
        {
            return !string.IsNullOrEmpty(name) && !IsArmor(name)
                && !IsPhysicsHull(name) && !IsMarker(name);
        }

        public static bool IsScreen(string name)
        {
            var lower = Lower(name);
            return lower.Contains("_screen") || lower.Contains("_screem");
        }

        public static bool IsTracksModule(string name)
        {
            return Lower(name).Contains("_module_tracks");
        }

        /// <summary>The millimetres Godot reads out of the last name token.</summary>
        public static int Thickness(string name)
        {
            if (string.IsNullOrEmpty(name)) { return 0; }

            var end = name.Length;
            while (end > 0 && !char.IsDigit(name[end - 1])) { end--; }

            var start = end;
            while (start > 0 && char.IsDigit(name[start - 1])) { start--; }

            int value;
            return start < end && int.TryParse(name.Substring(start, end - start), out value)
                ? value
                : 0;
        }

        /// <summary>The node the turret spins around, or an empty string.</summary>
        public static string TurretNode(GlbFile file)
        {
            return FirstNamed(file, "turret");
        }

        /// <summary>The node the gun elevates around, or an empty string.</summary>
        public static string GunNode(GlbFile file)
        {
            return FirstNamed(file, "gun0");
        }

        /// <summary>The muzzle marker of the gun, or an empty string.</summary>
        public static string MuzzleNode(GlbFile file)
        {
            for (var i = 0; i < file.Nodes.Count; i++)
            {
                if (Lower(file.Nodes[i].Name).Contains("shoot_pos"))
                {
                    return file.Nodes[i].Name;
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// Which moving part a plate or a mesh rides, read off the authored
        /// parent chain the way Godot reparents the armour bodies.
        /// </summary>
        public static string MountOf(GlbFile file, int index)
        {
            var turret = TurretNode(file);
            var gun = GunNode(file);
            var guard = 0;
            var at = index;
            while (at >= 0 && guard++ < file.Nodes.Count)
            {
                var name = file.Nodes[at].Name;
                if (!string.IsNullOrEmpty(gun) && name == gun) { return gun; }
                if (!string.IsNullOrEmpty(turret) && name == turret) { return turret; }
                at = file.Nodes[at].Parent;
            }

            return HullMount;
        }

        /// <summary>The plates, in the order the armour mesh stores them.</summary>
        public static List<GlbNode> ArmorNodes(GlbFile file)
        {
            var plates = new List<GlbNode>();
            for (var i = 0; i < file.Nodes.Count; i++)
            {
                var node = file.Nodes[i];
                if (node.HasGeometry && IsArmor(node.Name)) { plates.Add(node); }
            }

            return plates;
        }

        /// <summary>The <c>&lt;id&gt;_nodes.json</c> table for this scene.</summary>
        public static string NodesJson(GlbFile file)
        {
            var turret = TurretNode(file);
            var gun = GunNode(file);
            var muzzle = MuzzleNode(file);

            var rig = JsonNode.NewObject();
            rig.Set("turret", turret);
            rig.Set("gun", gun);
            rig.Set("muzzle", string.IsNullOrEmpty(muzzle) ? gun : muzzle);

            var turrets = JsonNode.NewObject();
            if (!string.IsNullOrEmpty(turret))
            {
                var slot = JsonNode.NewObject();
                var guns = JsonNode.NewArray();
                var muzzles = JsonNode.NewObject();
                if (!string.IsNullOrEmpty(gun))
                {
                    guns.Add(JsonNode.Str(gun));
                    muzzles.Set(gun, string.IsNullOrEmpty(muzzle) ? gun : muzzle);
                }

                slot.Set("guns", guns);
                slot.Set("muzzles", muzzles);
                turrets.Set(turret, slot);
            }

            rig.Set("turrets", turrets);

            var nodes = JsonNode.NewObject();
            for (var i = 0; i < file.Nodes.Count; i++)
            {
                var node = file.Nodes[i];
                if (string.IsNullOrEmpty(node.Name) || nodes.Has(node.Name)) { continue; }

                var entry = JsonNode.NewObject();
                entry.Set("parent", node.Parent >= 0 ? file.Nodes[node.Parent].Name : string.Empty);
                entry.Set("local", Point(ImportPoint(file.LocalOrigin(node))));
                entry.Set("world", Point(ImportPoint(node.WorldOrigin)));
                nodes.Set(node.Name, entry);
            }

            var root = JsonNode.NewObject();
            root.Set("albedos", JsonNode.NewObject());
            root.Set("rig", rig);
            root.Set("nodes", nodes);
            return root.ToJson(false);
        }

        /// <summary>The <c>&lt;id&gt;_armor.json</c> table for this scene.</summary>
        public static string ArmorJson(GlbFile file)
        {
            var plates = JsonNode.NewArray();
            var submesh = 0;
            for (var i = 0; i < file.Nodes.Count; i++)
            {
                var node = file.Nodes[i];
                if (!node.HasGeometry || !IsArmor(node.Name)) { continue; }

                var triangles = 0;
                for (var p = 0; p < node.Primitives.Count; p++)
                {
                    triangles += node.Primitives[p].TriangleCount;
                }

                var plate = JsonNode.NewObject();
                plate.Set("name", node.Name);
                plate.Set("mount", MountOf(file, i));
                plate.Set("armor", Thickness(node.Name));
                plate.Set("screen", IsScreen(node.Name));
                plate.Set("submesh", submesh++);
                plate.Set("triangles", triangles);
                plate.Set("module", IsTracksModule(node.Name) ? TracksModule : string.Empty);
                plates.Add(plate);
            }

            var root = JsonNode.NewObject();
            root.Set("plates", plates);
            root.Set("mountOffsets", JsonNode.NewObject());
            return root.ToJson(false);
        }

        private static JsonNode Point(float[] value)
        {
            var array = JsonNode.NewArray();
            for (var i = 0; i < 3; i++) { array.Add(JsonNode.Num(Round(value[i]))); }
            return array;
        }

        private static double Round(float value)
        {
            return Math.Round(value, 6);
        }

        private static string FirstNamed(GlbFile file, string prefix)
        {
            for (var i = 0; i < file.Nodes.Count; i++)
            {
                var name = file.Nodes[i].Name;
                if (Lower(name).StartsWith(prefix, StringComparison.Ordinal)
                    && !IsMarker(name))
                {
                    return name;
                }
            }

            return string.Empty;
        }

        private static string Lower(string value)
        {
            return string.IsNullOrEmpty(value) ? string.Empty : value.ToLowerInvariant();
        }
    }
}

