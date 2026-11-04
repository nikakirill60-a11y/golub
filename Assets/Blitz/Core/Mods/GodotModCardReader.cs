using System;
using System.Collections.Generic;
using System.IO;
using BlitzLegacy.Core.Tanks;

namespace BlitzLegacy.Core.Mods
{
    /// <summary>
    /// Turns a Godot mod card into the <see cref="TankInfo"/> the Unity port
    /// fights with.
    ///
    /// The mods were authored against the Godot resource scripts
    /// (tank_info.gd, module_gun.gd, shell_info.gd and friends), and the port
    /// already carries the same fields under English names, so the conversion
    /// is a field-by-field lift. Anything the card leaves out keeps the
    /// port's own default, which is what Godot did with an unset export too.
    /// </summary>
    public static class GodotModCardReader
    {
        public static TankInfo Read(string cardPath)
        {
            if (string.IsNullOrEmpty(cardPath) || !File.Exists(cardPath))
            {
                return null;
            }

            return Read(
                GodotResourceFile.Parse(File.ReadAllText(cardPath)),
                Path.GetFileNameWithoutExtension(cardPath));
        }

        public static TankInfo Read(GodotResourceFile file, string fallbackName)
        {
            if (file == null)
            {
                return null;
            }

            var root = file.Root;
            if (root == null)
            {
                return null;
            }

            var tank = new TankInfo();
            tank.Name = Name(root, fallbackName);
            tank.DisplayName = GodotResourceFile.Text(
                First(root, "display_name", "name"));
            if (string.IsNullOrEmpty(tank.DisplayName))
            {
                tank.DisplayName = tank.Name;
            }

            tank.DisplayNameShort = GodotResourceFile.Text(
                First(root, "display_name_short", "display_name"));
            if (string.IsNullOrEmpty(tank.DisplayNameShort))
            {
                tank.DisplayNameShort = tank.DisplayName;
            }

            tank.Health = GodotResourceFile.Integer(root.Raw("health"), 0);
            tank.Level = GodotResourceFile.Integer(First(root, "lvl", "level"), 1);
            tank.Class = (TankClass)GodotResourceFile.Integer(
                First(root, "tank_class", "class"), 0);
            tank.Nation = (Nation)GodotResourceFile.Integer(root.Raw("nation"), 0);
            tank.SpotRadius = GodotResourceFile.Number(root.Raw("spot_radius"), 0f);
            tank.TurretHealth = GodotResourceFile.Number(root.Raw("turret_health"), 0f);
            tank.TurretRotationSpeed = TankInfo.TraverseRadians(
                GodotResourceFile.Number(
                    First(root, "turret_rot_speed", "rot_speed"), 0f));
            tank.RepairCost = GodotResourceFile.Number(root.Raw("repair_cost"), 0f);
            tank.FarmCoefficient = GodotResourceFile.Number(
                First(root, "farm_coeff", "farm_coefficient"), 1f);
            tank.CrewXpFactor = GodotResourceFile.Number(root.Raw("crew_xp_factor"), 1f);
            tank.Premium = GodotResourceFile.Flag(First(root, "prem", "premium"), false);
            tank.ShopTank = GodotResourceFile.Flag(root.Raw("shop_tank"), false);
            tank.Cost = GodotResourceFile.Integer(root.Raw("cost"), 0);
            tank.CostXp = GodotResourceFile.Integer(root.Raw("cost_xp"), 0);
            tank.CostGold = GodotResourceFile.Integer(root.Raw("cost_gold"), 0);
            tank.HullWeight = GodotResourceFile.Number(
                First(root, "hull_weight", "weight"), 0f);
            tank.EngineSound = GodotResourceFile.Integer(root.Raw("engine_sound"), 0);
            tank.TrackSound = GodotResourceFile.Integer(root.Raw("track_sound"), 0);
            tank.ScaleFactorMod = GodotResourceFile.Number(root.Raw("scale_factor_mod"), 1f);
            tank.MatchmakerChance = GodotResourceFile.Number(
                root.Raw("matchmaker_chance"), 0.5f);
            tank.MatchmakerLoyal = GodotResourceFile.Flag(root.Raw("matchmaker_loyal"), false);
            tank.MatchmakerSuperLoyal = GodotResourceFile.Flag(
                root.Raw("matchmaker_super_loyal"), false);
            tank.IgnoreMatchmaker = GodotResourceFile.Flag(
                root.Raw("ignore_matchmaker"), false);

            float forward, backward;
            if (GodotResourceFile.TryVector(root.Raw("speed_limits"), out forward, out backward))
            {
                tank.SpeedForward = forward;
                tank.SpeedBackward = backward;
            }

            // A mod is not part of the research tree: it would have to rewrite
            // the stock branches to get there, and Godot shipped these cards as
            // standalone vehicles too.
            tank.TreeTank = false;
            tank.Hidden = false;

            ReadModules(file, root, tank);
            if (tank.Health <= 0)
            {
                tank.Health = 1;
            }

            return tank;
        }

        private static void ReadModules(GodotResourceFile file, GodotSection root, TankInfo tank)
        {
            var sections = file.Sections;
            for (var i = 0; i < sections.Count; i++)
            {
                var section = sections[i];
                if (section.Kind != "sub_resource" && section.Kind != "resource")
                {
                    continue;
                }

                switch (file.ScriptOf(section))
                {
                    case "module_engine.gd":
                        tank.Engines.Add(Engine(section));
                        break;
                    case "module_tracks.gd":
                        tank.Tracks.Add(Tracks(section));
                        break;
                    case "module_gun.gd":
                        tank.Guns.Add(Gun(file, section));
                        break;
                }
            }

            // The card may also point straight at its modules, which is how a
            // mod that reuses a stock resource ships them.
            AddReferenced(file, root, tank);
        }

        private static void AddReferenced(
            GodotResourceFile file, GodotSection root, TankInfo tank)
        {
            if (tank.Guns.Count == 0)
            {
                var gun = file.Follow(root, "gun");
                if (gun != null)
                {
                    tank.Guns.Add(Gun(file, gun));
                }
            }

            if (tank.Engines.Count == 0)
            {
                var engine = file.Follow(root, "engine");
                if (engine != null)
                {
                    tank.Engines.Add(Engine(engine));
                }
            }

            if (tank.Tracks.Count == 0)
            {
                var tracks = file.Follow(root, "tracks");
                if (tracks != null)
                {
                    tank.Tracks.Add(Tracks(tracks));
                }
            }
        }

        private static EngineInfo Engine(GodotSection section)
        {
            var engine = new EngineInfo();
            Module(section, engine);
            engine.Power = GodotResourceFile.Number(section.Raw("power"), 0f);
            engine.FireStartingChance = GodotResourceFile.Number(
                section.Raw("fireStartingChance"), 0f);
            return engine;
        }

        private static TracksInfo Tracks(GodotSection section)
        {
            var tracks = new TracksInfo();
            Module(section, tracks);
            tracks.RotationSpeed = GodotResourceFile.Number(
                First(section, "rot_speed", "rotation_speed"), 0f);
            tracks.DispersionMoveMultiplier = GodotResourceFile.Number(
                First(section, "disp_move", "dispersion_move"),
                tracks.DispersionMoveMultiplier);
            return tracks;
        }

        private static GunInfo Gun(GodotResourceFile file, GodotSection section)
        {
            var gun = new GunInfo();
            Module(section, gun);
            gun.Reload = GodotResourceFile.Number(section.Raw("reload"), 0f);
            gun.Dispersion = GodotResourceFile.Number(section.Raw("disp"), 0f);
            gun.AimTime = GodotResourceFile.Number(section.Raw("aim_time"), gun.AimTime);
            gun.Caliber = GodotResourceFile.Integer(section.Raw("caliber"), 0);
            gun.ShotSound = GodotResourceFile.Integer(section.Raw("shot_sound"), 0);
            gun.RotationSpeed = GodotResourceFile.Number(section.Raw("rot_speed"), 0f);
            gun.MaxAmmo = GodotResourceFile.Integer(section.Raw("max_ammo"), 0);
            gun.FireMode = GodotResourceFile.Integer(section.Raw("fire_mode"), 0);

            float up, down;
            if (GodotResourceFile.TryVector(section.Raw("deviation_up"), out up, out down))
            {
                // Godot packs the elevation pair as (up, down).
                gun.DeviationUp = up;
                gun.DeviationDown = down;
            }

            float left, right;
            if (GodotResourceFile.TryVector(section.Raw("deviation_r"), out left, out right))
            {
                gun.DeviationLeft = left;
                gun.DeviationRight = right;
            }

            var shells = file.FollowArray(section, "shells");
            for (var i = 0; i < shells.Count; i++)
            {
                // A mod without armour-piercing rounds still lists the slot
                // with 0 damage and 0 penetration; such a shell is no shell.
                var shell = Shell(shells[i]);
                if (shell.Damage <= 0f && shell.Penetration <= 0f) { continue; }

                gun.Shells.Add(shell);
            }

            var cycle = file.Follow(section, "cycle_info");
            if (cycle != null)
            {
                // A cycling gun reloads the whole clip, then fires it shot by
                // shot; the port models that as the intra-clip delay.
                gun.Reload = GodotResourceFile.Number(cycle.Raw("reload"), gun.Reload);
                gun.Burst.IntraClip = GodotResourceFile.Number(
                    cycle.Raw("intrashot"), gun.Burst.IntraClip);
                ReadMagazine(cycle, gun);
            }

            // A card may also carry the magazine on the gun itself, either as
            // a burst_info sub-resource (the shape the stock table uses) or
            // as plain fields. Reads only fill what is still unset, so the
            // cycle_info above wins over both, and burst_info over plain
            // fields.
            var burst = file.Follow(section, "burst_info");
            if (burst != null)
            {
                ReadMagazine(burst, gun);
            }

            ReadMagazine(section, gun);

            // The shorthand for a classic drum: N shells, one at a time,
            // intra-clip seconds apart, then the long reload. It expands to
            // N one-shell bursts, which is how the magazine clock models an
            // autoloader drum.
            var clipSize = GodotResourceFile.Integer(
                First(section, "clip_size", "magazine_size"), 0);
            if (clipSize <= 0 && cycle != null)
            {
                clipSize = GodotResourceFile.Integer(
                    First(cycle, "clip_size", "magazine_size"), 0);
            }

            if (clipSize > 0 && gun.Burst.Bursts.Count == 0)
            {
                for (var i = 0; i < clipSize; i++)
                {
                    gun.Burst.Bursts.Add(1);
                }
            }

            return gun;
        }

        /// <summary>
        /// Reads one magazine description (bursts, the two cadences and the
        /// per-shell autoload timers) without overwriting values an earlier
        /// description already set.
        /// </summary>
        private static void ReadMagazine(GodotSection section, GunInfo gun)
        {
            if (section == null || gun == null)
            {
                return;
            }

            if (gun.Burst.Bursts.Count == 0)
            {
                ReadInts(section.Raw("bursts"), gun.Burst.Bursts);
            }

            if (gun.Burst.IntraClip <= 0f)
            {
                gun.Burst.IntraClip = GodotResourceFile.Number(
                    First(section, "intraclip", "intra_clip"), gun.Burst.IntraClip);
            }

            if (gun.Burst.IntraBurst <= 0f)
            {
                gun.Burst.IntraBurst = GodotResourceFile.Number(
                    First(section, "intraburst", "intra_burst"), gun.Burst.IntraBurst);
            }

            if (gun.AutoReloadTimes.Count == 0)
            {
                var times = new List<float>();
                ReadFloats(section.Raw("autoreload_times"), times);
                for (var i = 0; i < times.Count; i++)
                {
                    if (times[i] > 0f)
                    {
                        gun.AutoReloadTimes.Add(times[i]);
                    }
                }
            }
        }

        /// <summary>Parses a Godot int array literal such as "[4, 4, 4]".</summary>
        private static void ReadInts(string raw, List<int> target)
        {
            if (string.IsNullOrEmpty(raw) || target == null)
            {
                return;
            }

            var parts = raw.Trim().Trim('[', ']').Split(',');
            for (var i = 0; i < parts.Length; i++)
            {
                var value = GodotResourceFile.Integer(parts[i], 0);
                if (value > 0)
                {
                    target.Add(value);
                }
            }
        }

        /// <summary>Parses a Godot float array literal such as "[3.0, 2.5]".</summary>
        private static void ReadFloats(string raw, List<float> target)
        {
            if (string.IsNullOrEmpty(raw) || target == null)
            {
                return;
            }

            var parts = raw.Trim().Trim('[', ']').Split(',');
            for (var i = 0; i < parts.Length; i++)
            {
                var value = GodotResourceFile.Number(parts[i], 0f);
                if (value > 0f)
                {
                    target.Add(value);
                }
            }
        }

        private static ShellInfo Shell(GodotSection section)
        {
            return new ShellInfo
            {
                Name = GodotResourceFile.Text(section.Raw("name")),
                Damage = GodotResourceFile.Number(section.Raw("dmg"), 0f),
                Penetration = GodotResourceFile.Number(section.Raw("pen"), 0f),
                Caliber = GodotResourceFile.Integer(section.Raw("caliber"), 0),
                Speed = GodotResourceFile.Number(section.Raw("speed"), 0f),
                Type = (ShellType)GodotResourceFile.Integer(section.Raw("type"), 0),
                Cost = GodotResourceFile.Integer(section.Raw("cost"), 0),
                Premium = GodotResourceFile.Flag(section.Raw("premium"), false),
            };
        }

        private static void Module(GodotSection section, ModuleInfo module)
        {
            module.Name = GodotResourceFile.Text(section.Raw("name"));
            module.DisplayName = GodotResourceFile.Text(
                First(section, "display_name", "name"));
            module.Level = GodotResourceFile.Integer(
                First(section, "level", "lvl"), 1);
            module.Health = GodotResourceFile.Number(section.Raw("health"), 0f);
            module.Weight = GodotResourceFile.Number(section.Raw("weight"), 0f);
        }

        private static string Name(GodotSection root, string fallback)
        {
            var name = GodotResourceFile.Text(root.Raw("tank_name"));
            if (string.IsNullOrEmpty(name))
            {
                name = fallback;
            }

            return (name ?? string.Empty).ToLowerInvariant();
        }

        private static string First(GodotSection section, params string[] keys)
        {
            for (var i = 0; i < keys.Length; i++)
            {
                if (section.Has(keys[i]))
                {
                    return section.Raw(keys[i]);
                }
            }

            return string.Empty;
        }
    }
}
