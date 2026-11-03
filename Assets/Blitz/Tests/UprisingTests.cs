using System;
using System.Linq;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Localization;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Tanks.Ai;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Ui;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    public sealed class UprisingTests
    {
        private UprisingCatalog _catalog;
        private UprisingState _state;
        [SetUp] public void Setup() { _catalog = new UprisingCatalog(BlitzResources.LoadText("uprising_abilities")); _state=new UprisingState(_catalog,"berserk"); }
        [Test] public void ModeMenuUsesSourceProjectThemeAndButtonStyles()
        {
            Assert.IsNotNull(Resources.Load<Font>("Uprising/menu_default"));
            StringAssert.Contains("gui/theme/custom",BlitzResources.LoadText("uprising_source_manifest"));
            foreach(var state in new[]{"normal","hover","pressed","focus"})Assert.IsNotNull(Resources.Load<Texture2D>("Uprising/Icons/menu_close_"+state));
        }
        [Test] public void CatalogueIncludesSevenActivesFourteenEnabledPassives()
        {
            Assert.AreEqual(22,_catalog.Abilities.Count);
            Assert.AreEqual(7,_catalog.Abilities.Count(x=>x["kind"].AsInt()==0));
            Assert.AreEqual(14,_catalog.Abilities.Count(x=>x["kind"].AsInt()==1 && x["wired"].AsBool()));
            Assert.IsFalse(_state.Grant("enemyReloadTime"));
        }
        [TestCase(TankClass.Light)] [TestCase(TankClass.Medium)] [TestCase(TankClass.Heavy)] [TestCase(TankClass.TankDestroyer)]
        public void DeterministicClassActive(TankClass cls)
        { var a=_catalog.ForTank("ms-1",cls);Assert.AreEqual(a,_catalog.ForTank("ms-1",cls));Assert.IsNotEmpty(a); }
        [TestCase(30)] [TestCase(60)] [TestCase(144)] public void ElixirIsFrameRateIndependent(int fps)
        { for(var i=0;i<fps*60;i++)_state.Accrue(1d/fps);Assert.AreEqual(60,_state.Elixir); }
        [Test] public void DamagePaysOnlyRealShellDamageAndOncePerShot()
        {
            _state.RewardDamage(60,123,DamageType.Shell);_state.RewardDamage(60,123,DamageType.Explosion);
            Assert.AreEqual(7,_state.Elixir);_state.RewardDamage(80,124,DamageType.Shell);Assert.AreEqual(14,_state.Elixir);
            _state.RewardDamage(500,125,DamageType.Ability);_state.RewardDamage(500,0,DamageType.Collision);Assert.AreEqual(14,_state.Elixir);
        }
        [Test] public void ThreeLivesAndIdempotentDeath()
        {
            Assert.IsTrue(_state.OnDeath(0,n=>0));Assert.IsFalse(_state.OnDeath(1,n=>0));Assert.AreEqual(100,_state.Elixir);
            Assert.IsFalse(_state.Respawn(9,true));Assert.IsTrue(_state.Respawn(10,true));Assert.AreEqual(1,_state.RespawnsLeft);
            Assert.IsTrue(_state.OnDeath(20,n=>0));Assert.IsTrue(_state.Respawn(30,true));Assert.AreEqual(0,_state.RespawnsLeft);
            Assert.IsFalse(_state.OnDeath(40,n=>0));Assert.IsFalse(_state.Respawning);Assert.AreEqual(300,_state.Elixir);
            _state.OnDeath(50,n=>0);Assert.AreEqual(300,_state.Elixir);
        }
        [Test] public void BattleEndPreventsRevival() { _state.OnDeath(0,n=>0);Assert.IsFalse(_state.Respawn(11,false)); }
        [Test] public void OffersStableDistinctFamiliesAndExactRaritySlots()
        {
            _state.OnDeath(0,n=>0);var families=_state.Offer.Select(UprisingCatalog.Family).ToArray();Assert.AreEqual(3,families.Distinct().Count());
            for(var i=0;i<3;i++)Assert.AreEqual(i+1,_catalog.Rarity(_state.Offer[i]));
        }
        [Test] public void RefundOnlyDuringSameCountdownAndBeforeDeadline()
        {
            _state.OnDeath(0,n=>0);var id=_state.Offer[0];Assert.IsTrue(_state.Toggle(id,1,true));Assert.IsTrue(_state.Has(id));Assert.AreEqual(0,_state.Elixir);
            Assert.IsTrue(_state.Toggle(id,2,true));Assert.IsFalse(_state.Has(id));Assert.AreEqual(100,_state.Elixir);
            Assert.IsTrue(_state.Toggle(id,3,true));Assert.IsFalse(_state.Toggle(id,10,true));_state.Respawn(10,true);
            Assert.IsTrue(_state.Has(id));Assert.IsFalse(_state.Toggle(id,11,true));Assert.IsEmpty(_state.Pending);
        }
        [Test] public void RejectUnknownInactiveUnaffordableOrNotOfferedPurchase()
        { _state.AddElixir(1000);Assert.IsFalse(_state.CanBuy("unknown"));Assert.IsFalse(_state.CanBuy("berserk"));_state.OnDeath(0,n=>0);Assert.IsFalse(_state.Toggle("ghost",1,false));Assert.IsFalse(_state.Toggle("enemyReloadTime",1,true)); }
        [Test] public void HigherGradesReplaceRatherThanStack()
        { _state.Grant("damageBoost1");_state.Grant("damageBoost2");Assert.AreEqual(1.4f,_state.Outgoing(DamageType.Shell,0));Assert.AreEqual(1,_state.Outgoing(DamageType.Ability,0));Assert.IsFalse(_state.CanBuy("damageBoost1")); }
        [Test] public void BattleTranceTradeAndReload()
        { _state.ActiveUntil=10;Assert.AreEqual(1.5f,_state.Outgoing(DamageType.Shell,1));Assert.AreEqual(.7f,_state.ReloadMultiplier(1));_state.Grant("reloadBoost2");Assert.AreEqual(.455f,_state.ReloadMultiplier(1),.00001f); }
        [Test] public void ShieldAndLastStandAreOneShotPerLife()
        { _state.ActiveUntil=10;_state.ShieldArmed=true;Assert.AreEqual(0,_state.Absorb(200,100,1));Assert.AreEqual(200,_state.Absorb(200,100,2));_state.Grant("comeBackKit");Assert.AreEqual(99,_state.Absorb(200,100,3));Assert.AreEqual(200,_state.Absorb(200,100,4)); }
        [Test] public void BotActivesRequireTacticalOpportunities()
        {
            var c=new UprisingAiDecision.Context {HasTarget=true,Distance=300,HealthFraction=1,SelfArmor=80,TargetArmor=100,CanPenetrate=true,State=AiStateKind.Rollout};
            foreach(var id in new[]{"moonwalk","pretendDead","berserk","vortex","huntersMark","curse"})
            {c.Ability=id;Assert.IsFalse(UprisingAiDecision.ShouldUse(c),id+" must not fire after spawn without a tactical reason");}
            c.Ability="berserk";c.Distance=70;Assert.IsTrue(UprisingAiDecision.ShouldUse(c));
            c.Ability="huntersMark";c.Distance=200;Assert.IsTrue(UprisingAiDecision.ShouldUse(c));
        }
        [Test] public void MoonwalkChoosesRetreatOrFavoredRam()
        {
            var c=new UprisingAiDecision.Context {Ability="moonwalk",HasTarget=true,Distance=60,HealthFraction=.4f,SelfArmor=80,TargetArmor=100};
            Assert.IsTrue(UprisingAiDecision.ShouldUse(c),"low-health bot should cloak to retreat");
            c.HealthFraction=1;c.SelfArmor=130;Assert.IsTrue(UprisingAiDecision.ShouldUse(c),"armoured bot should cloak for a ram");
            c.SelfArmor=80;c.HasRamming=true;Assert.IsTrue(UprisingAiDecision.ShouldUse(c),"Wolf Strike should enable the ram scenario");
            c.HasRamming=false;Assert.IsFalse(UprisingAiDecision.ShouldUse(c));
        }
        [Test] public void ThanatosVortexAndBerserkUseCombatSignals()
        {
            var c=new UprisingAiDecision.Context {Ability="pretendDead",HasTarget=true,Distance=20,ClosingSpeed=7,HealthFraction=1};
            Assert.IsTrue(UprisingAiDecision.ShouldUse(c),"Thanatos should answer an incoming ram");
            c.Ability="vortex";c.Distance=110;c.ClosingSpeed=0;c.TargetClass=TankClass.Heavy;c.CanPenetrate=false;Assert.IsTrue(UprisingAiDecision.ShouldUse(c),"Vortex should open an otherwise unpenetrable target");
            c.CanPenetrate=true;c.TargetClass=TankClass.Light;c.TargetSpeed=9;Assert.IsTrue(UprisingAiDecision.ShouldUse(c),"Vortex should stop a circling light tank");
            c.Ability="berserk";c.TargetClass=TankClass.Heavy;c.TargetSpeed=0;c.Distance=120;c.State=AiStateKind.Brawl;Assert.IsTrue(UprisingAiDecision.ShouldUse(c),"Berserk should start with a committed brawl");
        }
        [Test] public void LocalizationAndArtworkCoverEveryAbility()
        {
            var lang=LocalizationTable.Parse(BlitzResources.LoadText("lang"));
            foreach(var a in _catalog.Abilities)
            {var id=a["id"].AsString();Assert.IsNotNull(Resources.Load<Texture2D>("Uprising/Icons/"+id),id);Assert.IsTrue(lang.Has("uprising:ability/"+id+"/name"),id);Assert.IsTrue(lang.Has("uprising:ability/"+id+"/desc"),id);}
            Assert.IsNotNull(Resources.Load<Font>("Uprising/arialmb"));Assert.IsNotNull(Resources.Load<Texture2D>("Uprising/Icons/card"));Assert.IsNotNull(Resources.Load<Texture2D>("Uprising/Icons/selected"));
        }
        [Test] public void OriginalFxManifestContainsAllRawScenesAndNoGodotParticles()
        {
            var manifest=JsonNode.Parse(BlitzResources.LoadText("uprising_source_manifest"));Assert.AreEqual("11.20.0",manifest["sourceVersion"].AsString());
            foreach(var e in manifest["effects"].Fields)Assert.IsNotNull(Resources.Load<TextAsset>(e.Value["resource"].AsString()),e.Key);
            foreach(var s in manifest["sources"].Items)
            {Assert.IsFalse(s["source"].AsString().StartsWith("assets/particles/",StringComparison.Ordinal));Assert.AreEqual(64,s["sha256"].AsString().Length);}
        }
        [Test] public void ModeSelectorHasGravityCard()
        {
            var host=new GameObject("test");try
            {var ui=BattleModeScreen.Create(host.transform,BattleMode.Normal,null);Assert.IsNotNull(ui.transform.Find("mode_viewport/mode_cards/card_normal"));Assert.IsNotNull(ui.transform.Find("mode_viewport/mode_cards/card_gravity"));Assert.IsNotNull(ui.transform.Find("mode_viewport/mode_cards/card_uprising"));}
            finally {UnityEngine.Object.DestroyImmediate(host);}
        }
        [Test] public void NetworkSnapshotMirrorsHostStateWithLocalClock()
        {
            _state.AddElixir(900);Assert.IsTrue(_state.Grant("damageBoost1"));
            Assert.IsTrue(_state.OnDeath(100,n=>0));Assert.AreEqual(3,_state.Offer.Count);
            var buy=_state.Offer.First(x=>x!="" && _state.CanBuy(x));Assert.IsTrue(_state.Toggle(buy,101,true));
            _state.ActiveUntil=-1;_state.ReadyAt=130;_state.CloakUntil=-1;_state.ShieldArmed=true;
            var snap=_state.Capture(102);Assert.IsTrue(snap.Respawning);Assert.IsFalse(snap.AliveLife);
            var client=new UprisingState(_catalog,"berserk");client.Apply(snap,5000);
            Assert.AreEqual(_state.Elixir,client.Elixir);Assert.AreEqual(_state.LifeSerial,client.LifeSerial);
            Assert.AreEqual(_state.RespawnsLeft,client.RespawnsLeft);Assert.IsTrue(client.Respawning);
            Assert.AreEqual(5008,client.RespawnAt,1e-3);Assert.AreEqual(5028,client.ReadyAt,1e-3);
            Assert.IsFalse(client.Running(5000));Assert.IsTrue(client.ShieldArmed);
            CollectionAssert.AreEqual(_state.Offer,client.Offer);CollectionAssert.AreEquivalent(_state.Owned,client.Owned);
            Assert.AreEqual(_catalog.Cost(buy),client.Pending[buy]);
            // The mirrored state behaves like the host's: a refund toggle restores the elixir.
            var before=client.Elixir;Assert.IsTrue(client.Toggle(buy,5001,true));Assert.AreEqual(before+_catalog.Cost(buy),client.Elixir);
            Assert.IsTrue(_state.Respawn(110,true));var alive=_state.Capture(110);
            Assert.IsFalse(alive.Respawning);Assert.IsTrue(alive.AliveLife);Assert.Greater(alive.CloakIn,9.9f);
            client.Apply(alive,7000);Assert.IsFalse(client.Respawning);Assert.AreEqual(0,client.Offer.Count);Assert.IsTrue(client.Has(buy));
        }
        [Test] public void ModeScreenGlobeTogglesOnlineWithoutReplacingModes()
        {
            var host=new GameObject("globe_host");
            try
            {
                bool? online=null;
                var ui=BattleModeScreen.Create(host.transform,BattleMode.Uprising,null,false,v=>online=v);
                Assert.IsNull(ui.transform.Find("card_online"));Assert.IsNotNull(ui.transform.Find("mode_viewport/mode_cards/card_uprising"));
                var globe=ui.transform.Find("online_globe").GetComponent<UnityEngine.UI.Button>();
                globe.onClick.Invoke();Assert.AreEqual(true,online);globe.onClick.Invoke();Assert.AreEqual(false,online);
                Assert.IsNull(BattleModeScreen.Create(host.transform,BattleMode.Normal,null).transform.Find("online_globe"));
            }
            finally {UnityEngine.Object.DestroyImmediate(host);}
        }
        private static int PerkCost(string id)
        {
            return id == "duplet" || id == "splash" ? 400 : id == "lightning1" ? 200 : id.EndsWith("2", StringComparison.Ordinal) ? 200 : 100;
        }
        [Test] public void BotBuysThePerkItsClassFightsWithAndSkipsWhatItCannotAfford()
        {
            var offer = new[] { "damageBoost1", "ramming1", "engineBoost1", "splash" };
            Func<string, bool> any = id => true;
            Assert.AreEqual("ramming1", UprisingAiDecision.ChoosePerk(offer, TankClass.Heavy, false, any, PerkCost));
            Assert.AreEqual("engineBoost1", UprisingAiDecision.ChoosePerk(offer, TankClass.Light, false, any, PerkCost));
            Assert.AreEqual("damageBoost1", UprisingAiDecision.ChoosePerk(offer, TankClass.TankDestroyer, false, any, PerkCost));
            Assert.AreEqual("damageBoost1", UprisingAiDecision.ChoosePerk(offer, TankClass.Medium, true, any, PerkCost),
                "a sniper hits harder whatever its class");
            Assert.AreEqual("splash", UprisingAiDecision.ChoosePerk(offer, TankClass.Heavy, false, id => id != "ramming1", PerkCost));
            Assert.IsNull(UprisingAiDecision.ChoosePerk(offer, TankClass.Heavy, false, id => false, PerkCost));
            Assert.IsNull(UprisingAiDecision.ChoosePerk(null, TankClass.Heavy, false, any, PerkCost));
            Assert.AreEqual("ramming2", UprisingAiDecision.ChoosePerk(new[] { "ramming1", "ramming2" }, TankClass.Heavy, false, any, PerkCost),
                "the higher grade of a family wins the tie");
            Assert.AreEqual("ramming1", UprisingAiDecision.ChoosePerk(new[] { "ramming1", "lightning1" }, TankClass.Heavy, false, any, PerkCost),
                "a dearer perk never outweighs the class fit");
        }
    }
}
