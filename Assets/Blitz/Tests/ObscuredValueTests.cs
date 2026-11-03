using System.Reflection;
using BlitzLegacy.Core.Security;
using BlitzLegacy.Core.Tanks;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    public sealed class ObscuredValueTests
    {
        [SetUp] public void SetUp() { AntiTamper.ResetForTests(); }

        [Test]
        public void RoundTripsAndDefaultsToZero()
        {
            ObscuredInt i = 1234; ObscuredFloat f = 2.5f;
            Assert.AreEqual(1234, (int)i);
            Assert.AreEqual(2.5f, (float)f);
            Assert.AreEqual(0, default(ObscuredInt).Value);
            Assert.AreEqual(0f, default(ObscuredFloat).Value);
            ObscuredFloat nan = float.NaN;
            Assert.IsTrue(float.IsNaN(nan.Value));
            Assert.AreEqual(0, AntiTamper.Hits);
        }

        [Test]
        public void RealValueIsNotStoredInPlainOutsideTheDecoy()
        {
            object boxed = new ObscuredInt(777);
            var stored = (int)typeof(ObscuredInt).GetField("_value", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(boxed);
            Assert.AreNotEqual(777, stored);
        }

        [Test]
        public void EditingTheDecoyIsDetectedAndIgnored()
        {
            var hp = new HealthComponent { MaxHealth = 900 };
            var field = typeof(HealthComponent).GetField("_health", BindingFlags.Instance | BindingFlags.NonPublic);
            object boxed = field.GetValue(hp);
            typeof(ObscuredInt).GetField("_decoy", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(boxed, 99999);
            field.SetValue(hp, boxed);
            string what = null; AntiTamper.Detected += w => what = w;
            Assert.AreEqual(900, hp.Health);
            Assert.AreEqual("tank.health", what);
            Assert.AreEqual(1, AntiTamper.Hits);
            Assert.AreEqual(900, hp.Health);
            Assert.AreEqual(1, AntiTamper.Hits, "decoy is re-synced after a report");
        }

        [Test]
        public void HealthComponentBehaviourIsUnchanged()
        {
            var hp = new HealthComponent { MaxHealth = 500 };
            hp.ReceiveDamageBasic(120);
            Assert.AreEqual(380, hp.Health);
            hp.ReceiveDamageBasic(1000);
            Assert.IsTrue(hp.Dead);
            hp.Restore(50);
            Assert.AreEqual(50, hp.Health);
            Assert.AreEqual(0, AntiTamper.Hits);
        }
    }
}
