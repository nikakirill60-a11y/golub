using System.Collections.Generic;
using BlitzLegacy.Core.Maps;
using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Core.Tanks
{
    /// <summary>
    /// The armour plates exported next to the tank mesh by
    /// <c>Tools/port_godot_hangar.py</c>. Godot assembles them at load time:
    /// <c>sort_armor</c> walks the model for meshes whose names carry
    /// <c>armor</c> or <c>screen</c>, plus the gun mask, and
    /// <c>add_armor_from_mesh</c> reads the thickness out of the last token of
    /// the name before hiding the mesh and giving it a trimesh collider. The
    /// porter does that walk once and writes the result here, beside
    /// <c>MS1_armor.obj</c>.
    /// </summary>
    public sealed class TankArmorTable
    {
        /// <summary>Resource name passed to the data loader, without the folder.</summary>
        public const string Resource = "ms1_armor";

        /// <summary>The model holding one hidden mesh per plate.</summary>
        public const string ModelResource = "Models/Tanks/MS1/MS1_armor";

        /// <summary>Mount of the plates that ride the hull rather than a moving node.</summary>
        public const string HullMount = "hull";

        /// <summary>One plate: how thick it is and what it turns with.</summary>
        public struct Plate
        {
            /// <summary>Mesh name in the exported model, such as <c>_armor_13</c>.</summary>
            public string Name;

            /// <summary>
            /// The node the plate rides: <see cref="HullMount"/>,
            /// <see cref="TankNodeTable.Turret"/> or <see cref="TankNodeTable.Gun"/>.
            /// </summary>
            public string Mount;
            /// <summary>Optional turret context for a shared gun donor.</summary>
            public string RequiredTurret;

            /// <summary>Nominal thickness in millimetres.</summary>
            public int Armor;

            /// <summary>
            /// Spaced armour. Godot puts screens on their own collision layer so
            /// a shell bleeds penetration across the gap instead of stopping on
            /// the first plate it meets.
            /// </summary>
            public bool Screen;

            /// <summary>
            /// Submesh the plate occupies in <see cref="ModelResource"/>. Unity's
            /// OBJ importer merges the object records into one mesh and splits it
            /// into one submesh per material, so after import this index is all
            /// that tells the plates apart. The porter gives every plate its own
            /// material to keep that index meaningful.
            /// </summary>
            public int Submesh;

            /// <summary>
            /// Triangles the porter exported for the plate, so the runtime can
            /// check the submesh it takes a collider from is the expected one.
            /// </summary>
            public int Triangles;

            /// <summary>
            /// The damageable module this plate stands for, empty for ordinary
            /// armour. Godot writes the same value into the collision body's
            /// <c>module</c> metadata and <c>module_collide</c> reads it back;
            /// the only plate the client ships one for is
            /// <see cref="TankModuleIds.Tracks"/>.
            /// </summary>
            public string Module;

            /// <summary>Whether a shell that lands here also damages a module.</summary>
            public bool IsModule
            {
                get { return !string.IsNullOrEmpty(Module); }
            }
        }

        private readonly List<Plate> _plates = new List<Plate>();
        private readonly Dictionary<string, Vec3> _mountOffsets =
            new Dictionary<string, Vec3>();

        /// <summary>The plates, in the order the porter wrote them.</summary>
        public IReadOnlyList<Plate> Plates
        {
            get { return _plates; }
        }

        /// <summary>How many plates the porter exported.</summary>
        public int Count
        {
            get { return _plates.Count; }
        }

        /// <summary>The thinnest plate, which is what <c>min_armor</c> reports.</summary>
        public int MinArmor
        {
            get { return Extreme(true); }
        }

        /// <summary>The thickest plate, which is what <c>max_armor</c> reports.</summary>
        public int MaxArmor
        {
            get { return Extreme(false); }
        }

        /// <summary>
        /// <c>avg_armor</c>. The source does not average the plates, it takes
        /// the midpoint of the thinnest and the thickest.
        /// </summary>
        public float AvgArmor
        {
            get { return (MinArmor + MaxArmor) * 0.5f; }
        }

        /// <summary>Reads the <c>{ "plates": [ { armor, mount, name, screen } ] }</c> table.</summary>
        public static TankArmorTable Parse(string json)
        {
            var table = new TankArmorTable();
            if (string.IsNullOrEmpty(json))
            {
                return table;
            }

            var root = JsonNode.Parse(json);
            var plates = root["plates"];
            for (var i = 0; i < plates.Count; i++)
            {
                var plate = plates[i];
                table._plates.Add(new Plate
                {
                    Name = plate["name"].AsString(),
                    Mount = plate["mount"].AsString(),
                    RequiredTurret = plate["required_turret"].AsString(),
                    Armor = plate["armor"].AsInt(),
                    Screen = plate["screen"].AsBool(),
                    Submesh = plate["submesh"].AsInt(),
                    Triangles = plate["triangles"].AsInt(),
                    Module = plate["module"].AsString(),
                });
            }

            // The exporter recovers a part's pivot by matching bounding box
            // centres, so a tall cupola or an antenna lifts the recovered
            // origin above the real one and the whole collision shell rides
            // with it: the IS-7 turret shell sat 0.46 m over its turret.
            // Tools/fit_armor_offsets.py measures that error per mount and
            // writes it here; the node pivot itself stays untouched so the
            // turret keeps rotating around its exported axis.
            var offsets = root["mountOffsets"];
            if (offsets != null && offsets.Kind == JsonKind.Object)
            {
                foreach (var field in offsets.Fields)
                {
                    var value = field.Value;
                    if (value == null || value.Count < 3) { continue; }
                    table._mountOffsets[field.Key] = new Vec3(
                        value[0].AsFloat(), value[1].AsFloat(), value[2].AsFloat());
                }
            }

            return table;
        }

        /// <summary>
        /// How far the plates of one mount have to move to sit on the part
        /// they armour, in the exporter's own space. Zero for every mount the
        /// exporter already placed correctly.
        /// </summary>
        public Vec3 OffsetOf(string mount)
        {
            Vec3 offset;
            if (mount != null && _mountOffsets.TryGetValue(mount, out offset))
            {
                return offset;
            }

            return new Vec3(0f, 0f, 0f);
        }

        /// <summary>
        /// The same correction in battle space: the imported model turns
        /// halfway around Y, cancelling the importer's X flip and negating Z.
        /// </summary>
        public Vec3 MirroredOffsetOf(string mount)
        {
            var offset = OffsetOf(mount);
            return new Vec3(offset.X, offset.Y, -offset.Z);
        }

        /// <summary>The correction in the imported model's space (X flipped).</summary>
        public Vec3 ModelOffsetOf(string mount)
        {
            var offset = OffsetOf(mount);
            return new Vec3(-offset.X, offset.Y, offset.Z);
        }

        /// <summary>True when the porter exported a plate under this mesh name.</summary>
        public bool Has(string name)
        {
            return Index(name) >= 0;
        }

        /// <summary>The plate exported under this mesh name.</summary>
        public Plate Find(string name)
        {
            var index = Index(name);
            return index < 0 ? default(Plate) : _plates[index];
        }

        /// <summary>Nominal thickness of a plate, zero when it is not exported.</summary>
        public int ArmorOf(string name)
        {
            var index = Index(name);
            return index < 0 ? 0 : _plates[index].Armor;
        }

        /// <summary>The node a plate rides, null when it is not exported.</summary>
        public string MountOf(string name)
        {
            var index = Index(name);
            return index < 0 ? null : _plates[index].Mount;
        }

        /// <summary>Whether a plate is spaced armour.</summary>
        public bool IsScreen(string name)
        {
            var index = Index(name);
            return index >= 0 && _plates[index].Screen;
        }

        /// <summary>The plates riding one mount, such as the turret.</summary>
        public List<Plate> OnMount(string mount)
        {
            var plates = new List<Plate>();
            for (var i = 0; i < _plates.Count; i++)
            {
                if (_plates[i].Mount == mount)
                {
                    plates.Add(_plates[i]);
                }
            }

            return plates;
        }

        private int Index(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return -1;
            }

            for (var i = 0; i < _plates.Count; i++)
            {
                if (_plates[i].Name == name)
                {
                    return i;
                }
            }

            return -1;
        }

        private int Extreme(bool smallest)
        {
            var value = 0;
            var seen = false;
            for (var i = 0; i < _plates.Count; i++)
            {
                // `sort_armor` collects armour and screen meshes only, so the
                // track module never counted towards the card's armour spread.
                if (_plates[i].IsModule) { continue; }

                var armor = _plates[i].Armor;
                if (!seen || (smallest ? armor < value : armor > value))
                {
                    value = armor;
                    seen = true;
                }
            }

            return value;
        }
    }
}
