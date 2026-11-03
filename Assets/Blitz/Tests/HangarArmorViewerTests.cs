using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Hangar;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    public sealed class HangarArmorViewerTests
    {
        [Test]
        public void ArmorFillMovesFromGreenThroughYellowToRed()
        {
            var thin = HangarArmorViewer.ArmorColor(20);
            var medium = HangarArmorViewer.ArmorColor(150);
            var thick = HangarArmorViewer.ArmorColor(300);

            Assert.That(thin.g, Is.GreaterThan(thin.r));
            Assert.That(medium.r, Is.GreaterThan(0.8f));
            Assert.That(medium.g, Is.GreaterThan(0.6f));
            Assert.That(thick.r, Is.GreaterThan(thick.g));
            Assert.That(thin.a, Is.EqualTo(0.62f).Within(0.001f));
        }

        [Test]
        public void PenetrationOutcomesHaveDistinctNativeColors()
        {
            var penetration = HangarArmorViewer.OutcomeColor(HitOutcome.Penetration);
            var bounce = HangarArmorViewer.OutcomeColor(HitOutcome.Bounce);
            var ricochet = HangarArmorViewer.OutcomeColor(HitOutcome.Ricochet);
            var overmatch = HangarArmorViewer.OutcomeColor(HitOutcome.Overmatch);

            Assert.That(penetration.g, Is.GreaterThan(penetration.r));
            Assert.That(bounce.r, Is.GreaterThan(bounce.g));
            Assert.That(ricochet.g, Is.GreaterThan(bounce.g));
            Assert.That(overmatch.b, Is.GreaterThan(overmatch.r));
        }
    }
}
