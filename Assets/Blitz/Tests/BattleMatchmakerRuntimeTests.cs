using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Rng;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Tanks.Ai;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>Runtime contracts for the roster and bot loadout source paths.</summary>
    public sealed class BattleMatchmakerRuntimeTests
    {
        private GameObject _root;

        [TearDown]
        public void TearDown()
        {
            if (_root != null)
            {
                Object.DestroyImmediate(_root);
            }
        }

        [TestCase(SettingsData.TeamSize1x1, 1)]
        [TestCase(SettingsData.TeamSize2x2, 2)]
        [TestCase(SettingsData.TeamSize5x5, 5)]
        [TestCase(SettingsData.TeamSize7x7, 7)]
        [TestCase(SettingsData.TeamSize10x10, 10)]
        public void Settings_RoundTripSourceTeamSize(int setting, int expected)
        {
            var source = new SettingsData { MatchmakerTeamSize = setting };
            var loaded = SettingsData.FromJson(source.ToJson());

            Assert.That(loaded.MatchmakerTeamSize, Is.EqualTo(setting));
            Assert.That(BattleSession.TeamSizeFor(loaded), Is.EqualTo(expected));
        }

        [Test]
        public void BotAmmo_UsesTheSourceOneTwoAndThreeShellRatios()
        {
            Assert.That(BotLoadoutSelector.AmmoFor(Gun(100, 1)),
                Is.EqualTo(new[] { 100 }));
            Assert.That(BotLoadoutSelector.AmmoFor(Gun(100, 2)),
                Is.EqualTo(new[] { 75, 25 }));
            Assert.That(BotLoadoutSelector.AmmoFor(Gun(100, 3)),
                Is.EqualTo(new[] { 40, 20, 40 }));
        }

        [Test]
        public void BotLoadout_AlwaysSelectsInstalledModulesAndFillsItsGun()
        {
            var card = new TankInfo { Name = "fixture" };
            card.Guns.Add(Gun(80, 3));
            card.Guns.Add(Gun(60, 2));
            card.Engines.Add(new EngineInfo());
            card.Engines.Add(new EngineInfo());
            card.Tracks.Add(new TracksInfo());
            card.Tracks.Add(new TracksInfo());

            for (var seed = 0; seed < 100; seed++)
            {
                var loadout = BotLoadoutSelector.Select(
                    card, AiSkillLevel.BlueArmy, new SeededRandomSource(seed));
                Assert.That(loadout.Gun, Is.InRange(0, card.Guns.Count - 1), "seed " + seed);
                Assert.That(loadout.Engine, Is.InRange(0, card.Engines.Count - 1), "seed " + seed);
                Assert.That(loadout.Tracks, Is.InRange(0, card.Tracks.Count - 1), "seed " + seed);
                Assert.That(loadout.Ammo.Length,
                    Is.EqualTo(card.Guns[loadout.Gun].Shells.Count), "seed " + seed);
                Assert.That(loadout.Ammo[0], Is.GreaterThan(0), "seed " + seed);
            }
        }

        [Test]
        public void Session_DefaultSettingsBuildASevenPerSideMirroredRoster()
        {
            Random.InitState(982451653);
            _root = new GameObject("BattleMatchmakerRuntimeTests");
            var session = BattleSession.Create(
                _root.transform, false, BattleWorld.DefaultMapId, "t-34", false, "Tester");

            Assert.That(session.MatchRoster, Is.Not.Null);
            Assert.That(session.MatchRoster.TeamSize, Is.EqualTo(7));
            Assert.That(session.Allies.Count, Is.EqualTo(7));
            Assert.That(session.Enemies.Count, Is.EqualTo(7));
            Assert.That(session.Bots.Count, Is.EqualTo(13));
            Assert.That(session.Participants.Count, Is.EqualTo(14));
            Assert.That(session.Player.Card.Name, Is.EqualTo("t-34"));
            Assert.That(session.Player.Blocked, Is.True);

            for (var slot = 0; slot < session.MatchRoster.TeamSize; slot++)
            {
                Assert.That(session.Allies[slot].Card.Level,
                    Is.EqualTo(session.MatchRoster.SlotTiers[slot]), "ally slot " + slot);
                Assert.That(session.Enemies[slot].Card.Level,
                    Is.EqualTo(session.MatchRoster.SlotTiers[slot]), "enemy slot " + slot);
            }
        }

        private static GunInfo Gun(int ammo, int shells)
        {
            var gun = new GunInfo
            {
                MaxAmmo = ammo,
                Reload = 5f,
                Dispersion = 0.4f,
            };
            for (var i = 0; i < shells; i++)
            {
                gun.Shells.Add(new ShellInfo
                {
                    Damage = 100f + i,
                    Penetration = 80f + i,
                });
            }
            return gun;
        }
    }
}
