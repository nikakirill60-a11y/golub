using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Replicated health on a network puppet: hits arrive as damage, a heal on
    /// the authority (Uprising's Return in Time) arrives as a restore.
    /// </summary>
    public sealed class BattleTankNetStateTests
    {
        private GameObject _root;

        [TearDown]
        public void TearDown()
        {
            if (_root != null) { Object.DestroyImmediate(_root); }
        }

        private BattleTank Puppet()
        {
            _root = new GameObject("BattleTankNetStateTests", typeof(Rigidbody));
            var tank = _root.AddComponent<BattleTank>();
            var database = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
            tank.Initialize(database.Get(BattleSession.DefaultTankId));
            tank.SetNetPuppet();
            return tank;
        }

        [Test]
        public void PuppetTakesTheHostHealthBackAfterAHeal()
        {
            var tank = Puppet();
            var max = tank.Health.MaxHealth;
            var damageEvents = 0;
            tank.Health.OnDamageTaken += _ => damageEvents++;

            tank.ApplyNetState(Vector3.zero, Quaternion.identity, 0f, 0f, max / 2);
            Assert.AreEqual(max / 2, tank.Health.Health);
            Assert.AreEqual(1, damageEvents);

            tank.ApplyNetState(Vector3.zero, Quaternion.identity, 0f, 0f, max - 1);
            Assert.AreEqual(max - 1, tank.Health.Health, "the rewound health must reach the client");
            Assert.AreEqual(1, damageEvents, "a heal is not a hit");
        }

        [Test]
        public void DeadPuppetIsNotRevivedByAStaleSample()
        {
            var tank = Puppet();
            tank.ApplyNetState(Vector3.zero, Quaternion.identity, 0f, 0f, 0);
            Assert.IsTrue(tank.Health.Dead);

            tank.ApplyNetState(Vector3.zero, Quaternion.identity, 0f, 0f, tank.Health.MaxHealth);
            Assert.IsTrue(tank.Health.Dead);
        }
    }
}
