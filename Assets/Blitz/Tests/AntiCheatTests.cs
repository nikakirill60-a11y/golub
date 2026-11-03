using System.Reflection;
using BlitzLegacy.Core.Security;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Security;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    public sealed class AntiCheatTests
    {
        [SetUp] public void SetUp() { AntiTamper.ResetForTests(); }

        private static void EditDecoy(object owner, string field, object value)
        {
            var info = owner.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(info, field);
            object boxed = info.GetValue(owner);
            boxed.GetType().GetField("_decoy", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(boxed, value);
            info.SetValue(owner, boxed);
        }

        [Test]
        public void GunReloadEditIsDetectedAndIgnored()
        {
            var gun = new GunInfo { Reload = 7.5f };
            EditDecoy(gun, "_reload", 0.1f);
            string what = null; AntiTamper.Detected += w => what = w;
            Assert.AreEqual(7.5f, gun.Reload);
            Assert.AreEqual("gun.reload", what);
        }

        [Test]
        public void ConsumableCooldownEditIsDetectedAndIgnored()
        {
            var info = new ConsumableInfo { Cooldown = 90, UseTime = 30 };
            EditDecoy(info, "_cooldown", 0);
            string what = null; AntiTamper.Detected += w => what = w;
            Assert.AreEqual(90, info.Cooldown);
            Assert.AreEqual("consumable.cooldown", what);
            Assert.AreEqual(30, info.UseTime);
            Assert.AreEqual(1, AntiTamper.Hits);
        }

        [Test]
        public void ConsumableCooldownStillBlocksAReuse()
        {
            var runtime = new ConsumableRuntime(new ConsumableInfo { Cooldown = 60, UseTime = 0, Type = ConsumableType.Reusable });
            Assert.AreEqual(ConsumableUse.Used, runtime.UseTry(10f, false));
            Assert.AreEqual(ConsumableUse.OnCooldown, runtime.UseTry(30f, false));
            Assert.AreEqual(ConsumableUse.Used, runtime.UseTry(71f, false));
            Assert.AreEqual(0, AntiTamper.Hits);
        }

        [Test]
        public void ObscuredFloatListRoundTrips()
        {
            var list = new ObscuredFloatList("test") { };
            list.Add(1.5f); list.Add(2.25f);
            Assert.AreEqual(2, list.Count);
            Assert.AreEqual(2.25f, list[1]);
            var sum = 0f; foreach (var v in list) { sum += v; }
            Assert.AreEqual(3.75f, sum);
            Assert.AreEqual(0, AntiTamper.Hits);
        }

        [Test]
        public void ClonedGunKeepsItsOwnReload()
        {
            var gun = new GunInfo { Reload = 4f };
            var copy = gun.WithTurretSlot(new TurretGunSlot { Reload = 6f });
            Assert.AreEqual(4f, gun.Reload);
            Assert.AreEqual(6f, copy.Reload);
            Assert.AreEqual(0, AntiTamper.Hits);
        }

        [Test]
        public void BlocklistMatchesHashesOnly()
        {
            var hash = AntiCheatBan.Hash("device-1");
            Assert.AreEqual(32, hash.Length);
            Assert.AreNotEqual(hash, AntiCheatBan.Hash("device-2"));
            Assert.IsTrue(AntiCheatBan.ListContains("# comment\n\n" + hash.ToUpperInvariant() + "\r\n", hash));
            Assert.IsFalse(AntiCheatBan.ListContains("# " + hash, hash));
            Assert.IsFalse(AntiCheatBan.ListContains(null, hash));
        }
    }
}
