using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Localization;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Ui;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    public sealed class TeamKillTests
    {
        private const double Now = 1_800_000_000;
        private GameObject _root;

        [TearDown] public void Cleanup() { if (_root != null) Object.DestroyImmediate(_root); }

        private BattleTank Tank(int team)
        {
            if (_root == null) { _root = new GameObject("team-kill-tests"); }
            var host = new GameObject("tank" + team);
            host.transform.SetParent(_root.transform, false);
            var tank = host.AddComponent<BattleTank>();
            typeof(BattleTank).GetProperty("Team").SetValue(tank, team);
            return tank;
        }

        [Test]
        public void FriendlyDamage_OnlineQuarterAndTenthRam_OfflineUntouched()
        {
            Assert.AreEqual(100, TeamKillPolicy.FriendlyDamage(400, DamageType.Shell, true));
            Assert.AreEqual(100, TeamKillPolicy.FriendlyDamage(400, DamageType.Explosion, true));
            Assert.AreEqual(40, TeamKillPolicy.FriendlyDamage(400, DamageType.Collision, true));
            Assert.AreEqual(400, TeamKillPolicy.FriendlyDamage(400, DamageType.Environment, true));
            Assert.AreEqual(400, TeamKillPolicy.FriendlyDamage(400, DamageType.Shell, false));
            Assert.AreEqual(400, TeamKillPolicy.FriendlyDamage(400, DamageType.Collision, false));
        }

        [Test]
        public void Offences_EscalateTwoDaysWeekMonthThenPermanent()
        {
            var r = new TeamKillRecord();
            Assert.IsFalse(TeamKillPolicy.ApplyBattle(r, 0, Now));
            TeamKillPolicy.ApplyBattle(r, 1499, Now);
            Assert.IsFalse(r.IsBlue(Now));
            TeamKillPolicy.ApplyBattle(r, 1, Now);
            Assert.AreEqual(1, r.Offences);
            Assert.AreEqual(Now + 2 * TeamKillPolicy.Day, r.BlueUntil);
            Assert.IsTrue(r.IsBlue(Now + TeamKillPolicy.Day));
            Assert.IsFalse(r.IsBlue(Now + 3 * TeamKillPolicy.Day));

            var later = Now + 10 * TeamKillPolicy.Day;
            TeamKillPolicy.ApplyBattle(r, 1500, later);
            Assert.AreEqual(2, r.Offences);
            Assert.AreEqual(later + 7 * TeamKillPolicy.Day, r.BlueUntil);
            TeamKillPolicy.ApplyBattle(r, 1500, later);
            Assert.AreEqual(later + 30 * TeamKillPolicy.Day, r.BlueUntil);
            Assert.IsFalse(r.Permanent);
            TeamKillPolicy.ApplyBattle(r, 1500, later);
            Assert.IsTrue(r.Permanent);
            Assert.IsTrue(r.IsBlue(later + 1000 * TeamKillPolicy.Day));
            Assert.AreEqual(6000, r.Total);
            Assert.AreEqual(0, r.Pending);
        }

        [Test]
        public void StrictWeek_StartsAtFourThousandAndKicksAboveTwoHundred()
        {
            var r = new TeamKillRecord();
            TeamKillPolicy.ApplyBattle(r, 3999, Now);
            Assert.IsFalse(r.Watched(Now));
            TeamKillPolicy.ApplyBattle(r, 1, Now);
            Assert.IsTrue(r.Watched(Now + 6 * TeamKillPolicy.Day));
            Assert.IsFalse(r.Watched(Now + 8 * TeamKillPolicy.Day));
            Assert.IsFalse(TeamKillPolicy.ShouldKick(true, 200));
            Assert.IsTrue(TeamKillPolicy.ShouldKick(true, 201));
            Assert.IsFalse(TeamKillPolicy.ShouldKick(false, 5000));
        }

        [Test]
        public void Record_SurvivesAccountJson()
        {
            var a = new AccountData();
            TeamKillPolicy.ApplyBattle(a.TeamKill, 4600, Now);
            var back = AccountData.FromJson(JsonNode.Parse(a.ToJson().ToString()));
            Assert.AreEqual(4600, back.TeamKill.Total);
            Assert.AreEqual(100, back.TeamKill.Pending);
            Assert.AreEqual(3, back.TeamKill.Offences);
            Assert.AreEqual(a.TeamKill.BlueUntil, back.TeamKill.BlueUntil, 1e-3);
            Assert.AreEqual(a.TeamKill.WatchUntil, back.TeamKill.WatchUntil, 1e-3);
            Assert.AreEqual(0, AccountData.FromJson(JsonNode.Parse("{}")).TeamKill.Total);
        }

        [Test]
        public void Hello_CarriesTeamKillStatus()
        {
            var hello = new BattleNetHello { TankId = "t-34", TeamKiller = true, TeamKillWatched = true, TeamKillPending = 321 };
            var back = BattleNetHello.FromBytes(hello.ToBytes());
            Assert.IsTrue(back.TeamKiller);
            Assert.IsTrue(back.TeamKillWatched);
            Assert.AreEqual(321, back.TeamKillPending);
        }

        [Test]
        public void Tracker_TurnsBlueMidBattle_IgnoresBlueVictimsAndEnemies_RequestsKick()
        {
            var damage = new BattleTeamDamage();
            var shooter = Tank(0);
            var ally = Tank(0);
            var enemy = Tank(1);
            damage.Register(shooter, false, 1400, true);
            BattleTank kicked = null;
            damage.KickRequested += t => kicked = t;

            Assert.IsFalse(damage.Record(shooter, enemy, 500, true));
            Assert.IsFalse(damage.Record(shooter, shooter, 500, true));
            Assert.IsFalse(damage.Record(ally, shooter, 500, false), "bots never collect team damage");
            Assert.IsTrue(damage.Record(shooter, ally, 150, true));
            Assert.IsTrue(shooter.TeamKiller, "1400 pending + 150 crosses 1500");
            Assert.IsNull(kicked);
            Assert.IsTrue(damage.Record(shooter, ally, 60, true));
            Assert.AreSame(shooter, kicked, "strict week: over 200 in one battle");
            Assert.AreEqual(210, damage.BattleDamage(shooter));

            // Damage to a blue team-mate is not team damage.
            var other = Tank(0);
            damage.Register(other, false, 0, false);
            Assert.IsFalse(damage.Record(other, shooter, 900, true));
            Assert.AreEqual(0, damage.BattleDamage(other));
        }

        [Test]
        public void AtgmMarker_OnlyForOwnRockets()
        {
            Assert.IsTrue(BattleAtgmPresentation.ShowsMarker(null));
            Assert.IsFalse(BattleAtgmPresentation.ShowsMarker(Tank(1)), "no battle: nobody drives it locally");
        }

        [Test]
        public void StatisticsText_ShowsStatusExpiryAndStrictWeek()
        {
            var lang = LocalizationTable.Parse(Resources.Load<TextAsset>("Data/lang").text);
            lang.Language = "ru";
            var r = new TeamKillRecord();
            StringAssert.StartsWith("Статус тимкиллера: нет", AccountStatisticsScreen.TeamKillText(r, Now, lang));
            TeamKillPolicy.ApplyBattle(r, 4000, Now);
            var text = AccountStatisticsScreen.TeamKillText(r, Now, lang);
            StringAssert.Contains(AccountStatisticsScreen.Date(r.BlueUntil), text);
            StringAssert.Contains("Строгий режим до " + AccountStatisticsScreen.Date(r.WatchUntil), text);
            StringAssert.Contains("всего 4 000", text);
            r.Permanent = true;
            StringAssert.StartsWith("Тимкиллер (голубой ник) навсегда", AccountStatisticsScreen.TeamKillText(r, Now, lang));
        }
    }
}
