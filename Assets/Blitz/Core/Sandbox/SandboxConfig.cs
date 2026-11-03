using System.Collections.Generic;
using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Core.Sandbox
{
    /// <summary>
    /// TrainingModeConfig.BaseMode: the battle rules the Sandbox runs. BigBoss
    /// is not in the Godot build; it plays the 11.20 tables.
    /// </summary>
    public enum SandboxBaseMode { Standard = 0, Gravity = 1, Uprising = 2, BigBoss = 3 }

    /// <summary>TrainingModeConfig.BalancerMode: how random seats pick a tier.</summary>
    public enum SandboxBalancer { Random = 0, PlayerTier = 1, MirrorAllies = 2 }

    /// <summary>TrainingModeConfig.RosterMode.</summary>
    public enum SandboxRosterMode { Random = 0, Custom = 1 }

    /// <summary>TrainingModeConfig.PlayerTeamChoice: which spawn side the player takes.</summary>
    public enum SandboxTeamChoice { Random = 0, TeamA = 1, TeamB = 2 }

    /// <summary>TrainingModeConfig.AimbotPriority.</summary>
    public enum SandboxAimbotPriority { Crosshair = 0, Distance = 1, LowHealth = 2 }

    /// <summary>TrainingModeConfig.AimbotAimPoint.</summary>
    public enum SandboxAimPoint { Center = 0, Turret = 1, Hull = 2, Tracks = 3 }

    /// <summary>
    /// Port of assets/data/training_mode_config.gd: one persistent Sandbox
    /// configuration. Intentionally separate from settings and accounts: a
    /// Sandbox battle may read it, but never writes modules, statistics,
    /// currency or ammunition back into the player's garage.
    ///
    /// Field names, defaults, clamps and the export keys are the Godot ones, so
    /// a config code copied from the Godot build imports here unchanged.
    /// </summary>
    public sealed class SandboxConfig
    {
        public const int MaxSlots = 10;
        public const float MaxBattleMinutes = 60f;

        /// <summary>Empty map id means a random map.</summary>
        public string SelectedMapId = string.Empty;
        public SandboxBaseMode BaseMode = SandboxBaseMode.Standard;

        /// <summary>
        /// Legacy equal-team value. New saves use independent totals; zero means
        /// an old preset that still needs to inherit this value once.
        /// </summary>
        public int TeamSize = 7;
        public int AlliedTeamSize;
        public int EnemyTeamSize;
        public SandboxTeamChoice PlayerTeam = SandboxTeamChoice.Random;
        public SandboxBalancer Balancer = SandboxBalancer.PlayerTier;
        public SandboxRosterMode AlliedRosterMode = SandboxRosterMode.Random;
        public SandboxRosterMode EnemyRosterMode = SandboxRosterMode.Random;

        /// <summary>A zero battle time means no time limit.</summary>
        public float BattleTimeMinutes = 7f;
        public bool FriendlyFire;
        public float DamageDeviation = 0.25f;
        public float PenetrationDeviation = 0.05f;
        public float ProjectileGravityMul = 1f;

        public int UprisingLives = 3;
        public float UprisingElixirPerSecond = 1f;
        public int UprisingElixirPerDamagingShot = 2;
        public int UprisingElixirDamageStep = 100;
        public int UprisingElixirPerDamageStep = 5;
        public int UprisingElixirPerDeath = 100;
        public float UprisingUpgradePowerMul = 1f;
        public int UprisingCostLevel1 = 100;
        public int UprisingCostLevel2 = 200;
        public int UprisingCostLevel3 = 400;

        public float GravitySpeedMul = 1.45f;
        public float GravityShotPowerMul = 1f;
        public float GravityJumpPowerMul = 1f;
        public float GravityGravityMul = 0.3f;

        // Not in the Godot build: BIG BOSS knobs over the shipped 11.20 tables
        // (the defaults are that profile's boss lives and starting team points).
        public int BigBossLives = 3;
        public int BigBossTeamPoints = 30;
        /// <summary>Scales the passive boosts of the boss and the roles; 1 plays the shipped mode.</summary>
        public float BigBossBuffMul = 1f;

        // Player-only cheats. They never alter ordinary battles or garage data.
        public bool CheatInfiniteAmmo;
        public bool CheatNoRecoil;
        public bool CheatWallhack;
        public float WallhackCloseDistance = 50f;
        public float WallhackMaxDistance = 720f;

        public bool CheatAimbot;
        public SandboxAimbotPriority AimbotPriority = SandboxAimbotPriority.Crosshair;
        public SandboxAimPoint AimbotAimPoint = SandboxAimPoint.Center;
        public float AimbotFov = 45f;
        public float AimbotMaxDistance = 720f;
        public float AimbotLead = 1f;
        public float AimbotSmoothing = 10f;
        public bool AimbotThroughWalls;
        public bool AimbotRequireSpotted = true;
        public bool AimbotSticky = true;

        public bool CheatTriggerbot;
        public float TriggerbotDelay;
        public float TriggerbotMaxDistance = 720f;
        public float TriggerbotToleranceDeg = 1.5f;
        public bool TriggerbotRequireVisible = true;
        public bool TriggerbotRequireSpotted = true;
        public bool TriggerbotRequirePenetration;
        public bool TriggerbotRequireDirectHit = true;
        public bool TriggerbotUseAimbot = true;
        public bool TriggerbotFireWhileMoving = true;

        /// <summary>Global tank modifiers; slot and player rows stack with these.</summary>
        public readonly SandboxMultipliers Global = new SandboxMultipliers();

        /// <summary>The player's own row; modules still come from the garage tank.</summary>
        public readonly SandboxMultipliers Player = new SandboxMultipliers();

        /// <summary>Defaults applied to random seats. A custom seat overrides all three.</summary>
        public int DefaultBotSkill = SandboxBotSlot.RandomSelection;
        public bool DefaultBotsHarmless;
        public bool DefaultBotsStationary;

        public readonly List<SandboxBotSlot> AlliedSlots = new List<SandboxBotSlot>();
        public readonly List<SandboxBotSlot> EnemySlots = new List<SandboxBotSlot>();

        public SandboxConfig()
        {
            EnsureSlots();
        }

        public bool MapIsRandom
        {
            get { return string.IsNullOrEmpty(SelectedMapId); }
        }

        /// <summary>Battle length in seconds; zero is unlimited.</summary>
        public float BattleTimeSeconds
        {
            get { return BattleTimeMinutes <= 0.00001f ? 0f : BattleTimeMinutes * 60f; }
        }

        public List<SandboxBotSlot> Slots(bool enemy)
        {
            return enemy ? EnemySlots : AlliedSlots;
        }

        public bool RosterIsCustom(bool enemy)
        {
            return (enemy ? EnemyRosterMode : AlliedRosterMode) == SandboxRosterMode.Custom;
        }

        public int TeamTotal(bool enemy)
        {
            return enemy ? EnemyTeamSize : AlliedTeamSize;
        }

        public bool EnsureSlots()
        {
            var changed = Resize(AlliedSlots);
            return Resize(EnemySlots) || changed;
        }

        private static bool Resize(List<SandboxBotSlot> slots)
        {
            var changed = false;
            while (slots.Count < MaxSlots)
            {
                slots.Add(new SandboxBotSlot());
                changed = true;
            }

            if (slots.Count > MaxSlots)
            {
                slots.RemoveRange(MaxSlots, slots.Count - MaxSlots);
                changed = true;
            }

            for (var i = 0; i < slots.Count; i++)
            {
                if (slots[i] == null)
                {
                    slots[i] = new SandboxBotSlot();
                    changed = true;
                }
            }

            return changed;
        }

        /// <summary>
        /// Clamps every value to the Godot ranges, migrates the legacy team
        /// size and drops a map id that is not in <paramref name="mapIds"/>
        /// (an empty or null list keeps any id). Returns true when anything changed.
        /// </summary>
        public bool Validate(ICollection<string> mapIds = null)
        {
            var changed = EnsureSlots();
            var before = ToExport().ToJson(false);

            BaseMode = (SandboxBaseMode)Clamp((int)BaseMode, 0, 3);
            var legacyTeamSize = Clamp(TeamSize, 1, MaxSlots);
            if (AlliedTeamSize <= 0)
            {
                AlliedTeamSize = legacyTeamSize;
            }

            if (EnemyTeamSize <= 0)
            {
                EnemyTeamSize = legacyTeamSize;
            }

            AlliedTeamSize = Clamp(AlliedTeamSize, 1, MaxSlots);
            EnemyTeamSize = Clamp(EnemyTeamSize, 1, MaxSlots);
            TeamSize = AlliedTeamSize > EnemyTeamSize ? AlliedTeamSize : EnemyTeamSize;
            PlayerTeam = (SandboxTeamChoice)Clamp((int)PlayerTeam, 0, 2);
            Balancer = (SandboxBalancer)Clamp((int)Balancer, 0, 2);
            AlliedRosterMode = (SandboxRosterMode)Clamp((int)AlliedRosterMode, 0, 1);
            EnemyRosterMode = (SandboxRosterMode)Clamp((int)EnemyRosterMode, 0, 1);
            BattleTimeMinutes = Finite(BattleTimeMinutes, 0f, 0f, MaxBattleMinutes);
            DamageDeviation = Finite(DamageDeviation, 0.25f, 0f, 1f);
            PenetrationDeviation = Finite(PenetrationDeviation, 0.05f, 0f, 1f);
            ProjectileGravityMul = SandboxMultiplierMath.SanitizeMultiplier(ProjectileGravityMul);

            UprisingLives = Clamp(UprisingLives, 1, 10);
            UprisingElixirPerSecond = Finite(UprisingElixirPerSecond, 1f, 0f, 100f);
            UprisingElixirPerDamagingShot = Clamp(UprisingElixirPerDamagingShot, 0, 1000);
            UprisingElixirDamageStep = Clamp(UprisingElixirDamageStep, 1, 10000);
            UprisingElixirPerDamageStep = Clamp(UprisingElixirPerDamageStep, 0, 1000);
            UprisingElixirPerDeath = Clamp(UprisingElixirPerDeath, 0, 10000);
            UprisingUpgradePowerMul = Finite(UprisingUpgradePowerMul, 1f, 0f, 10f);
            UprisingCostLevel1 = Clamp(UprisingCostLevel1, 0, 5000);
            UprisingCostLevel2 = Clamp(UprisingCostLevel2, 0, 5000);
            UprisingCostLevel3 = Clamp(UprisingCostLevel3, 0, 5000);
            GravitySpeedMul = Finite(GravitySpeedMul, 1.45f, 0f, 10f);
            GravityShotPowerMul = Finite(GravityShotPowerMul, 1f, 0f, 10f);
            GravityJumpPowerMul = Finite(GravityJumpPowerMul, 1f, 0f, 10f);
            GravityGravityMul = Finite(GravityGravityMul, 0.3f, 0f, 5f);
            BigBossLives = Clamp(BigBossLives, 1, 10);
            BigBossTeamPoints = Clamp(BigBossTeamPoints, 1, 200);
            BigBossBuffMul = Finite(BigBossBuffMul, 1f, 0f, 10f);
            WallhackCloseDistance = Finite(WallhackCloseDistance, 50f, 0f, 720f);
            WallhackMaxDistance = Finite(WallhackMaxDistance, 720f, WallhackCloseDistance, 720f);
            AimbotPriority = (SandboxAimbotPriority)Clamp((int)AimbotPriority, 0, 2);
            AimbotAimPoint = (SandboxAimPoint)Clamp((int)AimbotAimPoint, 0, 3);
            AimbotFov = Finite(AimbotFov, 45f, 1f, 180f);
            AimbotMaxDistance = Finite(AimbotMaxDistance, 720f, 10f, 720f);
            AimbotLead = Finite(AimbotLead, 1f, 0f, 3f);
            AimbotSmoothing = Finite(AimbotSmoothing, 10f, 0f, 30f);
            TriggerbotDelay = Finite(TriggerbotDelay, 0f, 0f, 5f);
            TriggerbotMaxDistance = Finite(TriggerbotMaxDistance, 720f, 10f, 720f);
            TriggerbotToleranceDeg = Finite(TriggerbotToleranceDeg, 1.5f, 0.1f, 15f);

            Global.Sanitize();
            Player.Sanitize();
            DefaultBotSkill = Clamp(DefaultBotSkill, SandboxBotSlot.RandomSelection, SandboxBotSlot.MaxSkill);

            if (!string.IsNullOrEmpty(SelectedMapId) && mapIds != null && mapIds.Count > 0
                && !mapIds.Contains(SelectedMapId))
            {
                SelectedMapId = string.Empty;
            }

            for (var i = 0; i < AlliedSlots.Count; i++)
            {
                changed = AlliedSlots[i].ValidateNumeric() || changed;
            }

            for (var i = 0; i < EnemySlots.Count; i++)
            {
                changed = EnemySlots[i].ValidateNumeric() || changed;
            }

            return changed || before != ToExport().ToJson(false);
        }

        private static int Clamp(int value, int min, int max)
        {
            return SandboxMultiplierMath.Clamp(value, min, max);
        }

        private static float Finite(float value, float fallback, float min, float max)
        {
            return SandboxMultiplierMath.IsFinite(value)
                ? SandboxMultiplierMath.Clamp(value, min, max)
                : fallback;
        }

        public SandboxConfig Copy()
        {
            return CopyInto(new SandboxConfig());
        }

        /// <summary>A deep copy; the slots of the copy are independent instances.</summary>
        public SandboxConfig CopyInto(SandboxConfig copy)
        {
            if (copy == null)
            {
                throw new System.ArgumentNullException("copy");
            }

            copy.ReadFields(ToExport());
            copy.AlliedSlots.Clear();
            copy.EnemySlots.Clear();
            for (var i = 0; i < AlliedSlots.Count; i++)
            {
                copy.AlliedSlots.Add(AlliedSlots[i] == null ? new SandboxBotSlot() : AlliedSlots[i].Copy());
            }

            for (var i = 0; i < EnemySlots.Count; i++)
            {
                copy.EnemySlots.Add(EnemySlots[i] == null ? new SandboxBotSlot() : EnemySlots[i].Copy());
            }

            copy.EnsureSlots();
            return copy;
        }

        /// <summary>The Godot <c>to_export_dictionary</c> shape, key for key.</summary>
        public JsonNode ToExport()
        {
            var node = JsonNode.NewObject()
                .Set("selected_map_id", SelectedMapId ?? string.Empty)
                .Set("base_mode", (int)BaseMode)
                .Set("team_size", TeamSize)
                .Set("allied_team_size", AlliedTeamSize)
                .Set("enemy_team_size", EnemyTeamSize)
                .Set("player_team", (int)PlayerTeam)
                .Set("balancer_mode", (int)Balancer)
                .Set("allied_roster_mode", (int)AlliedRosterMode)
                .Set("enemy_roster_mode", (int)EnemyRosterMode)
                .Set("battle_time_minutes", (double)BattleTimeMinutes)
                .Set("friendly_fire", FriendlyFire)
                .Set("damage_deviation", (double)DamageDeviation)
                .Set("penetration_deviation", (double)PenetrationDeviation)
                .Set("projectile_gravity_mul", (double)ProjectileGravityMul)
                .Set("uprising_lives", UprisingLives)
                .Set("uprising_elixir_per_second", (double)UprisingElixirPerSecond)
                .Set("uprising_elixir_per_damaging_shot", UprisingElixirPerDamagingShot)
                .Set("uprising_elixir_damage_step", UprisingElixirDamageStep)
                .Set("uprising_elixir_per_damage_step", UprisingElixirPerDamageStep)
                .Set("uprising_elixir_per_death", UprisingElixirPerDeath)
                .Set("uprising_upgrade_power_mul", (double)UprisingUpgradePowerMul)
                .Set("uprising_cost_level_1", UprisingCostLevel1)
                .Set("uprising_cost_level_2", UprisingCostLevel2)
                .Set("uprising_cost_level_3", UprisingCostLevel3)
                .Set("gravity_mode_speed_mul", (double)GravitySpeedMul)
                .Set("gravity_mode_shot_power_mul", (double)GravityShotPowerMul)
                .Set("gravity_mode_jump_power_mul", (double)GravityJumpPowerMul)
                .Set("gravity_mode_gravity_mul", (double)GravityGravityMul)
                .Set("big_boss_lives", BigBossLives)
                .Set("big_boss_team_points", BigBossTeamPoints)
                .Set("big_boss_buff_mul", (double)BigBossBuffMul)
                .Set("cheat_infinite_ammo", CheatInfiniteAmmo)
                .Set("cheat_no_recoil", CheatNoRecoil)
                .Set("cheat_wallhack", CheatWallhack)
                .Set("wallhack_close_distance", (double)WallhackCloseDistance)
                .Set("wallhack_max_distance", (double)WallhackMaxDistance)
                .Set("cheat_aimbot", CheatAimbot)
                .Set("aimbot_priority", (int)AimbotPriority)
                .Set("aimbot_aim_point", (int)AimbotAimPoint)
                .Set("aimbot_fov", (double)AimbotFov)
                .Set("aimbot_max_distance", (double)AimbotMaxDistance)
                .Set("aimbot_lead", (double)AimbotLead)
                .Set("aimbot_smoothing", (double)AimbotSmoothing)
                .Set("aimbot_through_walls", AimbotThroughWalls)
                .Set("aimbot_require_spotted", AimbotRequireSpotted)
                .Set("aimbot_sticky", AimbotSticky)
                .Set("cheat_triggerbot", CheatTriggerbot)
                .Set("triggerbot_delay", (double)TriggerbotDelay)
                .Set("triggerbot_max_distance", (double)TriggerbotMaxDistance)
                .Set("triggerbot_tolerance_deg", (double)TriggerbotToleranceDeg)
                .Set("triggerbot_require_visible", TriggerbotRequireVisible)
                .Set("triggerbot_require_spotted", TriggerbotRequireSpotted)
                .Set("triggerbot_require_penetration", TriggerbotRequirePenetration)
                .Set("triggerbot_require_direct_hit", TriggerbotRequireDirectHit)
                .Set("triggerbot_use_aimbot", TriggerbotUseAimbot)
                .Set("triggerbot_fire_while_moving", TriggerbotFireWhileMoving);
            Global.Write(node, string.Empty);
            Player.Write(node, "player_");
            node.Set("default_bot_skill", DefaultBotSkill)
                .Set("default_bots_harmless", DefaultBotsHarmless)
                .Set("default_bots_stationary", DefaultBotsStationary);
            var allies = JsonNode.NewArray();
            for (var i = 0; i < AlliedSlots.Count; i++)
            {
                allies.Add(AlliedSlots[i].ToExport());
            }

            var enemies = JsonNode.NewArray();
            for (var i = 0; i < EnemySlots.Count; i++)
            {
                enemies.Add(EnemySlots[i].ToExport());
            }

            node.Set("allied_slots", allies);
            node.Set("enemy_slots", enemies);
            return node;
        }

        /// <summary>Godot <c>from_export_dictionary</c>: missing keys keep the defaults.</summary>
        public static SandboxConfig FromExport(JsonNode data)
        {
            var config = new SandboxConfig();
            if (data == null || data.Kind != JsonKind.Object)
            {
                config.Validate();
                return config;
            }

            config.ReadFields(data);
            ReadSlots(data["allied_slots"], config.AlliedSlots);
            ReadSlots(data["enemy_slots"], config.EnemySlots);
            config.Validate();
            return config;
        }

        private static void ReadSlots(JsonNode array, List<SandboxBotSlot> slots)
        {
            if (array.Kind != JsonKind.Array)
            {
                return;
            }

            slots.Clear();
            var items = array.Items;
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i].Kind == JsonKind.Object)
                {
                    slots.Add(SandboxBotSlot.FromExport(items[i]));
                }
            }
        }

        private void ReadFields(JsonNode data)
        {
            SelectedMapId = SandboxJson.Text(data, "selected_map_id", string.Empty);
            BaseMode = (SandboxBaseMode)SandboxJson.Int(data, "base_mode", 0);
            TeamSize = SandboxJson.Int(data, "team_size", 7);
            AlliedTeamSize = SandboxJson.Int(data, "allied_team_size", 0);
            EnemyTeamSize = SandboxJson.Int(data, "enemy_team_size", 0);
            PlayerTeam = (SandboxTeamChoice)SandboxJson.Int(data, "player_team", 0);
            Balancer = (SandboxBalancer)SandboxJson.Int(data, "balancer_mode", 1);
            AlliedRosterMode = (SandboxRosterMode)SandboxJson.Int(data, "allied_roster_mode", 0);
            EnemyRosterMode = (SandboxRosterMode)SandboxJson.Int(data, "enemy_roster_mode", 0);
            BattleTimeMinutes = SandboxJson.Float(data, "battle_time_minutes", 7f);
            FriendlyFire = SandboxJson.Bool(data, "friendly_fire", false);
            DamageDeviation = SandboxJson.Float(data, "damage_deviation", 0.25f);
            PenetrationDeviation = SandboxJson.Float(data, "penetration_deviation", 0.05f);
            ProjectileGravityMul = SandboxJson.Float(data, "projectile_gravity_mul", 1f);
            UprisingLives = SandboxJson.Int(data, "uprising_lives", 3);
            UprisingElixirPerSecond = SandboxJson.Float(data, "uprising_elixir_per_second", 1f);
            UprisingElixirPerDamagingShot = SandboxJson.Int(data, "uprising_elixir_per_damaging_shot", 2);
            UprisingElixirDamageStep = SandboxJson.Int(data, "uprising_elixir_damage_step", 100);
            UprisingElixirPerDamageStep = SandboxJson.Int(data, "uprising_elixir_per_damage_step", 5);
            UprisingElixirPerDeath = SandboxJson.Int(data, "uprising_elixir_per_death", 100);
            UprisingUpgradePowerMul = SandboxJson.Float(data, "uprising_upgrade_power_mul", 1f);
            UprisingCostLevel1 = SandboxJson.Int(data, "uprising_cost_level_1", 100);
            UprisingCostLevel2 = SandboxJson.Int(data, "uprising_cost_level_2", 200);
            UprisingCostLevel3 = SandboxJson.Int(data, "uprising_cost_level_3", 400);
            GravitySpeedMul = SandboxJson.Float(data, "gravity_mode_speed_mul", 1.45f);
            GravityShotPowerMul = SandboxJson.Float(data, "gravity_mode_shot_power_mul", 1f);
            GravityJumpPowerMul = SandboxJson.Float(data, "gravity_mode_jump_power_mul", 1f);
            GravityGravityMul = SandboxJson.Float(data, "gravity_mode_gravity_mul", 0.3f);
            BigBossLives = SandboxJson.Int(data, "big_boss_lives", 3);
            BigBossTeamPoints = SandboxJson.Int(data, "big_boss_team_points", 30);
            BigBossBuffMul = SandboxJson.Float(data, "big_boss_buff_mul", 1f);
            CheatInfiniteAmmo = SandboxJson.Bool(data, "cheat_infinite_ammo", false);
            CheatNoRecoil = SandboxJson.Bool(data, "cheat_no_recoil", false);
            CheatWallhack = SandboxJson.Bool(data, "cheat_wallhack", false);
            WallhackCloseDistance = SandboxJson.Float(data, "wallhack_close_distance", 50f);
            WallhackMaxDistance = SandboxJson.Float(data, "wallhack_max_distance", 720f);
            CheatAimbot = SandboxJson.Bool(data, "cheat_aimbot", false);
            AimbotPriority = (SandboxAimbotPriority)SandboxJson.Int(data, "aimbot_priority", 0);
            AimbotAimPoint = (SandboxAimPoint)SandboxJson.Int(data, "aimbot_aim_point", 0);
            AimbotFov = SandboxJson.Float(data, "aimbot_fov", 45f);
            AimbotMaxDistance = SandboxJson.Float(data, "aimbot_max_distance", 720f);
            AimbotLead = SandboxJson.Float(data, "aimbot_lead", 1f);
            AimbotSmoothing = SandboxJson.Float(data, "aimbot_smoothing", 10f);
            AimbotThroughWalls = SandboxJson.Bool(data, "aimbot_through_walls", false);
            AimbotRequireSpotted = SandboxJson.Bool(data, "aimbot_require_spotted", true);
            AimbotSticky = SandboxJson.Bool(data, "aimbot_sticky", true);
            CheatTriggerbot = SandboxJson.Bool(data, "cheat_triggerbot", false);
            TriggerbotDelay = SandboxJson.Float(data, "triggerbot_delay", 0f);
            TriggerbotMaxDistance = SandboxJson.Float(data, "triggerbot_max_distance", 720f);
            TriggerbotToleranceDeg = SandboxJson.Float(data, "triggerbot_tolerance_deg", 1.5f);
            TriggerbotRequireVisible = SandboxJson.Bool(data, "triggerbot_require_visible", true);
            TriggerbotRequireSpotted = SandboxJson.Bool(data, "triggerbot_require_spotted", true);
            TriggerbotRequirePenetration = SandboxJson.Bool(data, "triggerbot_require_penetration", false);
            TriggerbotRequireDirectHit = SandboxJson.Bool(data, "triggerbot_require_direct_hit", true);
            TriggerbotUseAimbot = SandboxJson.Bool(data, "triggerbot_use_aimbot", true);
            TriggerbotFireWhileMoving = SandboxJson.Bool(data, "triggerbot_fire_while_moving", true);
            Global.Reset();
            Global.Read(data, string.Empty);
            Player.Reset();
            Player.Read(data, "player_");
            DefaultBotSkill = SandboxJson.Int(data, "default_bot_skill", SandboxBotSlot.RandomSelection);
            DefaultBotsHarmless = SandboxJson.Bool(data, "default_bots_harmless", false);
            DefaultBotsStationary = SandboxJson.Bool(data, "default_bots_stationary", false);
        }
    }
}
