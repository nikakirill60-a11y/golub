using System.Collections.Generic;
using BlitzLegacy.Core.Maps;
using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Core.Tanks
{
    /// <summary>
    /// The node pivots exported next to the tank mesh by
    /// <c>Tools/port_godot_hangar.py</c>. <c>write_obj</c> bakes every authored
    /// transform into the vertices, so the points <c>rotate_turret</c> and
    /// <c>rotate_gun</c> spin around, and the muzzle the shells leave from, only
    /// survive as this table. Values are stored in raw Godot coordinates and
    /// mirrored across Z on the way out, exactly like the meshes.
    /// </summary>
    public sealed class TankNodeTable
    {
        /// <summary>Resource name passed to the data loader, without the folder.</summary>
        public const string Resource = "ms1_nodes";

        /// <summary>The node the hull rotates the turret around.</summary>
        public const string Turret = "turret01";

        /// <summary>The node the turret elevates the gun around.</summary>
        public const string Gun = "gun01";

        /// <summary>
        /// The muzzle. The source naming is off by one: the child of
        /// <c>gun01</c> is <c>gun02_shoot_pos</c>, and only <c>gun01</c> is in
        /// the visible allowlist, so this is the player's shoot position.
        /// </summary>
        public const string Muzzle = "gun02_shoot_pos";

        /// <summary>
        /// Suffix of a gun's spinning sub-assembly (<c>gun_01_rotor</c>). It
        /// belongs to its gun slot: shown and hidden with it, spun in battle.
        /// </summary>
        public const string RotorSuffix = "_rotor";

        /// <summary>
        /// Infix of a gun's other animated parts (<c>gun_01_anim_reload_drum</c>):
        /// each follows its gun slot like the rotor and is driven by a
        /// <see cref="GunClip"/> track of the same name.
        /// </summary>
        public const string AnimInfix = "_anim_";

        /// <summary>
        /// Infix of the batches of a part bound to a sheet of their own
        /// (<c>gun_04_sheet1</c>, the Silencer barrel): one object per extra
        /// sheet, drawn with its own albedo and moved, shown and hidden with
        /// the object it is named after.
        /// </summary>
        public const string SheetInfix = "_sheet";

        private readonly Dictionary<string, List<GunClip>> _clips =
            new Dictionary<string, List<GunClip>>();

        private readonly Dictionary<string, Entry> _nodes = new Dictionary<string, Entry>();
        private readonly List<string> _turrets = new List<string>();
        private readonly Dictionary<string, List<string>> _guns =
            new Dictionary<string, List<string>>();
        private readonly Dictionary<string, string> _muzzles =
            new Dictionary<string, string>();
        private readonly Dictionary<string, string> _albedos =
            new Dictionary<string, string>();

        private readonly Dictionary<string, JsonNode> _albedoMaterialSettings =
            new Dictionary<string, JsonNode>();

        private readonly Dictionary<string, List<string>> _collisionVisualHosts =
            new Dictionary<string, List<string>>();

        /// <summary>Resolved moving-node names for this card.</summary>
        public string TurretNode { get; private set; } = Turret;
        public string GunNode { get; private set; } = Gun;
        public string MuzzleNode { get; private set; } = Muzzle;

        /// <summary>
        /// Every turret slot the client ships for this vehicle, in the card's
        /// research order. <c>Tools/port_blitz_branch_models.py</c> exports one
        /// object per slot, so the installed module is chosen by name.
        /// </summary>
        public IList<string> TurretNodes
        {
            get { return _turrets; }
        }

        private sealed class Entry
        {
            public string Parent;
            public float[] Local;
            public float[] World;
        }

        /// <summary>How many nodes the porter wrote.</summary>
        public int Count
        {
            get { return _nodes.Count; }
        }

        /// <summary>Reads the <c>{ "nodes": { name: { local, parent, world } } }</c> table.</summary>
        public static TankNodeTable Parse(string json)
        {
            var table = new TankNodeTable();
            if (string.IsNullOrEmpty(json))
            {
                return table;
            }

            var root = JsonNode.Parse(json);
            var rig = root["rig"];
            table.TurretNode = rig["turret"].AsString(Turret);
            table.GunNode = rig["gun"].AsString(Gun);
            table.MuzzleNode = rig["muzzle"].AsString(Muzzle);

            var turrets = rig["turrets"];
            if (turrets != null && turrets.Kind == JsonKind.Object)
            {
                foreach (var entry in turrets.Fields)
                {
                    table._turrets.Add(entry.Key);
                    var guns = new List<string>();
                    var list = entry.Value["guns"];
                    for (var i = 0; i < list.Count; i++)
                    {
                        guns.Add(list[i].AsString());
                    }

                    table._guns[entry.Key] = guns;
                    var muzzles = entry.Value["muzzles"];
                    if (muzzles != null && muzzles.Kind == JsonKind.Object)
                    {
                        foreach (var muzzle in muzzles.Fields)
                        {
                            table._muzzles[MuzzleKey(entry.Key, muzzle.Key)] =
                                muzzle.Value.AsString();
                        }
                    }
                }
            }

            var albedos = root["albedos"];
            if (albedos != null && albedos.Kind == JsonKind.Object)
            {
                foreach (var field in albedos.Fields)
                {
                    table._albedos[field.Key] = field.Value.AsString();
                }
            }

            var materialSettings = root["albedoMaterialSettings"];
            if (materialSettings != null && materialSettings.Kind == JsonKind.Object)
            {
                foreach (var field in materialSettings.Fields)
                {
                    if (field.Value.Kind == JsonKind.Object)
                    { table._albedoMaterialSettings[field.Key] = field.Value; }
                }
            }

            var visualHosts = root["collisionVisualHosts"];
            if (visualHosts != null && visualHosts.Kind == JsonKind.Object)
            {
                foreach (var field in visualHosts.Fields)
                {
                    if (field.Value.Kind != JsonKind.Array) { continue; }
                    var hosts = new List<string>();
                    foreach (var value in field.Value.Items) { hosts.Add(value.AsString()); }
                    table._collisionVisualHosts[field.Key] = hosts;
                }
            }

            var animations = root["animations"];
            if (animations != null && animations.Kind == JsonKind.Object)
            {
                foreach (var gun in animations.Fields)
                {
                    var clips = new List<GunClip>();
                    foreach (var clip in gun.Value.Fields)
                    {
                        clips.Add(GunClip.Parse(clip.Key, clip.Value));
                    }

                    table._clips[gun.Key] = clips;
                }
            }

            var nodes = root["nodes"];
            foreach (var field in nodes.Fields)
            {
                table._nodes[field.Key] = new Entry
                {
                    Parent = field.Value["parent"].AsString(),
                    Local = Triple(field.Value["local"]),
                    World = Triple(field.Value["world"]),
                };
            }

            return table;
        }

        /// <summary>Baked sub-assembly clips of one gun slot (none for most guns).</summary>
        public IList<GunClip> ClipsOf(string gunNode)
        {
            List<GunClip> clips;
            return _clips.TryGetValue(gunNode ?? string.Empty, out clips)
                ? (IList<GunClip>)clips
                : new List<GunClip>();
        }

        /// <summary>
        /// The object an extra-sheet object (<c>gun_04_sheet1</c>,
        /// <c>hull_vis_sheet1</c>) is split from, or null for any other name.
        /// </summary>
        public static string SheetOwner(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            var infix = name.LastIndexOf(SheetInfix, System.StringComparison.Ordinal);
            var digits = infix + SheetInfix.Length;
            if (infix <= 0 || digits >= name.Length)
            {
                return null;
            }

            for (var i = digits; i < name.Length; i++)
            {
                if (!char.IsDigit(name[i]))
                {
                    return null;
                }
            }

            return name.Substring(0, infix);
        }

        /// <summary>
        /// The module slot a part object belongs to: a gun's moving parts
        /// (<c>gun_01_rotor</c>, <c>gun_01_anim_*</c>) and the extra-sheet
        /// objects of a turret or gun (<c>gun_04_sheet1</c>); null for any
        /// other object.
        /// </summary>
        public string PartOwner(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            name = SheetOwner(name) ?? name;
            if (IsModuleNode(name))
            {
                return name;
            }

            string owner = null;
            if (name.EndsWith(RotorSuffix, System.StringComparison.Ordinal))
            {
                owner = name.Substring(0, name.Length - RotorSuffix.Length);
            }
            else
            {
                var infix = name.IndexOf(AnimInfix, System.StringComparison.Ordinal);
                if (infix > 0)
                {
                    owner = name.Substring(0, infix);
                }
            }

            return owner != null && IsModuleNode(owner) ? owner : null;
        }

        /// <summary>The gun slots one turret can mount, in research order.</summary>
        public IList<string> GunsOf(string turretNode)
        {
            List<string> guns;
            return _guns.TryGetValue(turretNode ?? string.Empty, out guns)
                ? (IList<string>)guns
                : new List<string>();
        }

        /// <summary>
        /// The turret slot to show: the installed module's mesh when the export
        /// carries it, and the stock rig otherwise, so a card exported before
        /// the branch port still rigs.
        /// </summary>
        public string ResolveTurret(string mesh)
        {
            return _guns.ContainsKey(mesh ?? string.Empty) ? mesh : TurretNode;
        }

        /// <summary>The gun slot to show out of one turret's own slots.</summary>
        public string ResolveGun(string turretNode, string mesh)
        {
            var guns = GunsOf(turretNode);
            for (var i = 0; i < guns.Count; i++)
            {
                if (guns[i] == mesh)
                {
                    return mesh;
                }
            }

            return guns.Count > 0 ? guns[0] : GunNode;
        }

        /// <summary>The muzzle marker of one gun, or the gun itself when the
        /// client ships no <c>HP_gunFire</c> hardpoint for it.</summary>
        public string MuzzleOf(string turretNode, string gunNode)
        {
            string muzzle;
            if (_muzzles.TryGetValue(MuzzleKey(turretNode, gunNode), out muzzle))
            {
                return muzzle;
            }

            return Has(gunNode) ? gunNode : MuzzleNode;
        }

        /// <summary>True when the node is a turret or gun slot of this card.</summary>
        public bool IsModuleNode(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            if (_guns.ContainsKey(name))
            {
                return true;
            }

            foreach (var entry in _guns)
            {
                for (var i = 0; i < entry.Value.Count; i++)
                {
                    if (entry.Value[i] == name)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static string MuzzleKey(string turretNode, string gunNode)
        {
            return turretNode + "/" + gunNode;
        }

        /// <summary>True when the client scene binds a sheet per node.</summary>
        public bool HasNodeAlbedos
        {
            get { return _albedos.Count > 0; }
        }

        /// <summary>The SC2 albedo resource id bound to this visible module.</summary>
        public string AlbedoOf(string name)
        {
            string value;
            return _albedos.TryGetValue(name ?? string.Empty, out value) ? value : null;
        }

        /// <summary>
        /// Optional source-visible assembly for an internal collision carrier.
        /// Oscillating turrets may draw their body in the gun-mask mesh while
        /// the yaw node draws only a mounting plane. This is QA provenance,
        /// not a replacement mount or a runtime collision offset.
        /// </summary>
        public IReadOnlyList<string> CollisionVisualHostsOf(string mount)
        {
            List<string> hosts;
            return _collisionVisualHosts.TryGetValue(mount ?? string.Empty, out hosts)
                ? (IReadOnlyList<string>)hosts : new string[0];
        }

        public bool HasMaterialSettings(string name)
        { return _albedoMaterialSettings.ContainsKey(name ?? string.Empty); }

        public float AlphaCutoffOf(string name)
        {
            JsonNode settings;
            return _albedoMaterialSettings.TryGetValue(name ?? string.Empty, out settings)
                ? settings["alphaCutoff"].AsFloat() : 0f;
        }

        public int CullModeOf(string name)
        {
            JsonNode settings;
            return _albedoMaterialSettings.TryGetValue(name ?? string.Empty, out settings)
                ? settings["cullMode"].AsInt(2) : 2;
        }

        /// <summary>True when the porter exported this node.</summary>
        public bool Has(string name)
        {
            return _nodes.ContainsKey(name);
        }

        /// <summary>The authored parent node, which is what the pivot hangs off.</summary>
        public string ParentOf(string name)
        {
            Entry entry;
            return _nodes.TryGetValue(name, out entry) ? entry.Parent : null;
        }

        /// <summary>The node origin relative to its parent, mirrored into battle space.</summary>
        public Vec3 Local(string name)
        {
            return Mirror(name, true);
        }

        /// <summary>The node origin in model space, mirrored into battle space.</summary>
        public Vec3 World(string name)
        {
            return Mirror(name, false);
        }

        /// <summary>
        /// The node origin exactly as the porter wrote it, without the battle
        /// mirror. The hangar shows the imported model untouched, so whatever
        /// it places beside that model has to stay in the exporter's own
        /// space. Mirroring there moved every turret and gun plate to the
        /// wrong end of the tank, which is what pushed the armour overlay out
        /// over the turret roof.
        /// </summary>
        public Vec3 SourceWorld(string name)
        {
            Entry entry;
            if (!_nodes.TryGetValue(name, out entry))
            {
                return new Vec3(0f, 0f, 0f);
            }

            return new Vec3(entry.World[0], entry.World[1], entry.World[2]);
        }

        /// <summary>
        /// The node origin in the space of the imported model the hangar
        /// draws: the exporter's space with Unity's OBJ X flip and no battle
        /// mirror.
        /// </summary>
        public Vec3 ModelWorld(string name)
        {
            var point = SourceWorld(name);
            return new Vec3(-point.X, point.Y, point.Z);
        }

        private Vec3 Mirror(string name, bool local)
        {
            Entry entry;
            if (!_nodes.TryGetValue(name, out entry))
            {
                return new Vec3(0f, 0f, 0f);
            }

            // The imported mesh has X negated. Turning it halfway around Y
            // for battle negates X again and Z once, leaving raw source X and
            // mirrored Z. Pivots must follow the same conversion as vertices.
            return GodotSpace.Point(local ? entry.Local : entry.World);
        }

        private static float[] Triple(JsonNode node)
        {
            var values = new float[3];
            for (var i = 0; i < 3 && i < node.Count; i++)
            {
                values[i] = (float)node[i].AsDouble();
            }

            return values;
        }
    }
}
