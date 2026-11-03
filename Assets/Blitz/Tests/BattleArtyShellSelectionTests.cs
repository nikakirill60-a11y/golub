using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BlitzLegacy.Tests
{
    public sealed class BattleArtyShellSelectionTests
    {
        private BattleTank _tank;
        private BattleCamera _view;
        private void Create(string id)
        {
            var db=TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
            _tank=BattleTank.Spawn(null,"arty shell regression",db.Get(id),null,new Bounds(),0);
            _tank.GetComponent<Rigidbody>().isKinematic=true;
            _view=BattleCamera.Create(_tank,false);
            _tank.View=_view;
        }
        [TearDown]
        public void Cleanup()
        {
            if (_view!=null)
            {
                Object.DestroyImmediate(_view.Sniper.transform.parent.gameObject);
                Object.DestroyImmediate(_view.Arty.transform.parent.gameObject);
                Object.DestroyImmediate(_view.gameObject);
            }
            if (_tank!=null) Object.DestroyImmediate(_tank.gameObject);
        }
        [TestCase("g14_kpfpz_70r",0,false)]
        [TestCase("g14_kpfpz_70r",1,true)]
        [TestCase("g14_kpfpz_70r",2,false)]
        [TestCase("t49_rocket",0,true)]
        [TestCase("t49_rocket",1,true)]
        public void OnlySelectedMissileCanEnter(string id,int shell,bool allowed)
        {
            Create(id);_tank.SelectShell(shell);
            Assert.AreEqual(allowed,_view.ArtyAvailable);
            _view.SetArtyMode(true);
            Assert.AreEqual(allowed,_view.ArtyMode);
            Assert.AreEqual(allowed,_view.Arty.enabled);
            Assert.AreEqual(allowed,_tank.Turret.LobAim);
        }
        [TestCase("g14_kpfpz_70r",1,0)]
        [TestCase("g14_kpfpz_70r",1,2)]
        public void ChangingAmmoImmediatelyRestoresNormalCamera(string id,int from,int to)
        {
            Create(id);_tank.SelectShell(from);_view.SetArtyMode(true);
            Assert.IsTrue(_view.ArtyMode);
            var exits=0;_view.ArtyModeSwitched+=on=>{if(!on) exits++;};
            _tank.SelectShell(to);
            Assert.AreEqual(to,_tank.ShellId);
            Assert.IsFalse(_view.ArtyMode);
            Assert.IsFalse(_view.Arty.enabled);
            Assert.IsTrue(_view.Arcade.enabled);
            Assert.IsFalse(_tank.Turret.LobAim);
            Assert.IsFalse(_view.GetComponentInChildren<BattleArtyReticle>(true).gameObject.activeSelf);
            Assert.AreEqual(1,exits);
            _view.SetArtyMode(true);
            Assert.AreEqual(_tank.CurrentShell().Atgm,_view.ArtyMode);
        }
        [TestCase(0,1)]
        [TestCase(1,0)]
        public void SwitchingBetweenMissilesPreservesOverheadAim(int from,int to)
        {
            Create("t49_rocket");_tank.SelectShell(from);_view.SetArtyMode(true);
            var aim=_view.AimTarget;
            var switches=0;_view.ArtyModeSwitched+=on=>switches++;
            _tank.SelectShell(to);
            Assert.AreEqual(to,_tank.ShellId);
            Assert.IsTrue(_view.ArtyAvailable);
            Assert.IsTrue(_view.ArtyMode);
            Assert.IsTrue(_view.Arty.enabled);
            Assert.IsFalse(_view.Arcade.enabled);
            Assert.IsTrue(_tank.Turret.LobAim);
            Assert.IsTrue(_view.GetComponentInChildren<BattleArtyReticle>(true).gameObject.activeSelf);
            Assert.AreEqual(aim,_view.AimTarget);
            Assert.AreEqual(0,switches);
        }
        [Test]
        public void ReselectingSameClampedSlotKeepsArty()
        {
            Create("t49_rocket");_tank.SelectShell(1);_view.SetArtyMode(true);
            _tank.SelectShell(99);
            Assert.IsTrue(_view.ArtyMode);
        }
        [Test]
        public void ChangingObservedTankClearsOldLobState()
        {
            Create("t49_rocket");_view.SetArtyMode(true);_view.Tank=null;
            Assert.IsFalse(_view.ArtyMode);Assert.IsFalse(_tank.Turret.LobAim);
            Assert.IsFalse(_view.ArtyAvailable);
        }
    }
}
