using BlitzLegacy.Core.Localization;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// The shells half of shells_equipment in ui_player.tscn, driven by the
    /// shell block of ui_player_battle.gd: three ui_shell_icon widgets that
    /// mirror the rack of the mounted gun, highlight the selected slot and
    /// count down with projectile_fired.
    /// </summary>
    public sealed class BattleShellSelectorTests
    {
        private const float Tolerance = 1e-4f;

        private GameObject _root;

        [TearDown]
        public void TearDown()
        {
            if (_root != null)
            {
                Object.DestroyImmediate(_root);
                _root = null;
            }
        }

        [Test]
        public void AmmoArtworkCarriesTheSourceBackgroundsAndIcons()
        {
            var atlas = AtlasRegionTable.Parse(
                BlitzResources.LoadText(BlitzResources.AtlasRegionsData));
            var names = new[]
            {
                // ui_shell_icon.BgType frames the battle rack wears.
                "shell_bg_none",
                "shell_bg_battle",
                "shell_bg_selected",
                "shell_icon_4",
                "shell_icon_5",
                "shell_icon_6",
                "shell_icon_empty_4",
                "shell_icon_empty_6",
            };

            for (var i = 0; i < names.Length; i++)
            {
                var sprite = atlas.Get(names[i]);
                Assert.IsNotNull(sprite, names[i]);
                Assert.AreEqual(names[i], sprite.name);
            }

            var lang = LocalizationTable.Parse(
                BlitzResources.LoadText(BlitzResources.LanguageData));
            Assert.AreEqual("AP", lang.Get("Abbr_ARMOR_PIERCING"));
            Assert.AreEqual("APCR", lang.Get("Abbr_ARMOR_PIERCING_CR"));
            Assert.AreEqual("HEAT", lang.Get("Abbr_HOLLOW_CHARGE"));
            Assert.AreEqual("HE", lang.Get("Abbr_HIGH_EXPLOSIVE"));
        }

        [Test]
        public void RackMirrorsTheGunAndFollowsSelectionAndFire()
        {
            _root = new GameObject("BattleShellSelectorTests");
            var canvasObject = new GameObject("Canvas", typeof(RectTransform));
            canvasObject.transform.SetParent(_root.transform, false);
            var canvas = (RectTransform)canvasObject.transform;

            var card = LoadMs1();
            var spawn = new GameObject("Spawn").transform;
            spawn.SetParent(_root.transform, false);
            var tank = BattleTank.Spawn(
                _root.transform, "PlayerMS1", card, spawn,
                new Bounds(Vector3.zero, new Vector3(1000f, 100f, 1000f)), 0);
            tank.SetShellAmounts(new[] { 40, 0 });

            var selector = BattleShellSelector.Create(
                canvas,
                tank,
                AtlasRegionTable.Parse(
                    BlitzResources.LoadText(BlitzResources.AtlasRegionsData)),
                LocalizationTable.Parse(
                    BlitzResources.LoadText(BlitzResources.LanguageData)),
                BlitzResources.LoadFont(BlitzResources.BoldFontName));

            Assert.IsNotNull(selector);
            Assert.AreEqual(
                BattleShellSelector.RackScale, selector.Root.localScale.x, Tolerance);

            // try_setup_shell: the gun's own slots wear the BATTLE frame, the
            // slots it does not carry fall back to the empty NONE frame.
            var first = selector.GetIcon(0);
            var second = selector.GetIcon(1);
            var third = selector.GetIcon(2);
            Assert.AreEqual(BattleShellSelector.Background.Battle, first.Frame);
            Assert.IsFalse(first.Disabled);
            Assert.AreEqual("shell_bg_battle", first.BackgroundImage.sprite.name);
            Assert.AreEqual("40", first.AmountLabel.text);
            Assert.AreEqual(50, first.AmountLabel.fontSize,
                "the BATTLE background shrinks the counter in ui_shell_icon.init");

            Assert.AreEqual(BattleShellSelector.Background.Battle, second.Frame);
            Assert.AreEqual("0", second.AmountLabel.text);
            Assert.IsTrue(second.IconImage.sprite.name.StartsWith("shell_icon_empty_"),
                "an empty slot swaps in the greyed artwork");

            Assert.AreEqual(BattleShellSelector.Background.None, third.Frame);
            Assert.IsTrue(third.Disabled);
            Assert.AreEqual("shell_bg_none", third.BackgroundImage.sprite.name);
            Assert.IsFalse(third.AmountLabel.gameObject.activeSelf);

            // select_shell highlights exactly one slot and never lights up a
            // slot the gun does not carry.
            Assert.IsTrue(first.SelectedImage.gameObject.activeSelf);
            Assert.IsFalse(second.SelectedImage.gameObject.activeSelf);

            selector.Select(1);
            Assert.AreEqual(1, tank.ShellId);
            Assert.IsFalse(first.SelectedImage.gameObject.activeSelf);
            Assert.IsTrue(second.SelectedImage.gameObject.activeSelf);

            // base_tank.select_shell clamps to the mounted gun's rack, so the
            // third key lands back on the last shell the gun carries.
            selector.Select(2);
            Assert.AreEqual(tank.GunInfo.Shells.Count - 1, tank.ShellId);
            Assert.IsFalse(third.SelectedImage.gameObject.activeSelf);

            // can_launch_shell refuses the empty rack the selection sits on.
            Assert.IsFalse(tank.CanLaunchShell(1));
            Assert.IsTrue(tank.CanLaunchShell(0));

            // _on_projectile_fired refreshes every counter from the tank.
            tank.SetShellAmounts(new[] { 39, 0 });
            selector.Refresh();
            Assert.AreEqual("39", first.AmountLabel.text);
        }

        private static TankInfo LoadMs1()
        {
            var database = TanksDatabase.Parse(
                BlitzResources.LoadText(BlitzResources.TanksData));
            var card = database.Get(BattleSession.DefaultTankId);
            Assert.IsNotNull(card);
            return card;
        }
    }
}
