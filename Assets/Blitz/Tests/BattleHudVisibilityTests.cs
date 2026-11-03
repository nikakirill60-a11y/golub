using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// The source builds the battle UI together with the map, but
    /// <c>ui_battle_intro</c> covers the whole viewport until
    /// <c>_finish_battle_intro</c> runs, so the crosshair must not be on
    /// screen before the countdown. A device run showed the crosshair over the
    /// map intro, which is what this pins.
    /// </summary>
    public sealed class BattleHudVisibilityTests
    {
        [Test]
        public void Hud_HidesItsWholeTreeUntilTheIntroHandsOver()
        {
            var hud = BattleHud.Create(null, null, true);
            try
            {
                Assert.IsNotNull(hud.Root);
                Assert.IsTrue(hud.Visible);
                Assert.IsTrue(hud.Root.gameObject.activeSelf);

                // BattleSession hides the HUD for every non-autostart battle,
                // which is the flow the player goes through.
                hud.SetVisible(false);
                Assert.IsFalse(hud.Visible);
                Assert.IsFalse(hud.Root.gameObject.activeSelf);

                // BlitzBootstrap.OnIntroComplete brings it back before the
                // countdown, with the crosshair dots still built.
                hud.SetVisible(true);
                Assert.IsTrue(hud.Visible);
                Assert.IsTrue(hud.Root.gameObject.activeSelf);
                Assert.Greater(hud.CrosshairDotCount, 0);
            }
            finally
            {
                Object.DestroyImmediate(hud.gameObject);
            }
        }
        [Test]
        public void Header_UsesActualClassesAndRetainsDarkDeadSlotsInSourceOrder()
        {
            var root = new GameObject("HeaderRoster");
            var db = BlitzLegacy.Core.Tanks.TanksDatabase.Parse(
                BlitzLegacy.Runtime.Assets.BlitzResources.LoadText(BlitzLegacy.Runtime.Assets.BlitzResources.TanksData));
            var tanks = new System.Collections.Generic.List<BattleTank>();
            BattleHud hud = null;
            try {
                foreach (var id in new[] { "ms-1", "t-34", "is-7", "su-100" })
                {
                    var tank = BattleTank.Spawn(root.transform, id, db.Get(id), root.transform,
                        new Bounds(Vector3.zero, new Vector3(1000,100,1000)), 0);
                    tanks.Add(tank);
                }
                hud = BattleHud.Create(null, null, true, 4);
                hud.SetTeamRoster(tanks, tanks);
                Assert.AreEqual(8, hud.TeamIconCount);
                // The scores count the tanks each side still has.
                Assert.AreEqual("4", hud.AllyScoreLabel.text);
                Assert.AreEqual("4", hud.EnemyScoreLabel.text);
                Assert.AreEqual(BlitzLegacy.Runtime.Ui.GodotRect.TopLeftPivot,
                    ((RectTransform)hud.Root.Find("total_tanks/box_ally")).pivot);
                Assert.AreEqual(BlitzLegacy.Runtime.Ui.GodotRect.TopLeftPivot,
                    ((RectTransform)hud.Root.Find("total_tanks/bg_time")).pivot);
                var allies = hud.Root.Find("total_tanks/box_ally");
                var enemies = hud.Root.Find("total_tanks/box_enemy");
                Assert.AreEqual("battle_tank_icon", allies.GetChild(0).GetChild(0).GetComponent<UnityEngine.UI.Image>().sprite.name);
                Assert.AreEqual("battle_tank_icon_08", enemies.GetChild(0).GetChild(0).GetComponent<UnityEngine.UI.Image>().sprite.name);
                tanks[1].Health.ReceiveDamageBasic(100000);
                hud.SetTeamTankAlive(false, 1, false);
                Assert.AreEqual(4, allies.childCount);
                Assert.AreEqual(4, enemies.childCount);
                var dead = allies.GetChild(0).GetChild(0).GetComponent<UnityEngine.UI.Image>();
                Assert.IsTrue(dead.gameObject.activeInHierarchy);
                Assert.AreEqual("battle_tank_icon_015", dead.sprite.name);
                Assert.AreEqual("battle_tank_icon_015", enemies.GetChild(3).GetChild(0).GetComponent<UnityEngine.UI.Image>().sprite.name);
                Assert.AreEqual("3", hud.AllyScoreLabel.text);
                Assert.AreEqual("3", hud.EnemyScoreLabel.text);
                // The battle-ending kill is reported before Health settles: the
                // roster event alone must blacken the icon and drop the score.
                hud.MarkTankDestroyed(tanks[0]);
                Assert.AreEqual("2", hud.AllyScoreLabel.text);
                Assert.AreEqual(4, allies.childCount);
                Assert.IsNotNull(hud.Root.Find("total_tanks/bg1"));
                Assert.IsNotNull(hud.Root.Find("total_tanks/bg2"));
                Assert.IsNotNull(hud.Root.Find("total_tanks/bg_time"));
            } finally {
                if (hud != null) Object.DestroyImmediate(hud.gameObject);
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void Clock_CountsDownFromTheLimitAndShowsDashesWhenEndless()
        {
            var hud = BattleHud.Create(null, null, true);
            try
            {
                hud.SetTimeLimit(420f);
                hud.SetBattleTime(0f);
                Assert.AreEqual("7:00", hud.ClockText);
                hud.SetBattleTime(3f);
                Assert.AreEqual("6:57", hud.ClockText);
                hud.SetBattleTime(500f);
                Assert.AreEqual("0:00", hud.ClockText);
                Assert.AreEqual(0f, hud.TimeRemaining);
                hud.SetTimeLimit(0f);
                Assert.AreEqual("-:--", hud.ClockText);
                Assert.AreEqual(-1f, hud.TimeRemaining);
            }
            finally
            {
                Object.DestroyImmediate(hud.gameObject);
            }
        }
    }
}
