using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Core.Tanks
{
    /// <summary>
    /// Port of Godot <c>TankInfoMain</c> (assets/data/tank_info/tank_info.gd).
    /// Values come from the exported card table, never from C# literals.
    /// </summary>
    public sealed class TankInfo
    {
        public string Name = string.Empty;
        public string DisplayName = string.Empty;
        public string DisplayNameShort = string.Empty;

        public int Health;
        public int Level = 1;
        public float FarmCoefficient = 1f;
        public TankClass Class = TankClass.Light;
        public Nation Nation = Nation.None;
        public float SpotRadius;
        public float SpeedForward;
        public float SpeedBackward;
        public float TurretRotationSpeed;
        public float TurretHealth;

        /// <summary>
        /// A Blitz hull and the turret bolted on it each carry their own pool,
        /// and the number the game fights with is the sum: the 4.8.0 card table
        /// ships a KV-4 as 1176 hull plus a 374 turret, i.e. 1550, and a KV-3 as
        /// 1120 plus 330, i.e. 1450. The port read the hull alone, which is why
        /// every tank in the build was short exactly one turret.
        /// </summary>
        public int TotalHealth { get { return TotalHealthWith(null); } }

        /// <summary>
        /// The same sum for the turret actually mounted. <paramref
        /// name="turret"/> null falls back to <see cref="TurretHealth"/>, the
        /// top turret the card table carries at the vehicle level.
        /// </summary>
        public int TotalHealthWith(TurretInfo turret)
        {
            var extra = turret != null && turret.Health > 0f
                ? turret.Health
                : TurretHealth;
            return Health + (int)Math.Round(extra);
        }

        /// <summary>
        /// tank_info.gd <c>cleanup()</c>: a traverse rate above one is the
        /// degrees per second the card table carries, and the source stores it
        /// as radians per second because <c>rotate_turret</c> multiplies it by
        /// delta without converting. Anything at or below one is already
        /// radians, exactly as the resource files ship it.
        /// </summary>
        public static float TraverseRadians(float value)
        {
            return value > 1f ? value * (float)(Math.PI / 180.0) : value;
        }
        public float RepairCost;
        public float CrewXpFactor = 1f;
        public bool Premium;
        public bool ShopTank;

        /// <summary>Godot tank_info.gd default; Cleanup() applies the nation bias.</summary>
        public float MatchmakerChance = 0.5f;

        /// <summary>Never rolled by the matchmaker (shop and event cards).</summary>
        public bool IgnoreMatchmaker;

        /// <summary>Gets a downtiered template and one reroll when it lands below the player.</summary>
        public bool MatchmakerSuperLoyal;

        /// <summary>Always gets a loyal template.</summary>
        public bool MatchmakerLoyal;

        /// <summary>Hidden from the tree, the shop and the matchmaker.</summary>
        public bool Hidden;
        public bool TreeTank;
        public int TreeBranch;
        public int CostXp;
        public int Cost;
        public float HullWeight;
        public int EngineSound;
        public int TrackSound;
        public float ScaleFactorMod = 1f;
        public bool Collector;
        public int CollectorSellGold;

        /// <summary>
        /// Installed from a mod. Mods are their own class, drawn like a
        /// collector vehicle but in light green; the flag is the nation the
        /// card declares, or the mixed (<see cref="Nation.Other"/>) flag
        /// when it declares none.
        /// </summary>
        public bool Mod;

        /// <summary>
        /// Gold price of the 4.8.0 client card. Tree premiums carry the shop
        /// price the yellow card shows; event and collector vehicles keep the
        /// client value even though nothing offers them for sale yet.
        /// </summary>
        public int CostGold;

        /// <summary>
        /// How the client hands the vehicle out: empty for researchable cards,
        /// otherwise <c>tree_premium</c>, <c>premium</c> or <c>collectible</c>.
        /// </summary>
        public string Acquisition = string.Empty;

        public string IconPath = string.Empty;
        public string ModelPath = string.Empty;
        public string TextureMain = string.Empty;
        public string TextureTracks = string.Empty;

        /// <summary>Godot <c>unlock_tanks</c>: next tank name -> research cost in XP.</summary>
        /// <summary>
        /// <c>hull/ammoBayHealth</c>. The ammo rack is not researchable, so it
        /// is the one damageable module the hull owns outright.
        /// </summary>
        public ModuleHealthInfo AmmoBay;

        /// <summary><c>fuelTanks</c>: one shared record per hull.</summary>
        public ModuleHealthInfo FuelTank;

        /// <summary>
        /// <c>&lt;crew&gt;</c> flattened to the roles the vehicle covers, which
        /// is what the damage panel lights a slot per.
        /// </summary>
        public readonly List<string> Crew = new List<string>();

        public readonly Dictionary<string, int> UnlockTanks =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Sidecar links extend the tree but do not change existing module
        /// research costs, compatibility or legacy elite completion.
        /// </summary>
        public readonly Dictionary<string, AdditionalTankResearchLink> AdditionalResearchLinks =
            new Dictionary<string, AdditionalTankResearchLink>(StringComparer.OrdinalIgnoreCase);

        public readonly List<GunInfo> Guns = new List<GunInfo>();
        public readonly List<EngineInfo> Engines = new List<EngineInfo>();
        public readonly List<TracksInfo> Tracks = new List<TracksInfo>();
        public readonly List<RadioInfo> Radios = new List<RadioInfo>();

        /// <summary>
        /// Researchable turrets of the Blitz client, stock first. A card ported
        /// before the branch rebuild carries none, and then the whole port
        /// keeps using the hull copies of the turret values.
        /// </summary>
        public readonly List<TurretInfo> Turrets = new List<TurretInfo>();

        /// <summary>The turret at <paramref name="id"/>, clamped, or null.</summary>
        public TurretInfo Turret(int id)
        {
            if (Turrets.Count == 0)
            {
                return null;
            }

            var index = id < 0 ? 0 : (id >= Turrets.Count ? Turrets.Count - 1 : id);
            return Turrets[index];
        }

        /// <summary>
        /// Godot installed a gun by index into <see cref="Guns"/>. The client
        /// authors the gun list per turret, so an index that the installed
        /// turret cannot mount has to fall back to the turret's own top gun -
        /// the same thing Blitz does when a turret is swapped under a gun it
        /// does not carry.
        /// </summary>
        public int GunIdFor(int turretId, int gunId)
        {
            var turret = Turret(turretId);
            if (turret == null || turret.Guns.Count == 0)
            {
                return gunId;
            }

            if (gunId >= 0 && gunId < Guns.Count && turret.CanMount(Guns[gunId].Name))
            {
                return gunId;
            }

            var fallback = turret.Guns[turret.Guns.Count - 1].Name;
            for (var i = 0; i < Guns.Count; i++)
            {
                if (string.Equals(Guns[i].Name, fallback, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return gunId;
        }

        /// <summary>
        /// The gun as the installed turret mounts it: the client authors
        /// <c>reloadTime</c> and <c>maxAmmo</c> per turret, so the stock turret
        /// really does reload slower with the same barrel.
        /// </summary>
        public GunInfo GunFor(int turretId, int gunId)
        {
            if (Guns.Count == 0)
            {
                return null;
            }

            var index = GunIdFor(turretId, gunId);
            index = index < 0 ? 0 : (index >= Guns.Count ? Guns.Count - 1 : index);
            var gun = Guns[index];
            var turret = Turret(turretId);
            var slot = turret == null ? null : turret.Slot(gun.Name);
            if (slot == null)
            {
                return gun;
            }

            return gun.WithTurretSlot(slot);
        }

        public static TankInfo FromJson(JsonNode node)
        {
            var tank = new TankInfo();
            if (node == null || node.Kind != JsonKind.Object)
            {
                return tank;
            }

            tank.Name = node["name"].AsString();
            tank.DisplayName = node["display_name"].AsString();
            tank.DisplayNameShort = node["display_name_short"].AsString(tank.DisplayName);
            tank.Health = node["health"].AsInt();
            tank.Level = node["lvl"].AsInt(1);
            tank.FarmCoefficient = node["farm_coeff"].AsFloat(tank.FarmCoefficient);
            tank.Class = (TankClass)node["tank_class"].AsInt();
            tank.Nation = (Nation)node["nation"].AsInt();
            tank.SpotRadius = node["spot_radius"].AsFloat();
            tank.SpeedForward = node["speed_limits"]["x"].AsFloat();
            tank.SpeedBackward = node["speed_limits"]["y"].AsFloat();
            tank.TurretRotationSpeed =
                TraverseRadians(node["turret_rot_speed"].AsFloat());
            tank.TurretHealth = node["turret_health"].AsFloat();
            tank.RepairCost = node["repair_cost"].AsFloat();
            tank.CrewXpFactor = node["crew_xp_factor"].AsFloat(tank.CrewXpFactor);
            tank.Premium = node["prem"].AsBool();
            tank.ShopTank = node["shop_tank"].AsBool();
            tank.MatchmakerChance = node["matchmaker_chance"].AsFloat(tank.MatchmakerChance);
            tank.IgnoreMatchmaker = node["ignore_matchmaker"].AsBool();
            tank.MatchmakerSuperLoyal = node["matchmaker_super_loyal"].AsBool();
            tank.MatchmakerLoyal = node["matchmaker_loyal"].AsBool();
            tank.Hidden = node["hidden"].AsBool();
            tank.TreeTank = node["tree_tank"].AsBool();
            tank.TreeBranch = node["tree_branch"].AsInt();
            tank.CostXp = node["cost_xp"].AsInt();
            tank.Cost = node["cost"].AsInt();
            tank.HullWeight = node["hull_weight"].AsFloat();
            tank.EngineSound = node["engine_sound"].AsInt();
            tank.TrackSound = node["track_sound"].AsInt();
            tank.ScaleFactorMod = node["scale_factor_mod"].AsFloat(tank.ScaleFactorMod);
            tank.Collector = node["collector"].AsBool();
            tank.CollectorSellGold = node["collector_sell_gold"].AsInt();
            tank.CostGold = node["cost_gold"].AsInt();
            tank.Acquisition = node["acquisition"].AsString();
            tank.IconPath = node["icon"].AsString();
            tank.ModelPath = node["model_path"].AsString();
            tank.TextureMain = node["tex_main"].AsString();
            tank.TextureTracks = node["tex_tracks"].AsString();

            var unlocks = node["unlock_tanks"];
            if (unlocks.Kind == JsonKind.Object)
            {
                foreach (var entry in unlocks.Fields)
                {
                    // Godot cards contain both "T-34" and "t-34" spellings of the
                    // same unlock, so the dictionary is case insensitive and the
                    // cheapest cost of a duplicate wins.
                    var cost = entry.Value.AsInt();
                    int known;
                    if (tank.UnlockTanks.TryGetValue(entry.Key, out known) && known <= cost)
                    {
                        continue;
                    }

                    tank.UnlockTanks[entry.Key] = cost;
                }
            }

            ReadModules(node["turrets"], tank.Turrets, TurretInfo.FromJson);
            ReadModules(node["guns"], tank.Guns, GunInfo.FromJson);
            ReadModules(node["engines"], tank.Engines, EngineInfo.FromJson);
            ReadModules(node["tracks"], tank.Tracks, TracksInfo.FromJson);
            ReadModules(node["radios"], tank.Radios, RadioInfo.FromJson);
            tank.AmmoBay = ModuleHealthInfo.FromJson(node["ammo_bay"]);
            tank.FuelTank = ModuleHealthInfo.FromJson(node["fuel_tank"]);
            JsonMap.ReadStrings(node["crew"], tank.Crew);
            return tank;
        }

        /// <summary>
        /// Port of Godot <c>cleanup()</c>, run once when the card enters the
        /// database. Two source steps are deliberately absent: the unlock keys
        /// are lower cased by the case insensitive dictionary already, and the
        /// turret rotation stays in degrees because Godot only converts it to
        /// radians for its own radian based transforms.
        /// </summary>
        public void Cleanup()
        {
            Level = Level < 1 ? 1 : (Level > TanksDatabase.MaxLevel ? TanksDatabase.MaxLevel : Level);
            Name = (Name ?? string.Empty).ToLowerInvariant();
            MatchmakerChance = MatchmakerChance < 0f
                ? 0f
                : (MatchmakerChance > 1f ? 1f : MatchmakerChance);

            if (Nation == Nation.Ussr)
            {
                // The source biases the queue towards its largest tree after the
                // clamp, so a fully loyal card ends up above 1.0 there too.
                MatchmakerChance *= 1.25f;
            }
        }

        private static void ReadModules<T>(JsonNode node, List<T> target, Func<JsonNode, T> read)
        {
            if (node == null || node.Kind != JsonKind.Array)
            {
                return;
            }

            for (var i = 0; i < node.Count; i++)
            {
                target.Add(read(node[i]));
            }
        }
    }
}
