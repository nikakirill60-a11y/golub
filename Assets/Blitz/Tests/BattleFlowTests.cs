using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Ui;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    public sealed class BattleFlowTests
    {
        [Test]
        public void Queue_UsesSourceTierRatiosAndClassRemainder()
        {
            var model = new BattleQueueModel();
            model.Refresh((min, max) => min, (min, max) => min);

            var expected = new[] { 4, 12, 8, 16, 5, 6, 9, 10, 16, 12, 2 };
            var total = 0;
            for (var i = 0; i < expected.Length; i++)
            {
                Assert.AreEqual(expected[i], model.Tiers[i], "tier " + i);
                total += expected[i];
            }
            Assert.AreEqual(total, model.TotalPlayers);
            Assert.AreEqual((int)(total * 0.05f), model.Types[0]);
            Assert.AreEqual((int)(total * 0.05f), model.Types[1]);
            Assert.AreEqual((int)(total * 0.05f), model.Types[2]);
            Assert.AreEqual(total, model.Types[0] + model.Types[1]
                + model.Types[2] + model.Types[3]);
        }

        [Test]
        public void Queue_ClampsRandomFunctionsToSourceRanges()
        {
            var model = new BattleQueueModel();
            model.Refresh((min, max) => int.MaxValue, (min, max) => float.MaxValue);
            for (var i = 0; i < model.Tiers.Count; i++)
            {
                Assert.AreEqual(354 * BattleQueueModel.TierRatios[i] / 100,
                    model.Tiers[i]);
            }
            for (var i = 0; i < 3; i++)
            {
                Assert.AreEqual((int)(model.TotalPlayers * 0.25f), model.Types[i]);
            }
        }

        [TestCase(-2f, "0:00")]
        [TestCase(0f, "0:00")]
        [TestCase(9.9f, "0:09")]
        [TestCase(65f, "1:05")]
        [TestCase(601f, "10:01")]
        public void Queue_FormatsElapsedLikeGodot(float seconds, string expected)
        {
            Assert.AreEqual(expected, BattleQueueModel.FormatElapsed(seconds));
        }

        [Test]
        public void Outcome_MapsWinnerAndDrawFromPlayerPerspective()
        {
            Assert.AreEqual(BattleOutcome.Draw,
                BattleOutcomeRules.FromWinner(-1, 0));
            Assert.AreEqual(BattleOutcome.Victory,
                BattleOutcomeRules.FromWinner(1, 1));
            Assert.AreEqual(BattleOutcome.Defeat,
                BattleOutcomeRules.FromWinner(0, 1));
        }

        [Test]
        public void Rewards_UseSourceXpCreditsGoldAndAchievementFormulas()
        {
            var result = BattleRewardCalculator.Calculate(new BattleRewardInput
            {
                Id = "formula",
                TankId = "ms-1",
                TankName = "TANK_MS1",
                PlayerName = "Tester",
                Outcome = BattleOutcome.Victory,
                FinishReason = BattleFinishReason.Elimination,
                TankLevel = 1,
                FarmCoefficient = 1.29f,
                Spots = 1,
                Damage = 100,
                DamageBlocked = 20,
                Kills = 1,
                Shots = 4,
                Hits = 3,
                Survived = true,
            });

            Assert.AreEqual(258, result.Xp);
            Assert.AreEqual(12, result.FreeXp);
            Assert.AreEqual(4747, result.CreditsEarned);
            Assert.AreEqual(4747, result.Credits);
            Assert.AreEqual(504, result.Gold);
            Assert.AreEqual(2, result.Rewards.Count);
            Assert.AreEqual("first_victory", result.Rewards[0].Id);
            Assert.AreEqual("first_blood", result.Rewards[1].Id);
            Assert.AreEqual(258, result.Player.Xp);
        }

        [Test]
        public void Rewards_ApplyOnceAndRoundTripWithOldSaveMigration()
        {
            var account = new AccountData();
            account.AddTank("ms-1", true);
            var result = BattleRewardCalculator.Calculate(new BattleRewardInput
            {
                Id = "once",
                TankId = "ms-1",
                PlayerName = "Tester",
                TankName = "TANK_MS1",
                Outcome = BattleOutcome.Defeat,
                FinishReason = BattleFinishReason.Timeout,
                Damage = 80,
                DamageTaken = 40,
                Kills = 1,
                Shots = 3,
                Hits = 2,
                HadFirstBlood = true,
            });
            var originalCredits = account.Credits;

            Assert.IsTrue(BattleResultApplier.Apply(account, result));
            Assert.IsFalse(BattleResultApplier.Apply(account, result));
            Assert.AreEqual(originalCredits + result.Credits, account.Credits);
            Assert.AreEqual(1, account.StatBattles);
            Assert.AreEqual(1, account.StatKills);
            Assert.AreEqual(1, account.BattleResults.Count);
            Assert.AreEqual(result.Xp, account.GetTank("ms-1").Exp);

            var roundTrip = AccountData.FromJson(account.ToJson());
            Assert.AreEqual(1, roundTrip.BattleResults.Count);
            Assert.AreEqual("once", roundTrip.BattleResults[0].Id);
            Assert.AreEqual(1, roundTrip.BattleResults[0].Participants.Count);
            Assert.IsTrue(roundTrip.BattleResults[0].Participants[0].IsPlayer);

            var oldSave = AccountData.FromJson(JsonNode.NewObject()
                .Set("name", "Old").Set("credits", 123));
            Assert.AreEqual(0, oldSave.BattleResults.Count);
            Assert.AreEqual(123, oldSave.Credits);
        }

        [Test]
        public void IntroTiming_UsesTrackRollCapAndFallback()
        {
            Assert.AreEqual(6f, BattleLoadingScreen.CalculateDuration(0f, 0.5f));
            Assert.AreEqual(40f, BattleLoadingScreen.CalculateDuration(100f, 0f), 0.001f);
            Assert.AreEqual(75f, BattleLoadingScreen.CalculateDuration(100f, 1f), 0.001f);
            Assert.AreEqual(0.6f, BattleLoadingScreen.CalculateDuration(1f, 1f), 0.001f);
            // map_loading_screen_timer_mul scales the wanted slice and is
            // clamped to 0.2-3, but the track still has to cover the screen.
            Assert.AreEqual(20f,
                BattleLoadingScreen.CalculateDuration(100f, 0f, 0.5f), 0.001f);
            Assert.AreEqual(99.2f,
                BattleLoadingScreen.CalculateDuration(100f, 0f, 3f), 0.001f);
            Assert.AreEqual(8f,
                BattleLoadingScreen.CalculateDuration(100f, 0f, 0.05f), 0.001f);
            Assert.AreEqual(40f,
                BattleLoadingScreen.CalculateDuration(100f, 0f, 1f), 0.001f);
            Assert.AreEqual(1f, SettingsData.DefaultMapIntroTimeMul);
            Assert.AreEqual(0.2f, SettingsData.ClampMapIntroTimeMul(0f));
            Assert.AreEqual(3f, SettingsData.ClampMapIntroTimeMul(10f));
            var savedMul = SettingsData.FromJson(
                new SettingsData { MapIntroTimeMul = 1.5f }.ToJson());
            Assert.AreEqual(1.5f, savedMul.MapIntroTimeMul, 0.001f);
            // maps/loading_screen.tscn has no animation or script, so the wheel
            // is static and only carries the authored 0.515 scale.
            Assert.AreEqual(0.515f, LoadingOverlay.SpinnerScale);
            Assert.AreEqual(5f, BattleCountdownScreen.SourceDurationSeconds);
        }

        [Test]
        public void FlowAssets_ExposeEveryScreenAndSourceManifest()
        {
            var manifest = Resources.Load<TextAsset>(
                "Data/battle_flow_source_manifest");
            Assert.IsNotNull(manifest);
            var root = JsonNode.Parse(manifest.text);
            Assert.AreEqual("Tools/port_godot_battle_flow.py",
                root["generator"].AsString());
            Assert.GreaterOrEqual(root["sheets"].Count, 9);
            Assert.AreEqual(4, root["audio"].Count);
            Assert.AreEqual(10, root["queue"]["tierRatios"].Count);

            var atlas = AtlasRegionTable.Parse(
                BlitzResources.LoadText(BlitzResources.AtlasRegionsData));
            foreach (var region in new[] {
                "flow_loading_spinner", "flow_queue_bg", "flow_queue_mode",
                "flow_intro_table", "flow_tank_ms1_small",
                "flow_intro_platoon", "flow_intro_tier_1", "flow_intro_tier_10",
                "flow_result_win_top", "flow_result_draw_top",
                "flow_result_lose_1", "flow_reward_first_blood",
            })
            {
                Assert.IsNotNull(atlas.Get(region), region);
            }
            foreach (var audio in new[] {
                "map_intro_rudniki", "result_victory", "result_defeat", "result_draw",
            })
            {
                Assert.IsNotNull(BlitzResources.LoadAudio(audio), audio);
            }
        }

        [Test]
        public void ResultScreen_HasThreeWorkingTabsAndBothTeams()
        {
            var result = new BattleResultData
            {
                Id = "ui",
                Outcome = BattleOutcome.Draw,
                FinishReason = BattleFinishReason.Timeout,
                Credits = 900,
                CreditsEarned = 900,
                Xp = 40,
                FreeXp = 2,
            };
            for (var i = 0; i < 4; i++)
            {
                result.Participants.Add(new BattleParticipantResult
                {
                    Name = "Player" + i,
                    TankId = "ms-1",
                    TankName = "TANK_MS1",
                    Team = i < 2 ? 0 : 1,
                    IsPlayer = i == 0,
                    Survived = true,
                    Damage = 40 * i,
                });
            }
            var screen = BattleResultScreen.Create(null, result, null);
            Assert.AreEqual(4, screen.TeamRowCount);
            screen.ShowTab(1);
            Assert.AreEqual(1, screen.ActiveTab);
            screen.ShowTab(2);
            Assert.AreEqual(2, screen.ActiveTab);
            Object.DestroyImmediate(screen.gameObject);
        }
        [Test]
        public void PremiumCredits_UsePositiveNetAndLeaveRawAndGoldUntouched()
        {
            var input = new BattleRewardInput { Id = "net", TankLevel = 1,
                PremiumAccount = true, AmmoCost = 200, Outcome = BattleOutcome.Defeat,
                HadFirstBlood = true, HadFirstVictory = true };
            var result = BattleRewardCalculator.Calculate(input);
            Assert.AreEqual(800, result.CreditsEarned);
            Assert.AreEqual(900, result.Credits);
            Assert.AreEqual(0, result.Gold);
            input.AmmoCost = 1000;
            result = BattleRewardCalculator.Calculate(input);
            Assert.AreEqual(-200, result.Credits);
        }

        [Test]
        public void TeamXp_IsRawWhileEarnedXpKeepsTankAndOutcomeBonuses()
        {
            var input = new BattleRewardInput { Id = "raw", TankLevel = 10,
                PremiumTank = true, PremiumAccount = true, Outcome = BattleOutcome.Victory,
                Damage = 100, Spots = 1, Kills = 1 };
            input.Interactions.Add(new BattleXpInteraction { TargetLevel = 5,
                Damage = 100, Spotted = true, Killed = true });
            // maxi truncation: spot x3, damage x2, kill x1.
            var result = BattleRewardCalculator.Calculate(input);
            Assert.AreEqual(370, result.Player.XpRaw);
            Assert.AreEqual(832, result.Xp);
            Assert.AreEqual(1248, result.Player.Xp);
            var restored = BattleResultData.FromJson(result.ToJson());
            Assert.AreEqual(370, restored.Player.XpRaw);
            var old = BattleParticipantResult.FromJson(JsonNode.NewObject().Set("xp", 120));
            Assert.AreEqual(120, old.XpRaw);
        }

        [Test]
        public void Warrior_IsRepeatableLikeTheGodotGenericReward()
        {
            var result = BattleRewardCalculator.Calculate(new BattleRewardInput {
                Id = "repeat", Kills = 4, HighestAllyKills = 3, HadWarrior = true,
                HadFirstVictory = true, HadFirstBlood = true });
            Assert.AreEqual(1, result.Rewards.Count);
            Assert.AreEqual("warrior", result.Rewards[0].Id);
        }

        [Test]
        public void ResultCard_UsesRegisteredArtAndMarkersAndDuplicateMedalCount()
        {
            var result = new BattleResultData { TankId = "f119_projet_murat" };
            result.Participants.Add(new BattleParticipantResult { TankId = result.TankId,
                IsPlayer = true, Name = "Player", Survived = true });
            result.Rewards.Add(new BattleRewardData { Id = "warrior", Gold = 150 });
            result.Rewards.Add(new BattleRewardData { Id = "warrior", Gold = 150 });
            var screen = BattleResultScreen.Create(null, result, null);
            try {
                var main = screen.transform.Find("Canvas/battle_result/main");
                Assert.IsNotNull(main.Find("carousel_tank/icon"));
                Assert.AreEqual("tank_icon_f119projetmurat",
                    main.Find("carousel_tank/icon").GetComponent<UnityEngine.UI.Image>().sprite.name);
                Assert.IsNotNull(main.Find("carousel_tank/lvl"));
                Assert.IsNotNull(main.Find("carousel_tank/class"));
                Assert.IsTrue(main.Find("carousel_tank/slot").GetComponent<UnityEngine.UI.Image>().sprite.name.EndsWith("_6"));
                var rewards = main.Find("rewards_box");
                Assert.AreEqual(1, rewards.childCount);
                Assert.AreEqual("2", rewards.Find("medal_warrior/series/series_counter").GetComponent<UnityEngine.UI.Text>().text);
                Assert.Greater(main.Find("earned/table1").GetComponent<UnityEngine.UI.Image>().sprite.border.x, 0);
            } finally { Object.DestroyImmediate(screen.gameObject); }
        }

        [Test]
        public void QueueCard_IsSetUpLikeTheCarouselCard()
        {
            // ui_queue runs carousel_tank.setup() for the account tank, the
            // hangar carousel's own setup: nation plate, glyphs, name colour.
            const string tankName = "m4a2e4";
            var tank = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData)).Get(tankName);
            Assert.IsTrue(tank.Premium && !tank.Collector && tank.Nation == Nation.Usa, tankName);
            var account = AccountData.FromJson(JsonNode.NewObject().Set("name", "Tester"));
            account.SelectedTankName = tankName;

            MenuStyle.ToolOverride = SettingsData.UiStyleClassic;
            var queue = BattleQueueScreen.Create(null, account, null, null);
            try
            {
                var card = queue.transform.Find("Canvas/battle_queue/carousel_tank");
                Assert.IsNotNull(card);
                Assert.AreEqual(HangarUiKit.CarouselNationRegion(Nation.Usa),
                    card.GetComponent<UnityEngine.UI.Image>().sprite.name);
                Assert.AreEqual("tank_icon_" + tankName,
                    card.Find("icon").GetComponent<UnityEngine.UI.Image>().sprite.name);
                foreach (var glyph in new[] { "class", "lvl" })
                {
                    var image = card.Find(glyph).GetComponent<UnityEngine.UI.Image>();
                    Assert.AreEqual(HangarUiKit.TankGlyphTint(tank), image.color, glyph);
                    Assert.IsTrue(image.sprite.name.EndsWith("_prem"), glyph + " is not the gold glyph");
                }

                Assert.AreEqual(HangarUiKit.TankClassRegion(tank),
                    card.Find("class").GetComponent<UnityEngine.UI.Image>().sprite.name);
                Assert.AreEqual(HangarUiKit.TankLevelRegion(tank),
                    card.Find("lvl").GetComponent<UnityEngine.UI.Image>().sprite.name);
                Assert.AreEqual(HangarUiKit.TankNameColor(tank),
                    card.Find("name").GetComponent<UnityEngine.UI.Text>().color);
            }
            finally
            {
                Object.DestroyImmediate(queue.gameObject);
                MenuStyle.ToolOverride = null;
            }
        }

        [Test]
        public void ResultMedals_UseAllSourceRegionsNativeSizesAndRareFirst()
        {
            var definitions = JsonNode.Parse(Resources.Load<TextAsset>("Data/battle_reward_definitions").text)["definitions"];
            var atlas = BattleFlowUi.LoadAtlas();
            Assert.AreEqual(23, definitions.Count);
            foreach (var item in definitions.Fields)
            {
                var sprite = atlas.Get("reward_" + item.Key);
                Assert.IsNotNull(sprite, item.Key);
                Assert.AreEqual(item.Value["rect"][2].AsFloat(), sprite.rect.width);
                Assert.AreEqual(item.Value["rect"][3].AsFloat(), sprite.rect.height);
            }
            var result = new BattleResultData { TankId = "ms-1" };
            result.Rewards.Add(new BattleRewardData { Id = "warrior", Rarity = 100 });
            result.Rewards.Add(new BattleRewardData { Id = "kolobanov", Rarity = 0 });
            var screen = BattleResultScreen.Create(null, result, null);
            try {
                var box = screen.transform.Find("Canvas/battle_result/main/rewards_box");
                Assert.AreEqual("medal_kolobanov", box.GetChild(0).name);
                var medal = (RectTransform)box.GetChild(1);
                var icon = medal.Find("icon").GetComponent<UnityEngine.UI.Image>();
                const float scale = .709375f;
                Assert.AreEqual((icon.sprite.rect.width + 10) * scale, medal.rect.width, .001f);
                Assert.AreEqual(icon.sprite.rect.width * scale, icon.rectTransform.rect.width, .001f);
                Assert.AreEqual(icon.sprite.rect.height * scale, icon.rectTransform.rect.height, .001f);
            } finally { Object.DestroyImmediate(screen.gameObject); }
        }

        [Test]
        public void OutcomeBanner_ClipsToViewportWidthNotTheCenteredCardWidth()
        {
            var screen = BattleResultScreen.Create(null, new BattleResultData { TankId = "ms-1" }, null);
            try
            {
                var root = (RectTransform)screen.transform.Find("Canvas/battle_result");
                root.sizeDelta += new Vector2(400, 0);
                typeof(BattleResultScreen).GetMethod("RefreshOutcomeWidth",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(screen, null);
                var frame = (RectTransform)root.Find("main/bg");
                Assert.AreEqual(root.rect.width, frame.rect.width, .001f);
                Assert.AreEqual(new Vector2(.5f, 1), frame.pivot);
                Assert.AreEqual(new Vector2(.5f, 1), ((RectTransform)frame.GetChild(0)).anchorMin);
            }
            finally { Object.DestroyImmediate(screen.gameObject); }
        }

        [Test]
        public void ModeLabels_NameThePickedBattleModeLikeGodot()
        {
            // ui_battle_intro.tscn keeps the authored encounter battle for a
            // standard battle and prints the picked mode otherwise.
            Assert.AreEqual("map_intro:enc_battle", BattleLoadingScreen.ModeLabelKey(BattleMode.Normal));
            Assert.AreEqual("battle_mode:gravity/name", BattleLoadingScreen.ModeLabelKey(BattleMode.Gravity));
            Assert.AreEqual("battle_mode:uprising/name", BattleLoadingScreen.ModeLabelKey(BattleMode.Uprising));
            // ui_hangar.tscn rewrites randomBattle/Label with the mode name.
            Assert.AreEqual("battle_mode:normal/name", BattleQueueScreen.ModeLabelKey(BattleMode.Normal, false));
            Assert.AreEqual("battle_mode:gravity/name", BattleQueueScreen.ModeLabelKey(BattleMode.Gravity, false));
            Assert.AreEqual("battle_mode:uprising/name", BattleQueueScreen.ModeLabelKey(BattleMode.Uprising, true));
            Assert.AreEqual(BattleOnline.NameKey, BattleQueueScreen.ModeLabelKey(BattleMode.Normal, true));

            var lang = BattleFlowUi.LoadLanguage();
            Assert.AreEqual("ВОЗРОЖДЕНИЕ", lang.Get("battle_mode:uprising/name", "ru"));
            Assert.AreEqual("ГРАВИТАЦИЯ", lang.Get("battle_mode:gravity/name", "ru"));

            var intro = BattleLoadingScreen.Create(
                null, new System.Collections.Generic.List<BattleParticipantStats>(), null, false, null,
                BattleMode.Uprising);
            try
            {
                Assert.AreEqual(lang.Get("battle_mode:uprising/name"), intro.ModeText);
                var label = intro.GetComponentsInChildren<UnityEngine.UI.Text>(true);
                Assert.That(System.Array.Exists(label, l => l.name == "gamemode" && l.text == intro.ModeText));
            }
            finally { Object.DestroyImmediate(intro.gameObject); }

            var queue = BattleQueueScreen.Create(
                null, AccountData.FromJson(JsonNode.NewObject().Set("name", "Tester")), null, null,
                BattleMode.Gravity);
            try { Assert.AreEqual(lang.Get("battle_mode:gravity/name"), queue.ModeText); }
            finally { Object.DestroyImmediate(queue.gameObject); }
        }
    }
}
