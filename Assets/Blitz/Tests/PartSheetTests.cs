using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// The Silencer (Oth46 Charioteer S3) draws the barrel tube of its top
    /// guns on a sheet of its own (`Oth46_Charioteer_S3_obves`). The export
    /// splits those batches into `&lt;part&gt;_sheetN` objects; they take that
    /// sheet and ride, show and hide with the part they belong to.
    /// </summary>
    public sealed class PartSheetTests
    {
        private const string Silencer = "oth46_charioteer_s3";

        [Test]
        public void SheetOwner_NamesThePartObject()
        {
            Assert.AreEqual("gun_04", TankNodeTable.SheetOwner("gun_04_sheet1"));
            Assert.AreEqual("hull_vis", TankNodeTable.SheetOwner("hull_vis_sheet12"));
            Assert.IsNull(TankNodeTable.SheetOwner("gun_04"));
            Assert.IsNull(TankNodeTable.SheetOwner("gun_04_sheet"));
            Assert.IsNull(TankNodeTable.SheetOwner("gun_04_sheetA"));
            Assert.IsNull(TankNodeTable.SheetOwner("_sheet1"));
        }

        [Test]
        public void Silencer_RidesItsObvesBarrelOnTheGun()
        {
            var database = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
            var card = database.Get(Silencer);
            Assert.IsNotNull(card);
            var root = new GameObject("sheet-test");
            LogAssert.ignoreFailingMessages = true;
            try
            {
                var spawn = new GameObject("Spawn").transform;
                spawn.SetParent(root.transform, false);
                var tank = BattleTank.Spawn(root.transform, card.Name, card, spawn,
                    new Bounds(Vector3.zero, new Vector3(1000f, 100f, 1000f)));
                Assert.IsNotNull(tank);
                var turret = tank.Turret;
                var gunName = turret.GunNodeName;
                var turretName = turret.TurretNodeName;

                MeshFilter gun = null, gunSheet = null, turretSheet = null;
                foreach (var filter in tank.Model.GetComponentsInChildren<MeshFilter>(true))
                {
                    var name = filter.gameObject.name.Replace("(Clone)", string.Empty).Trim();
                    var owner = TankNodeTable.SheetOwner(name);
                    if (name == gunName) { gun = filter; }
                    else if (name == gunName + "_sheet1") { gunSheet = filter; }
                    else if (name == turretName + "_sheet1") { turretSheet = filter; }
                    else if (owner != null && owner.StartsWith("gun_", System.StringComparison.Ordinal))
                    {
                        Assert.IsFalse(filter.gameObject.activeInHierarchy, name + " belongs to another gun");
                    }
                    else if (owner != null && owner.StartsWith("turret_", System.StringComparison.Ordinal))
                    {
                        Assert.IsFalse(filter.gameObject.activeInHierarchy, name + " belongs to another turret");
                    }
                }

                Assert.IsNotNull(gun, gunName);
                Assert.IsNotNull(gunSheet, gunName + "_sheet1");
                Assert.IsNotNull(turretSheet, turretName + "_sheet1");
                Assert.IsTrue(gunSheet.gameObject.activeInHierarchy, "the installed gun shows its barrel");
                Assert.IsTrue(turretSheet.gameObject.activeInHierarchy, "the installed turret shows its kit");
                Assert.IsTrue(gunSheet.transform.IsChildOf(turret.GunPivot), "the barrel elevates with the gun");
                Assert.IsTrue(turretSheet.transform.IsChildOf(turret.TurretPivot), "the kit turns with the turret");

                // The tube runs from the turret face to the muzzle brake.
                Assert.Greater(gunSheet.GetComponent<MeshRenderer>().bounds.size.z, 1.5f, "barrel tube");

                var texture = gunSheet.GetComponent<MeshRenderer>().sharedMaterial.mainTexture;
                Assert.IsNotNull(texture);
                StringAssert.Contains("OBVES", texture.name.ToUpperInvariant(), "drawn with the obves sheet");
                Assert.AreNotSame(gun.GetComponent<MeshRenderer>().sharedMaterial,
                    gunSheet.GetComponent<MeshRenderer>().sharedMaterial);
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
                Object.DestroyImmediate(root);
            }
        }
    }
}
