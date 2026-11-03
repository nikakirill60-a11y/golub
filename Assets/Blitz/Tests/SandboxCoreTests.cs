using System.Collections.Generic;
using System.Linq;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Rng;
using BlitzLegacy.Core.Sandbox;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Sandbox rules ported from training_config.gd, training_presets.gd and
    /// training_mode_screen.gd: multipliers, validation, presets and their
    /// exchange codes, the editor helpers, tuning and the roster planner.
    /// </summary>
    public sealed class SandboxCoreTests
    {
        private static readonly string[] Maps = { "rudniki", "ordeal", "karieri" };
        private static TanksDatabase _tanks;

        private static TanksDatabase Tanks
        {
            get
            {
                if (_tanks == null)
                {
                    _tanks = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
                }
                return _tanks;
            }
        }

        private static SandboxConfig Valid()
        {
            var config = new SandboxConfig();
            config.Validate(Maps);
            return config;
        }

        private static List<TankInfo> Picker()
        {
            return SandboxEditor.PickerTanks(Tanks, null);
        }

        // ------------------------------------------------------------------
        // Multipliers.
        // ------------------------------------------------------------------

        [Test]
        public void Multiplier_OffsetsDoubleEverySixSteps()
        {
            Assert.AreEqual(1f, SandboxMultiplierMath.OffsetToMultiplier(0f));
            Assert.AreEqual(2f, SandboxMultiplierMath.OffsetToMultiplier(6f), 1e-5f);
            Assert.AreEqual(0.5f, SandboxMultiplierMath.OffsetToMultiplier(-6f), 1e-5f);
            Assert.AreEqual(SandboxMultiplierMath.MaxMultiplier, SandboxMultiplierMath.OffsetToMultiplier(99f));
            Assert.AreEqual(1f, SandboxMultiplierMath.OffsetToMultiplier(float.NaN));
            Assert.AreEqual(6f, SandboxMultiplierMath.MultiplierToOffset(2f), 1e-4f);
            Assert.AreEqual(0f, SandboxMultiplierMath.MultiplierToOffset(1f));
        }

        [Test]
        public void Multiplier_SanitizeAndCombineStayInRange()
        {
            Assert.AreEqual(1f, SandboxMultiplierMath.SanitizeMultiplier(float.PositiveInfinity));
            Assert.AreEqual(1f, SandboxMultiplierMath.SanitizeMultiplier(0f));
            Assert.AreEqual(0.5f, SandboxMultiplierMath.SanitizeMultiplier(-2f), 1e-6f, "negative = reciprocal");
            Assert.AreEqual(SandboxMultiplierMath.MinMultiplier, SandboxMultiplierMath.SanitizeMultiplier(0.001f));
            Assert.AreEqual(6f, SandboxMultiplierMath.Combine(2f, 3f), 1e-5f);
            Assert.AreEqual(SandboxMultiplierMath.MaxMultiplier, SandboxMultiplierMath.Combine(4f, 4f));
            Assert.AreEqual(2f, SandboxMultiplierMath.SnapMultiplier(2.03f), 1e-4f);
        }

        [Test]
        public void Multipliers_SanitizeAndSerialize()
        {
            var m = new SandboxMultipliers();
            Assert.IsTrue(m.IsNeutral);
            m[SandboxStat.Damage] = 2f;
            m[SandboxStat.Hp] = float.NaN;
            m.Sanitize();
            Assert.AreEqual(1f, m.Hp);
            Assert.AreEqual(2f, m.Damage);
            Assert.IsFalse(m.IsNeutral);

            var node = JsonNode.NewObject();
            m.Write(node, "global_");
            var back = new SandboxMultipliers();
            back.Read(node, "global_");
            Assert.AreEqual(2f, back.Damage);
            back.Reset();
            Assert.IsTrue(back.IsNeutral);
        }

        // ------------------------------------------------------------------
        // Config.
        // ------------------------------------------------------------------

        [Test]
        public void Config_ValidateFillsTeamsAndClampsEverything()
        {
            var config = Valid();
            Assert.AreEqual(7, config.AlliedTeamSize);
            Assert.AreEqual(7, config.EnemyTeamSize);
            Assert.AreEqual(SandboxConfig.MaxSlots, config.AlliedSlots.Count);
            Assert.AreEqual(SandboxConfig.MaxSlots, config.EnemySlots.Count);
            Assert.IsTrue(config.MapIsRandom);

            config.AlliedTeamSize = 99;
            config.EnemyTeamSize = -3;
            config.BattleTimeMinutes = 500f;
            config.UprisingLives = 0;
            config.AimbotFov = float.NaN;
            config.BaseMode = (SandboxBaseMode)17;
            config.BigBossLives = 0;
            config.BigBossTeamPoints = 999;
            config.BigBossBuffMul = float.NaN;
            config.SelectedMapId = "atlantis";
            Assert.IsTrue(config.Validate(Maps));
            Assert.AreEqual(SandboxConfig.MaxSlots, config.AlliedTeamSize);
            Assert.AreEqual(7, config.EnemyTeamSize, "a non-positive size falls back to the legacy team size");
            Assert.AreEqual(SandboxConfig.MaxBattleMinutes, config.BattleTimeMinutes);
            Assert.AreEqual(1, config.UprisingLives);
            Assert.AreEqual(45f, config.AimbotFov);
            Assert.AreEqual(SandboxBaseMode.BigBoss, config.BaseMode, "the last rule set");
            Assert.AreEqual(1, config.BigBossLives);
            Assert.AreEqual(200, config.BigBossTeamPoints);
            Assert.AreEqual(1f, config.BigBossBuffMul);
            Assert.IsTrue(config.MapIsRandom, "an unknown map falls back to RANDOM");

            config.SelectedMapId = "ordeal";
            config.Validate(Maps);
            Assert.AreEqual("ordeal", config.SelectedMapId);
            Assert.IsFalse(config.Validate(Maps), "a valid config is left alone");
        }

        [Test]
        public void Config_ZeroMinutesMeansNoTimeLimit()
        {
            var config = Valid();
            config.BattleTimeMinutes = 0f;
            Assert.AreEqual(0f, config.BattleTimeSeconds);
            config.BattleTimeMinutes = 2.5f;
            Assert.AreEqual(150f, config.BattleTimeSeconds);
        }

        [Test]
        public void Config_ExportRoundTripsAndCopiesAreDeep()
        {
            var config = Valid();
            config.BaseMode = SandboxBaseMode.Gravity;
            config.SelectedMapId = "karieri";
            config.CheatAimbot = true;
            config.AimbotAimPoint = SandboxAimPoint.Tracks;
            config.Global[SandboxStat.Reload] = 0.5f;
            config.EnemySlots[2].SelectTank(Picker()[0].Name);
            config.EnemySlots[2].Skill = 3;

            var json = config.ToExport().ToJson(false);
            var back = SandboxConfig.FromExport(JsonNode.Parse(json));
            Assert.AreEqual(json, back.ToExport().ToJson(false));
            Assert.AreEqual(SandboxBaseMode.Gravity, back.BaseMode);
            Assert.AreEqual(Picker()[0].Name, back.EnemySlots[2].TankName);

            var copy = config.Copy();
            copy.EnemySlots[2].MakeRandom();
            copy.Global[SandboxStat.Reload] = 1f;
            Assert.IsFalse(config.EnemySlots[2].RandomTank, "Copy() shares no slot");
            Assert.AreEqual(0.5f, config.Global.Reload, "Copy() shares no multipliers");
        }

        [Test]
        public void Config_BigBossAndTurnRateRoundTripAndGodotCodesKeepTheDefaults()
        {
            var config = Valid();
            config.BaseMode = SandboxBaseMode.BigBoss;
            config.BigBossLives = 5;
            config.BigBossTeamPoints = 12;
            config.BigBossBuffMul = 2.5f;
            config.Global[SandboxStat.TurnRate] = 2f;
            config.Player[SandboxStat.TurnRate] = 0.5f;
            // Only an exact seat keeps its own row; a random one resets it.
            config.EnemySlots[1].SelectTank(Picker()[0].Name);
            config.EnemySlots[1].Multipliers[SandboxStat.TurnRate] = 3f;

            var node = config.ToExport();
            Assert.AreEqual(3, node["base_mode"].AsInt());
            Assert.AreEqual(5, node["big_boss_lives"].AsInt());
            Assert.AreEqual(12, node["big_boss_team_points"].AsInt());
            Assert.AreEqual(2.5, node["big_boss_buff_mul"].AsDouble(), 1e-6);
            Assert.AreEqual(2.0, node["turn_rate_mul"].AsDouble(), 1e-6);
            Assert.AreEqual(0.5, node["player_turn_rate_mul"].AsDouble(), 1e-6);
            var back = SandboxConfig.FromExport(JsonNode.Parse(node.ToJson(false)));
            Assert.AreEqual(node.ToJson(false), back.ToExport().ToJson(false));
            Assert.AreEqual(SandboxBaseMode.BigBoss, back.BaseMode);
            Assert.AreEqual(3f, back.EnemySlots[1].Multipliers.TurnRate);

            // A Godot code has none of these keys: the shipped mode and the stock traverse.
            var godot = SandboxConfig.FromExport(JsonNode.Parse("{\"base_mode\":2,\"hp_mul\":2}"));
            Assert.AreEqual(SandboxBaseMode.Uprising, godot.BaseMode);
            Assert.AreEqual(3, godot.BigBossLives);
            Assert.AreEqual(30, godot.BigBossTeamPoints);
            Assert.AreEqual(1f, godot.BigBossBuffMul);
            Assert.AreEqual(2f, godot.Global.Hp);
            Assert.AreEqual(1f, godot.Global.TurnRate);
            Assert.AreEqual(1f, godot.Player.TurnRate);
        }

        // ------------------------------------------------------------------
        // Presets.
        // ------------------------------------------------------------------

        [Test]
        public void Library_AddSelectRenameRemove()
        {
            var library = new SandboxPresetLibrary();
            var first = library.Add("Default");
            var second = library.Add("  Two   spaces  ");
            Assert.AreEqual(second.Id, library.ActiveId, "a new preset becomes active");
            Assert.AreEqual("Two spaces", second.DisplayName);
            Assert.IsTrue(library.Select(first.Id));
            Assert.IsFalse(library.Select("missing"));

            Assert.IsTrue(library.Rename(first.Id, new string('x', 90), "fallback"));
            Assert.AreEqual(SandboxPresetLibrary.MaxNameLength, first.DisplayName.Length);
            library.Rename(first.Id, "   ", "fallback");
            Assert.AreEqual("fallback", first.DisplayName);

            Assert.IsTrue(library.Remove(first.Id));
            Assert.AreEqual(second.Id, library.ActiveId, "removing the active preset activates a neighbour");
            Assert.IsFalse(library.Remove(second.Id), "the last preset stays");

            while (!library.IsFull) { Assert.IsNotNull(library.Add("p")); }
            Assert.AreEqual(SandboxPresetLibrary.MaxPresets, library.Count);
            Assert.IsNull(library.Add("overflow"));
        }

        [Test]
        public void Library_ExchangeCodeRoundTrips()
        {
            var library = new SandboxPresetLibrary();
            var preset = library.Add("Sniper duel");
            preset.Config.Validate(Maps);
            preset.Config.SelectedMapId = "rudniki";
            preset.Config.EnemyTeamSize = 1;
            preset.Config.CheatWallhack = true;

            var code = library.ExportActive();
            Assert.IsTrue(SandboxPresetLibrary.LooksLikeCode(code));
            string id;
            Assert.AreEqual(SandboxImportError.None, library.Import(code, "fallback", Maps, out id));
            var imported = library.Get(id);
            Assert.AreEqual(id, library.ActiveId);
            Assert.AreEqual("Sniper duel", imported.DisplayName);
            Assert.AreEqual("rudniki", imported.Config.SelectedMapId);
            Assert.AreEqual(1, imported.Config.EnemyTeamSize);
            Assert.IsTrue(imported.Config.CheatWallhack);

            // A code naming a map this build lacks still imports, on RANDOM.
            Assert.AreEqual(SandboxImportError.None, library.Import(code, "fallback", new[] { "moon" }, out id));
            Assert.IsTrue(library.Get(id).Config.MapIsRandom);
        }

        [Test]
        public void Library_ImportRejectsBadCodes()
        {
            var library = new SandboxPresetLibrary();
            library.Add("Default");
            string id;
            Assert.AreEqual(SandboxImportError.Format, library.Import("not json", "x", Maps, out id));
            Assert.AreEqual(SandboxImportError.Format, library.Import("{\"format\":\"other\"}", "x", Maps, out id));
            Assert.AreEqual(SandboxImportError.Version, library.Import(
                "{\"format\":\"" + SandboxPresetLibrary.ExportFormat + "\",\"version\":99,\"config\":{}}", "x", Maps, out id));
            Assert.AreEqual(SandboxImportError.Config, library.Import(
                "{\"format\":\"" + SandboxPresetLibrary.ExportFormat + "\",\"version\":1,\"config\":5}", "x", Maps, out id));
            Assert.AreEqual(1, library.Count, "nothing was added");

            var code = library.ExportActive();
            while (!library.IsFull) { library.Add("p"); }
            Assert.AreEqual(SandboxImportError.Limit, library.Import(code, "x", Maps, out id));
            Assert.AreEqual(string.Empty, id);
        }

        [Test]
        public void Store_CreatesTheDefaultPresetAndPersists()
        {
            var storage = new MemorySaveStorage();
            var store = new SandboxPresetStore(storage);
            var library = store.Load("Default", Maps, Tanks);
            Assert.AreEqual(1, library.Count);
            Assert.AreEqual("Default", library.Active.DisplayName);
            Assert.IsTrue(storage.Exists(SandboxPresetStore.PresetsPath));

            var duel = library.Add("Duel");
            duel.Config.Validate(Maps);
            duel.Config.AlliedSlots[0].SelectTank(Picker()[3].Name);
            store.Save(library);

            var back = new SandboxPresetStore(storage).Load("Default", Maps, Tanks);
            Assert.AreEqual(2, back.Count);
            Assert.AreEqual(duel.Id, back.ActiveId);
            Assert.AreEqual(Picker()[3].Name, back.Active.Config.AlliedSlots[0].TankName);
        }

        [Test]
        public void Store_RepairsCorruptFilesAndUnknownTanks()
        {
            var storage = new MemorySaveStorage();
            storage.WriteAllText(SandboxPresetStore.PresetsPath, "{ broken");
            var library = new SandboxPresetStore(storage).Load("Default", Maps, Tanks);
            Assert.AreEqual(1, library.Count, "a corrupt file starts over with the default preset");

            library.Active.Config.AlliedSlots[0].SelectTank("no_such_tank");
            Assert.IsTrue(SandboxValidation.Validate(library, "Default", Maps, Tanks));
            Assert.IsTrue(library.Active.Config.AlliedSlots[0].RandomTank, "an unknown tank falls back to random");
        }

        // ------------------------------------------------------------------
        // Editor helpers (training_mode_screen.gd).
        // ------------------------------------------------------------------

        [Test]
        public void Editor_AlliedSlotsExcludeThePlayer()
        {
            var config = Valid();
            config.AlliedTeamSize = 3;
            config.EnemyTeamSize = 2;
            Assert.AreEqual(2, SandboxEditor.ActiveSlotCount(config, false));
            Assert.AreEqual(2, SandboxEditor.ActiveSlotCount(config, true));
            config.AlliedTeamSize = 1;
            Assert.AreEqual(0, SandboxEditor.ActiveSlotCount(config, false));
        }

        [Test]
        public void Editor_ChooseTankFillsSlotsInOrder()
        {
            var config = Valid();
            config.EnemyTeamSize = 2;
            var tanks = Picker();
            Assert.AreEqual(1, SandboxEditor.ChooseTank(config, true, 1, tanks[0].Name));
            Assert.AreEqual(SandboxRosterMode.Custom, config.EnemyRosterMode);
            Assert.AreEqual(0, SandboxEditor.NextRandomSlot(config, true, 1), "the search wraps around");
            Assert.AreEqual(0, SandboxEditor.ChooseTank(config, true, -1, tanks[1].Name));
            Assert.AreEqual(-1, SandboxEditor.NextRandomSlot(config, true, 0));
            Assert.AreEqual(-1, SandboxEditor.ChooseTank(config, true, -1, tanks[2].Name), "no free slot");
            Assert.AreEqual(2, SandboxEditor.ExactTanks(config, true, Tanks).Count);
            Assert.IsTrue(SandboxEditor.SelectedNames(config, true).Contains(tanks[0].Name.ToUpperInvariant()));

            Assert.IsTrue(SandboxEditor.MakeSlotRandom(config, true, 0));
            Assert.IsFalse(SandboxEditor.MakeSlotRandom(config, true, 99));
            SandboxEditor.ClearTeam(config, true);
            Assert.AreEqual(0, SandboxEditor.ExactTanks(config, true, Tanks).Count);
        }

        [Test]
        public void Editor_PickerListsVisibleTanksByLevel()
        {
            var tanks = Picker();
            Assert.Greater(tanks.Count, 10);
            Assert.IsFalse(tanks.Any(t => t.Hidden), "hidden tanks are not offered");
            Assert.IsFalse(tanks.Any(t => t.Name == TankSaveData.DefaultName), "the base tank is not offered");
            for (var i = 1; i < tanks.Count; i++)
            {
                Assert.LessOrEqual(tanks[i - 1].Level, tanks[i].Level);
            }

            var named = SandboxEditor.PickerTanks(Tanks, t => "Tank " + t.Name);
            Assert.AreEqual(tanks.Count, named.Count);
            Assert.IsTrue(SandboxEditor.MatchesSearch(tanks[0], "Pretty NAME", "name"));
            Assert.IsTrue(SandboxEditor.MatchesSearch(tanks[0], "x", tanks[0].Name.ToUpperInvariant()));
            Assert.IsTrue(SandboxEditor.MatchesSearch(tanks[0], "x", "   "));
            Assert.IsFalse(SandboxEditor.MatchesSearch(tanks[0], "x", "zz-no-such-zz"));
        }

        [Test]
        public void Editor_NumbersAndFormatsMatchGodot()
        {
            Assert.AreEqual("1.5", SandboxEditor.Number(1.5f));
            Assert.AreEqual("2", SandboxEditor.Number(2f));
            Assert.AreEqual("0.33", SandboxEditor.Number(1f / 3f));
            Assert.AreEqual("0", SandboxEditor.Number(-0.0001f));
            Assert.AreEqual("7 min", SandboxEditor.SliderText(7f, "min"));
            Assert.AreEqual("×2", SandboxEditor.MultiplierText(6f));
            Assert.AreEqual(0.25f, SandboxEditor.Snap(0.37f, 0f, 1f, 0.25f));
            Assert.AreEqual(1f, SandboxEditor.Snap(4f, 0f, 1f, 0.25f));
            Assert.AreEqual(0f, SandboxEditor.Snap(float.NaN, 0f, 1f, 0.25f));
            Assert.AreEqual("HP ×1.5 · 3%", SandboxEditor.Percent("HP ×%s · %d%%", 1.5f, 3));
            Assert.AreEqual("a %s", SandboxEditor.Percent("a %s"), "a missing argument stays literal");
            Assert.AreEqual(string.Empty, SandboxEditor.Percent(null, 1));
        }

        // ------------------------------------------------------------------
        // Tuning, Uprising rules and cheats.
        // ------------------------------------------------------------------

        [Test]
        public void Tuning_AppliesFactorsWithFloors()
        {
            Assert.AreEqual(1500, SandboxTuning.Health(1000, 1.5f));
            Assert.AreEqual(1, SandboxTuning.Health(1, 0.1f));
            Assert.AreEqual(100, SandboxTuning.Damage(100, 1f));
            Assert.AreEqual(51, SandboxTuning.Damage(101, 0.5f), "Godot round(): halves away from zero");
            Assert.AreEqual(SandboxTuning.MinReload, SandboxTuning.Reload(0.01f, 0.1f));
            Assert.AreEqual(1f, SandboxTuning.Mobility(0.2f, 1f));
            Assert.AreEqual(100f, SandboxTuning.TurnRate(40f, 2.5f), 1e-4f);
            Assert.AreEqual(SandboxTuning.MinTurnRate, SandboxTuning.TurnRate(5f, 0.1f));

            var config = Valid();
            config.Global[SandboxStat.Hp] = 2f;
            var local = new SandboxMultipliers();
            local[SandboxStat.Hp] = 3f;
            config.Global[SandboxStat.TurnRate] = 2f;
            local[SandboxStat.TurnRate] = 1.5f;
            var factors = SandboxTankFactors.For(config, local);
            Assert.AreEqual(6f, factors.Hp, 1e-5f);
            Assert.AreEqual(1f, factors.Damage);
            Assert.AreEqual(3f, factors.TurnRate, 1e-5f);
            Assert.AreEqual(1f, SandboxTankFactors.Neutral.TurnRate);
            Assert.AreEqual(1f, SandboxTankFactors.For(null, local).Hp);
        }

        [Test]
        public void Tuning_BaseModeAndUprisingRules()
        {
            var config = Valid();
            Assert.AreEqual(BattleMode.Normal, SandboxTuning.BaseMode(config));
            config.BaseMode = SandboxBaseMode.Gravity;
            Assert.AreEqual(BattleMode.Gravity, SandboxTuning.BaseMode(config));
            config.BaseMode = SandboxBaseMode.Uprising;
            Assert.AreEqual(BattleMode.Uprising, SandboxTuning.BaseMode(config));
            config.BaseMode = SandboxBaseMode.BigBoss;
            Assert.AreEqual(BattleMode.BigBoss, SandboxTuning.BaseMode(config));

            config.UprisingLives = 5;
            config.UprisingUpgradePowerMul = 2f;
            config.UprisingCostLevel2 = 321;
            var rules = SandboxTuning.Uprising(config);
            Assert.AreEqual(5, rules.Lives);
            Assert.AreEqual(4, rules.Respawns);
            Assert.IsTrue(rules.OverridesCosts);
            Assert.IsFalse(UprisingRules.Standard.OverridesCosts);
            Assert.AreEqual(2, UprisingRules.Standard.Respawns);

            var elixir = JsonNode.Parse("{\"kind\":1}");
            var other = JsonNode.Parse("{\"kind\":0}");
            Assert.AreEqual(1.4f, rules.Scale(elixir, "dmg_out_mul", 1.2f), 1e-5f);
            Assert.AreEqual(20f, rules.Scale(elixir, "radius", 10f), 1e-5f);
            Assert.AreEqual(7f, rules.Scale(elixir, "duration", 7f));
            Assert.AreEqual(1.2f, rules.Scale(other, "dmg_out_mul", 1.2f));
            CollectionAssert.AreEqual(new[] { 2f, 4f }, rules.ScaleAll(elixir, new[] { 1f, 2f }));
        }

        [Test]
        public void Tuning_BigBossTablesTakeThePresetLivesPointsAndBoosts()
        {
            var json = BlitzResources.LoadText(BigBossRules.Resource);
            var shipped = new BigBossRules(json);
            var config = Valid();
            Assert.AreEqual((int)shipped.P("bossLives"), config.BigBossLives, "the defaults are the shipped profile");
            Assert.AreEqual((int)shipped.P("startPoints"), config.BigBossTeamPoints);
            var stock = SandboxTuning.BigBoss(config, json);
            Assert.AreEqual(shipped.Data["abilities"].ToJson(false), stock.Data["abilities"].ToJson(false),
                "a default preset plays the shipped abilities");
            Assert.AreEqual(shipped.P("bossMaxHealthFactor"), stock.P("bossMaxHealthFactor"));
            Assert.AreEqual(shipped.P("bossLives"), SandboxTuning.BigBoss(null, json).P("bossLives"));

            config.BigBossLives = 6;
            config.BigBossTeamPoints = 45;
            config.BigBossBuffMul = 2f;
            var tuned = SandboxTuning.BigBoss(config, json);
            Assert.AreEqual(6, new BigBossState(tuned, 1).Lives);
            Assert.AreEqual(-1, new BigBossState(tuned, 2).Lives, "ordinary crews have no lives");
            var match = new BigBossMatch(tuned);
            Assert.AreEqual(45, match.TeamA);
            Assert.AreEqual(45, match.TeamB);
            Assert.AreEqual(1.8f, tuned.Number("bigBoss", "commonDamageFactor"), 1e-5f);
            Assert.AreEqual(1.4f, shipped.Number("bigBoss", "commonDamageFactor"), 1e-5f, "the shipped tables are untouched");
        }

        [Test]
        public void UprisingRules_ClampHostileValues()
        {
            var rules = new UprisingRules(99, double.NaN, -5, 0, 5000, -1, float.PositiveInfinity,
                new[] { -1, 9999, 50 });
            Assert.AreEqual(10, rules.Lives);
            Assert.AreEqual(1.0, rules.ElixirPerSecond);
            Assert.AreEqual(0, rules.ElixirPerDamagingShot);
            Assert.AreEqual(1, rules.ElixirDamageStep);
            Assert.AreEqual(1000, rules.ElixirPerDamageStep);
            Assert.AreEqual(0, rules.ElixirPerDeath);
            Assert.AreEqual(1f, rules.UpgradePower);
            Assert.Throws<System.ArgumentException>(() => new UprisingRules(3, 1, 2, 100, 5, 100, 1f, new[] { 1 }));
        }

        [Test]
        public void CheatMath_TargetingRules()
        {
            Assert.Less(SandboxCheatMath.TargetScore(SandboxAimbotPriority.Crosshair, 2f, 500f, 100f, 100f),
                SandboxCheatMath.TargetScore(SandboxAimbotPriority.Crosshair, 5f, 10f, 100f, 100f),
                "crosshair priority: the smaller angle wins");
            Assert.Less(SandboxCheatMath.TargetScore(SandboxAimbotPriority.Distance, 30f, 50f, 100f, 100f),
                SandboxCheatMath.TargetScore(SandboxAimbotPriority.Distance, 1f, 200f, 100f, 100f),
                "distance priority: the nearer tank wins");
            Assert.Less(SandboxCheatMath.TargetScore(SandboxAimbotPriority.LowHealth, 30f, 500f, 10f, 100f),
                SandboxCheatMath.TargetScore(SandboxAimbotPriority.LowHealth, 1f, 50f, 90f, 100f),
                "low-health priority: the weaker tank wins");

            Assert.IsTrue(SandboxCheatMath.InsideFov(22f, 45f));
            Assert.IsFalse(SandboxCheatMath.InsideFov(23f, 45f));
            Assert.AreEqual(1f, SandboxCheatMath.SmoothingAlpha(0f, 0.016f));
            Assert.That(SandboxCheatMath.SmoothingAlpha(10f, 0.016f), Is.InRange(0.1f, 0.2f));
            Assert.AreEqual(0.5f, SandboxCheatMath.LeadTime(400f, 800f, 1f), 1e-6f);
            Assert.AreEqual(0f, SandboxCheatMath.LeadTime(400f, 0f, 1f));
            Assert.IsFalse(SandboxCheatMath.InWallhackBand(50f, 50f, 720f), "close tanks keep the normal outline");
            Assert.IsTrue(SandboxCheatMath.InWallhackBand(51f, 50f, 720f));
            Assert.IsFalse(SandboxCheatMath.InWallhackBand(721f, 50f, 720f));
            Assert.AreEqual(0f, SandboxCheatMath.Incidence(1f), 1e-6f);
            Assert.IsTrue(SandboxCheatMath.CanPenetrate(null, true, 100f, 0f));
            Assert.IsTrue(SandboxCheatMath.CanPenetrate(new ShellInfo(), false, 100f, 0f), "no plate, nothing to stop it");
        }

        // ------------------------------------------------------------------
        // Roster.
        // ------------------------------------------------------------------

        [Test]
        public void Roster_SeatsConfiguredBotsAndRespectsSpawns()
        {
            var tanks = Picker();
            var player = tanks.First(t => t.Level == 5);
            var config = Valid();
            config.AlliedTeamSize = 3;
            config.EnemyTeamSize = 4;
            SandboxEditor.ChooseTank(config, false, 0, tanks[0].Name);
            config.AlliedSlots[0].Skill = 2;
            config.AlliedSlots[0].Harmless = true;
            config.DefaultBotsStationary = true;

            var plan = SandboxRosterPlanner.Build(config, Tanks, player, true, 10, 3, new SeededRandomSource(7));
            Assert.AreEqual(3, plan.AlliedTotal);
            Assert.AreEqual(2, plan.Allies.Count, "the player takes the first allied seat");
            Assert.AreEqual(3, plan.EnemyTotal);
            Assert.IsTrue(plan.EnemyLimitedByMap);
            Assert.IsFalse(plan.AlliedLimitedByMap);
            Assert.AreEqual(tanks[0].Name, plan.Allies[0].TankName);
            Assert.AreEqual(2, plan.Allies[0].Skill);
            Assert.IsTrue(plan.Allies[0].Harmless);
            Assert.IsFalse(plan.Allies[0].Stationary, "a configured slot keeps its own flags");
            Assert.IsTrue(plan.Allies[1].Stationary, "a random slot takes the bot defaults");
            Assert.IsTrue(plan.Enemies.All(s => Tanks.Has(s.TankName)));

            Assert.Throws<System.InvalidOperationException>(() =>
                SandboxRosterPlanner.Build(config, Tanks, player, true, 10, 0, new SeededRandomSource(7)));
        }

        [Test]
        public void Roster_MirrorBalancerCopiesTheAllies()
        {
            var tanks = Picker();
            var player = tanks.First(t => t.Level == 6);
            var config = Valid();
            config.Balancer = SandboxBalancer.MirrorAllies;
            config.AlliedTeamSize = 3;
            config.EnemyTeamSize = 3;
            var plan = SandboxRosterPlanner.Build(config, Tanks, player, false, 10, 10, new SeededRandomSource(3));
            Assert.IsFalse(plan.PlayerOnTeamA);
            Assert.AreEqual(player.Name, plan.Enemies[0].TankName);
            Assert.AreEqual(plan.Allies[0].TankName, plan.Enemies[1].TankName);
            Assert.AreEqual(plan.Allies[1].TankName, plan.Enemies[2].TankName);

            config.PlayerTeam = SandboxTeamChoice.TeamB;
            Assert.IsFalse(SandboxRosterPlanner.PlayerOnTeamA(config, new SeededRandomSource(1)));
            config.PlayerTeam = SandboxTeamChoice.TeamA;
            Assert.IsTrue(SandboxRosterPlanner.PlayerOnTeamA(config, new SeededRandomSource(1)));
        }

        // ------------------------------------------------------------------
        // Mode catalogue.
        // ------------------------------------------------------------------

        [Test]
        public void ModeCatalog_SandboxIsAMenuModeButNeverOnline()
        {
            CollectionAssert.Contains(BattleModeCatalog.MenuModes, BattleMode.Sandbox);
            CollectionAssert.DoesNotContain(BattleModeCatalog.SelectorModes, BattleMode.Sandbox);
            Assert.AreEqual("training", BattleModeCatalog.Id(BattleMode.Sandbox));
            Assert.AreEqual("battle_mode:training/name", BattleModeCatalog.NameKey(BattleMode.Sandbox));
            Assert.IsFalse(BattleModeCatalog.AllowsOnline(BattleMode.Sandbox));
            Assert.IsFalse(BattleModeCatalog.AllowsOnline((BattleMode)42));
            foreach (var mode in BattleModeCatalog.SelectorModes)
            {
                Assert.IsTrue(BattleModeCatalog.AllowsOnline(mode), mode.ToString());
            }
        }
    }
}
