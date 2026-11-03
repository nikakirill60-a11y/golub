using System;
using System.Globalization;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Ui;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// One battle HUD for every mode: the same standard parts and hangar slots everywhere,
    /// and a mode only adds what its <see cref="BattleHudMode"/> answers for.
    /// </summary>
    public sealed class BattleHudStandardTests
    {
        /// <summary>The hangar loadout: repair kit, boost and adrenaline in the third slot.</summary>
        private static readonly string[] Hangar =
        {
            ConsumableCatalog.RepairKit, ConsumableCatalog.Afterburning, ConsumableCatalog.Berserk,
        };

        private BattleSession _session;
        private GameObject _viewport;
        private float _scale;

        [SetUp]
        public void Setup()
        {
            _scale = Time.timeScale;
            Time.timeScale = 1f;
        }

        [TearDown]
        public void Cleanup()
        {
            if (_session != null) { Object.DestroyImmediate(_session.gameObject); }
            if (_viewport != null) { Object.DestroyImmediate(_viewport); }
            Time.timeScale = _scale;
        }

        private void Create(BattleMode mode, string tankId = "t-34")
        {
            if (_session != null) { Object.DestroyImmediate(_session.gameObject); }
            // No saved rack: the seat takes the stock one, exactly like a battle without a save.
            var save = new TankSaveData { Name = tankId };
            save.ShellsAmounts.Clear();
            save.Consumables.AddRange(Hangar);
            _session = BattleSession.Create(null, false, tankId: tankId, autoStart: true, playerTank: save, mode: mode);
        }

        private static void Kill(BattleTank tank)
        {
            for (var i = 0; i < 2 && !tank.Dead; i++)
            {
                var damage = tank.Health.MaxHealth * 10;
                tank.ReceiveDamage(new DamageInfo { Dmg = damage, DmgBase = damage, DmgType = DamageType.Ability });
            }
            Assert.IsTrue(tank.Dead);
        }

        private static int Score(Text label)
        {
            return int.Parse(label.text, CultureInfo.InvariantCulture);
        }

        [TestCase(BattleMode.Normal)]
        [TestCase(BattleMode.Gravity)]
        [TestCase(BattleMode.Uprising)]
        [TestCase(BattleMode.BigBoss)]
        public void EveryModeShowsTheStandardHudWithTheHangarLoadout(BattleMode mode)
        {
            Create(mode);
            var bar = _session.Controls.Consumables;
            Assert.True(bar.gameObject.activeInHierarchy, mode.ToString());
            for (var slot = 0; slot < Hangar.Length; slot++)
            {
                var icon = bar.GetIcon(slot);
                var what = mode + " slot " + slot;
                Assert.True(icon.gameObject.activeSelf, what);
                Assert.AreEqual(Hangar[slot], icon.Consumable.Info.Id, what);
                Assert.True(icon.IconImage.enabled, what);
                Assert.AreEqual("consumable_" + Hangar[slot], icon.IconImage.sprite.name, what);
            }

            var root = _session.Hud.Root;
            Assert.AreSame(root, _session.Hud.StatusRow.transform.parent, mode.ToString());
            Assert.True(_session.Scoreboard.transform.IsChildOf(root), mode.ToString());
            Assert.True(_session.Lamp.transform.IsChildOf(root), mode.ToString());
            Assert.NotNull(_session.PauseMenu, mode.ToString());
        }

        [TestCase(BattleMode.Normal, typeof(BattleHudMode), false)]
        [TestCase(BattleMode.Gravity, typeof(GravityHudMode), true)]
        [TestCase(BattleMode.Uprising, typeof(UprisingHudMode), true)]
        [TestCase(BattleMode.BigBoss, typeof(BigBossHudMode), true)]
        public void TheModeAddsOnlyItsOwnParts(BattleMode mode, Type contract, bool modeSlot)
        {
            Create(mode);
            foreach (var tank in _session.BattleTanks())
            {
                Assert.AreEqual(contract, BattleHudMode.Of(tank).GetType(), tank.name);
            }

            var bar = _session.Controls.Consumables;
            Assert.AreEqual(modeSlot, bar.ModeIcon != null, "cons_jump");
            var root = _session.Hud.Root;
            Assert.AreEqual(mode == BattleMode.Uprising ? 1 : 0, root.GetComponentsInChildren<UprisingBattleUi>(true).Length);
            Assert.AreEqual(mode == BattleMode.BigBoss ? 1 : 0, root.GetComponentsInChildren<BigBossBattleUi>(true).Length);
        }

        [Test]
        public void GravityHeaviesGetNoModeSlot()
        {
            Create(BattleMode.Gravity, "maus");
            Assert.IsInstanceOf<GravityHudMode>(BattleHudMode.Of(_session.Player));
            Assert.Null(BattleHudMode.Of(_session.Player).Ability);
            Assert.Null(_session.Controls.Consumables.ModeIcon);
        }

        [Test]
        public void TheStandardBattleKeepsItsDefaults()
        {
            Create(BattleMode.Normal);
            var standard = BattleHudMode.Of(_session.Player);
            Assert.AreSame(BattleHudMode.Standard, standard);
            Assert.Null(standard.Ability);
            Assert.Null(standard.StatusFont);
            Assert.False(standard.Respawns);
            Assert.True(standard.CanSpectate);
            Assert.False(standard.ModalOpen);
            Assert.AreEqual(-1, standard.HeaderLives);
            Assert.False(_session.Controls.ModalOpen);

            var row = _session.Hud.StatusRow;
            row.Refresh();
            Assert.AreEqual(0, row.Ids.Count);
            Assert.False(row.gameObject.activeSelf, "the status row stays empty and hidden");
        }

        [Test]
        public void TheHeaderCountsTanksUnlessTheModeCountsLives()
        {
            Create(BattleMode.Normal);
            Assert.AreEqual(_session.Allies.Count, Score(_session.Hud.AllyScoreLabel));
            Assert.AreEqual(_session.Enemies.Count, Score(_session.Hud.EnemyScoreLabel));

            Create(BattleMode.Uprising);
            var hud = _session.Hud;
            var allies = 0;
            foreach (var tank in _session.Allies) { allies += tank.Uprising.State.RespawnsLeft + 1; }
            Assert.Greater(allies, _session.Allies.Count, "every banked life counts");
            Assert.AreEqual(allies, Score(hud.AllyScoreLabel));

            var enemy = _session.Enemies[0];
            var enemies = Score(hud.EnemyScoreLabel);
            Kill(enemy);
            hud.RefreshTeamRoster();
            Assert.AreEqual(enemy.Uprising.State.RespawnsLeft, BattleHudMode.Of(enemy).HeaderLives);
            Assert.AreEqual(enemies - 1, Score(hud.EnemyScoreLabel), "a death spends one life");
        }

        [Test]
        public void RespawnFrameWaitsFadesRisesAndFitsTheViewport()
        {
            _viewport = new GameObject("viewport", typeof(RectTransform));
            var viewport = (RectTransform)_viewport.transform;
            viewport.sizeDelta = new Vector2(400f, 300f);
            var size = new Vector2(620f, 444f);

            var frame = BattleRespawnFrame.Create(viewport, "respawn", 7);
            Assert.False(frame.Root.gameObject.activeSelf);
            Assert.AreEqual(0f, frame.Alpha);
            Assert.AreSame(frame.Root, frame.Content.parent);
            Assert.AreEqual(7, frame.Root.GetComponent<Canvas>().sortingOrder);
            Assert.NotNull(frame.Root.GetComponent<GraphicRaycaster>());
            Assert.Null(BattleRespawnFrame.Create(viewport, "plain").Root.GetComponent<Canvas>());

            // The death plays out first: shown, swallowing the pointer, still clear and low.
            Assert.AreEqual(0f, frame.Present(true, 0.5, size, true));
            Assert.True(frame.Root.gameObject.activeSelf);
            Assert.True(frame.Group.blocksRaycasts);
            Assert.False(frame.Group.interactable);
            Assert.AreEqual(-BattleRespawnFrame.Rise, frame.Content.anchoredPosition.y, 1e-3f);

            // Faded in: in place, fitted into the viewport, taking input when interactive.
            Assert.AreEqual(1f, frame.Present(true, 2.0, size, true));
            Assert.True(frame.Group.interactable);
            Assert.AreEqual(0f, frame.Content.anchoredPosition.y, 1e-3f);
            var fit = Mathf.Min(
                (400f - BattleRespawnFrame.Margin.x) / size.x, (300f - BattleRespawnFrame.Margin.y) / size.y);
            Assert.AreEqual(fit, frame.Content.localScale.x, 1e-4f);
            Assert.AreEqual(1f, frame.Present(true, 2.0, new Vector2(100f, 80f), true));
            Assert.AreEqual(1f, frame.Content.localScale.x, 1e-4f, "small content keeps its size");
            Assert.AreEqual(1f, frame.Present(true, 2.0, size, false));
            Assert.False(frame.Group.interactable, "a frame that only informs never takes input");

            Assert.AreEqual(0f, frame.Present(false, 2.0, size, true));
            Assert.False(frame.Root.gameObject.activeSelf);
            Assert.False(frame.Group.blocksRaycasts);
            Assert.AreEqual(0f, frame.Alpha);
        }
    }
}
