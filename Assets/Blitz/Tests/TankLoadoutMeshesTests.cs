using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// The hangar stand, the battle tank and the headless render all have to
    /// mount the pair the save installs. They used to each read the card on
    /// their own, which let a frame taken outside the hangar screen show the
    /// stock turret while the player looked at the researched one.
    /// </summary>
    public sealed class TankLoadoutMeshesTests
    {
        private static TanksDatabase Database()
        {
            return TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
        }

        [Test]
        public void NoSave_MountsTheStockPair()
        {
            var card = Database().Get("bt-7");
            Assert.That(card, Is.Not.Null);

            string turretMesh;
            string gunMesh;
            TankLoadoutMeshes.Resolve(card, null, out turretMesh, out gunMesh);

            Assert.That(turretMesh, Is.EqualTo(card.Turret(0).Mesh));
            Assert.That(gunMesh, Is.Not.Null.And.Not.Empty);
            Assert.That(card.Turret(0).Slot(card.GunFor(0, 0).Name).Mesh,
                Is.EqualTo(gunMesh));
        }

        [Test]
        public void ResearchedSave_MountsItsOwnTurretAndGun()
        {
            var card = Database().Get("bt-7");
            Assert.That(card.Turrets.Count, Is.GreaterThan(1), "bt-7 turrets");
            var top = card.Turrets.Count - 1;
            var save = new TankSaveData { Name = card.Name, TurretId = top, GunId = 0 };

            string turretMesh;
            string gunMesh;
            TankLoadoutMeshes.Resolve(card, save, out turretMesh, out gunMesh);

            Assert.That(turretMesh, Is.EqualTo(card.Turret(top).Mesh));
            Assert.That(turretMesh, Is.Not.EqualTo(card.Turret(0).Mesh));
            // A gun index the researched turret cannot mount falls back to its
            // own barrel, so the pair always names two meshes of one turret.
            Assert.That(card.Turret(top).Slot(card.GunFor(top, 0).Name).Mesh,
                Is.EqualTo(gunMesh));
        }

        [Test]
        public void SaveOf_FindsTheCardInsideTheAccount()
        {
            var account = new AccountData();
            account.Tanks.Add(new TankSaveData { Name = "ms-1" });
            account.Tanks.Add(new TankSaveData { Name = "bt-7", TurretId = 1 });

            Assert.That(TankLoadoutMeshes.SaveOf(account, "bt-7").TurretId, Is.EqualTo(1));
            Assert.That(TankLoadoutMeshes.SaveOf(account, "t-54"), Is.Null);
            Assert.That(TankLoadoutMeshes.SaveOf(null, "bt-7"), Is.Null);
        }
    }
}
