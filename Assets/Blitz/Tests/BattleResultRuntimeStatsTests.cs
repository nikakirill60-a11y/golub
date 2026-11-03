using System;
using System.Reflection;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BlitzLegacy.Tests
{
    public sealed class BattleResultRuntimeStatsTests
    {
        private GameObject _root;
        private BattleSession _session;
        private BattleTank _attacker, _victim;
        private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        [SetUp]
        public void Setup()
        {
            _root = new GameObject("result_stats");
            _session = _root.AddComponent<BattleSession>();
            var db = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
            var bounds = new Bounds(Vector3.zero, new Vector3(1000,100,1000));
            _attacker = BattleTank.Spawn(_root.transform, "attacker", db.Get("t-34"), _root.transform, bounds, 0);
            _victim = BattleTank.Spawn(_root.transform, "victim", db.Get("ms-1"), _root.transform, bounds, 1);
            Register(_attacker); Register(_victim);
        }

        private void Register(BattleTank tank)
        {
            typeof(BattleSession).GetMethod("RegisterParticipant", Private).Invoke(_session,
                new object[] { tank, tank.name, tank == _attacker });
        }

        [TearDown]
        public void Teardown() { Object.DestroyImmediate(_root); }

        [Test]
        public void Damage_DeduplicatesShellHitsExcludesFireAndRamAndTruncatesRepairPerEvent()
        {
            var damage = typeof(BattleSession).GetMethod("OnDamage", Private);
            _victim.Card.RepairCost = .6f;
            foreach (var kind in new[] { DamageType.Shell, DamageType.Shell, DamageType.Fire, DamageType.Collision })
                damage.Invoke(_session, new object[] { _victim, new DamageInfo {
                    Dmg = 1, DmgBase = 1, ShotId = kind == DamageType.Shell ? 42 : 0,
                    AttackerTank = _attacker, DmgType = kind } });
            var attacker = _session.StatsFor(_attacker);
            Assert.AreEqual(4, attacker.Damage);
            Assert.AreEqual(1, attacker.Hits);
            Assert.AreEqual(4, _session.StatsFor(_victim).DamageTaken);
            Assert.AreEqual(0, _session.StatsFor(_victim).RepairCost);
        }

        [Test]
        public void Piercer_CountsShellDamageEventsButNotBlocksOrFire()
        {
            var damage=typeof(BattleSession).GetMethod("OnDamage",Private);
            for(var i=0;i<20;i++)damage.Invoke(_session,new object[]{_victim,new DamageInfo{Dmg=1,DmgBase=1,ShotId=500+i,AttackerTank=_attacker,DmgType=DamageType.Shell}});
            damage.Invoke(_session,new object[]{_victim,new DamageInfo{Dmg=1,DmgBase=1,AttackerTank=_attacker,DmgType=DamageType.Fire}});
            Assert.AreEqual(2,_session.StatsFor(_attacker).PiercerStreaks);
            Assert.AreEqual(0,_session.StatsFor(_attacker).PiercerCurrent);
        }
        [Test]
        public void ModuleCritSignal_UpdatesAccountAwardCounter()
        {
            var module=_attacker.Modules.Get(TankModuleIds.Tracks);
            var signal=(Action<string>)typeof(TankModule).GetField("OnCrit",Private).GetValue(module);
            for(var i=0;i<5;i++)signal(TankModuleIds.Tracks);
            Assert.AreEqual(5,_session.StatsFor(_attacker).ModuleCrits);
        }
        [Test]
        public void BotNicknames_DeduplicateRepeatedSourceEntries()
        {
            var draw=typeof(BattleSession).GetMethod("DrawBotNames",BindingFlags.Static|BindingFlags.NonPublic);
            var names=(System.Collections.Generic.List<string>)draw.Invoke(null,new object[]{300,new BlitzLegacy.Core.Rng.SeededRandomSource(19)});
            Assert.AreEqual(names.Count,new System.Collections.Generic.HashSet<string>(names).Count);
        }
        [Test]
        public void Ammunition_CostsEachLaunchedShellTypeNotTheFinalSelection()
        {
            _attacker.GunInfo.Shells[0].Cost = 11;
            _attacker.GunInfo.Shells[1].Cost = 73;
            var fired = (Action<bool,int>)typeof(BattleTank).GetField("ProjectileFired", Private).GetValue(_attacker);
            fired(true, 0); fired(true, 1); fired(false, 1);
            Assert.AreEqual(3, _session.StatsFor(_attacker).Shots);
            Assert.AreEqual(157, _session.StatsFor(_attacker).AmmoCost);
        }
    }
}
