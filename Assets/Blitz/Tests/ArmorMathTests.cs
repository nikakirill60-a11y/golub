using System;
using BlitzLegacy.Core.Tanks;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Guards the penetration math ported out of Godot <c>Projectile</c>. The
    /// worked example is the MS-1 37 mm gun firing at MS-1 plates, so every
    /// number here can be read back out of tanks.json and ms1_armor.json.
    /// </summary>
    public sealed class ArmorMathTests
    {
        private const float Tolerance = 1e-3f;

        // UBRT1: armour-piercing, 37 mm wide, 34 mm of penetration.
        private const int Caliber = 37;
        private const float Penetration = 34f;
        private const float DegToRad = (float)(Math.PI / 180.0);
        private const float RadToDeg = (float)(180.0 / Math.PI);

        [Test]
        public void ImpactAngle_IsZeroHeadOnAndFortyFiveOnTheSlope()
        {
            var head = ArmorMath.ImpactAngle(-1f);
            var slope = ArmorMath.ImpactAngle(-(float)Math.Sqrt(0.5));

            Assert.That(head * RadToDeg, Is.EqualTo(0f).Within(Tolerance));
            Assert.That(slope * RadToDeg, Is.EqualTo(45f).Within(Tolerance));
        }

        [Test]
        public void EffectiveArmor_ThickensWithTheAngle()
        {
            // High explosive neither normalises nor overmatches, so the plate is
            // exactly the nominal thickness over the cosine.
            var straight = ArmorMath.EffectiveArmor(ShellType.He, Caliber, 13, 0f);
            var sloped = ArmorMath.EffectiveArmor(
                ShellType.He, Caliber, 13, 60f * DegToRad);

            Assert.That(straight, Is.EqualTo(13f).Within(Tolerance));
            Assert.That(sloped, Is.EqualTo(26f).Within(Tolerance));
        }

        [Test]
        public void Normalization_FollowsTheShellType()
        {
            Assert.That(ArmorMath.Normalization(ShellType.Ap), Is.EqualTo(5f));
            Assert.That(ArmorMath.Normalization(ShellType.Apcr), Is.EqualTo(2f));
            Assert.That(ArmorMath.Normalization(ShellType.Heat), Is.EqualTo(0f));
            Assert.That(ArmorMath.Normalization(ShellType.He), Is.EqualTo(0f));
            Assert.That(ArmorMath.IsExplosive(ShellType.He), Is.True);
            Assert.That(ArmorMath.IsExplosive(ShellType.Hesh), Is.True);
            Assert.That(ArmorMath.IsExplosive(ShellType.Ap), Is.False);
            Assert.That(ArmorMath.IsExplosive(ShellType.Heat), Is.False);
        }

        [Test]
        public void NormalizationFor_GrowsOnceTheShellIsTwiceThePlate()
        {
            var overmatched = ArmorMath.NormalizationFor(ShellType.Ap, Caliber, 13);
            var plain = ArmorMath.NormalizationFor(ShellType.Ap, Caliber, 20);
            var shaped = ArmorMath.NormalizationFor(ShellType.Heat, Caliber, 13);

            Assert.That(
                overmatched,
                Is.EqualTo(5f + 5f * 1.4f * Caliber / 13f).Within(Tolerance));
            Assert.That(plain, Is.EqualTo(5f).Within(Tolerance));
            Assert.That(shaped, Is.EqualTo(0f).Within(Tolerance));
        }

        [Test]
        public void Overmatches_NeedsThreeCalibersOfSolidShot()
        {
            Assert.That(ArmorMath.Overmatches(ShellType.Ap, Caliber, 8), Is.True);
            Assert.That(ArmorMath.Overmatches(ShellType.Ap, Caliber, 13), Is.False);
            Assert.That(ArmorMath.Overmatches(ShellType.He, Caliber, 8), Is.False);
            Assert.That(ArmorMath.UsesCaliberRule(ShellType.Apcr), Is.True);
            Assert.That(ArmorMath.UsesCaliberRule(ShellType.Heat), Is.False);
        }

        [Test]
        public void Ricochets_OnlySolidShotPastSeventyDegrees()
        {
            var graze = 75f * DegToRad;
            var shallow = 60f * DegToRad;

            Assert.That(ArmorMath.Ricochets(ShellType.Ap, graze, false), Is.True);
            Assert.That(ArmorMath.Ricochets(ShellType.Ap, shallow, false), Is.False);
            // A screen already caught the shell, so the plate behind it cannot
            // bounce the shell away any more.
            Assert.That(ArmorMath.Ricochets(ShellType.Ap, graze, true), Is.False);
            Assert.That(ArmorMath.Ricochets(ShellType.Heat, graze, false), Is.False);
        }

        /// <summary>
        /// The Blitz cards carry their own <c>ricochetAngle</c>: 70 on solid
        /// shot, 80/85 on every shaped charge and none at all on high
        /// explosive. The MGM-51A of the XM81 launcher is the shaped charge
        /// this port ships, so it has to bounce past 85 degrees and bite at
        /// 80, where an AP round of the same angle would already have grazed.
        /// </summary>
        [Test]
        public void Ricochets_FollowsTheCardAngleWhenTheClientShipsOne()
        {
            var grazing = 86f * DegToRad;
            var steep = 80f * DegToRad;

            Assert.That(ArmorMath.RicochetAngleFor(ShellType.Heat, 85f), Is.EqualTo(85f));
            Assert.That(ArmorMath.RicochetAngleFor(ShellType.Ap, 0f), Is.EqualTo(70f));
            Assert.That(ArmorMath.RicochetAngleFor(ShellType.Heat, 0f), Is.EqualTo(0f));
            // No high-explosive card in the pack ships an angle, and the one
            // that does ships a zero, so explosives never bounce.
            Assert.That(ArmorMath.RicochetAngleFor(ShellType.He, 85f), Is.EqualTo(0f));

            Assert.That(ArmorMath.Ricochets(ShellType.Heat, grazing, false, 85f), Is.True);
            Assert.That(ArmorMath.Ricochets(ShellType.Heat, steep, false, 85f), Is.False);
            Assert.That(ArmorMath.Ricochets(ShellType.Heat, grazing, true, 85f), Is.False);
            Assert.That(ArmorMath.Ricochets(ShellType.He, grazing, false, 85f), Is.False);

            Assert.That(
                HitResolver.ResolveArmor(
                    ShellType.Heat, 152, 185f, 30, grazing, false, 85f),
                Is.EqualTo(HitOutcome.Ricochet));
            Assert.That(
                HitResolver.ResolveArmor(
                    ShellType.Heat, 152, 185f, 30, steep, false, 85f),
                Is.EqualTo(HitOutcome.Penetration));
        }

        /// <summary>
        /// <c>explosion_radius_for</c>: the authored blast wins, and a card
        /// without one falls back to the calibre curve the source uses.
        /// </summary>
        [Test]
        public void ExplosionRadius_PrefersTheCardOverTheCalibreRule()
        {
            Assert.That(
                ArmorMath.ExplosionRadiusFor(5.11f, 152),
                Is.EqualTo(5.11f).Within(Tolerance));
            Assert.That(
                ArmorMath.ExplosionRadiusFor(0f, 45),
                Is.EqualTo((float)Math.Pow(45 * 0.02, 1.3)).Within(Tolerance));
            Assert.That(ArmorMath.ExplosionRadiusFor(0f, 0), Is.EqualTo(0f));
        }

        [Test]
        public void Penetrates_ComparesTheEffectivePlateWithThePenetration()
        {
            var frontal = ArmorMath.EffectiveArmor(ShellType.Ap, Caliber, 13, 0f);
            var thickAndSloped = ArmorMath.EffectiveArmor(
                ShellType.Ap, Caliber, 30, 70f * DegToRad);

            Assert.That(frontal, Is.LessThan(Penetration));
            Assert.That(thickAndSloped, Is.EqualTo(60f).Within(Tolerance));
            Assert.That(ArmorMath.Penetrates(frontal, Penetration), Is.True);
            Assert.That(ArmorMath.Penetrates(thickAndSloped, Penetration), Is.False);
        }

        [Test]
        public void ScreensAndShapedChargesBleedPenetrationOverTheGap()
        {
            Assert.That(
                ArmorMath.PenetrationAfterScreen(Penetration, 0.5f),
                Is.EqualTo(Penetration - 0.5f * ArmorMath.ScreenPenaltyPerMetre)
                    .Within(Tolerance));
            Assert.That(
                ArmorMath.PenetrationAfterScreen(Penetration, 5f),
                Is.EqualTo(0f).Within(Tolerance));
            // The source leaves the shaped-charge loss unclamped, so a long gap
            // takes the jet below zero and the plate behind it holds.
            Assert.That(
                ArmorMath.PenetrationAfterHeatGap(44f, 1f),
                Is.EqualTo(-56f).Within(Tolerance));
        }

        // UOF-354: the 76 mm high-explosive shell of the T-34's F-34, with the
        // 38 mm of penetration tanks.json carries for it.
        private const int HeCaliber = 76;
        private const float HePenetration = 38f;

        /// <summary>
        /// High explosive follows the plain thickness rule: no normalisation,
        /// no three-calibre overmatch and no ricochet, however steep the plate.
        /// </summary>
        [Test]
        public void HighExplosive_NeverOvermatchesAndNeverRicochets()
        {
            var graze = 80f * DegToRad;

            Assert.That(ArmorMath.CanRicochet(ShellType.He), Is.False);
            Assert.That(ArmorMath.Ricochets(ShellType.He, graze, false), Is.False);
            Assert.That(ArmorMath.UsesCaliberRule(ShellType.He), Is.False);
            Assert.That(ArmorMath.Overmatches(ShellType.He, HeCaliber, 15), Is.False);
            Assert.That(
                ArmorMath.NormalizationFor(ShellType.He, HeCaliber, 15),
                Is.EqualTo(0f).Within(Tolerance));
            Assert.That(
                HitResolver.ResolveArmor(
                    ShellType.He, HeCaliber, HePenetration, 15, graze, false),
                Is.Not.EqualTo(HitOutcome.Ricochet),
                "an explosive shell digs into the plate instead of skipping");
        }

        /// <summary>
        /// The T-34 shooting a T-34 with UOF-354: the 45 mm hull and turret
        /// sides hold and only splash the crew, while the thin roofs go
        /// through. These are the plate thicknesses of t34_armor.json.
        /// </summary>
        [TestCase(45, 0f, false, TestName = "HeHoldsOnTheFortyFiveMillimetreHull")]
        [TestCase(45, 30f, false, TestName = "HeHoldsOnTheSlopedHull")]
        [TestCase(45, 0f, false, TestName = "HeHoldsOnTheTurretFront")]
        [TestCase(20, 0f, true, TestName = "HeGoesThroughTheHullRoof")]
        [TestCase(15, 0f, true, TestName = "HeGoesThroughTheTurretRoof")]
        [TestCase(38, 0f, true, TestName = "HeGoesThroughExactlyItsOwnPenetration")]
        public void HighExplosive_PenetratesOnlyThePlatesThinnerThanItsRating(
            int plate, float angleDeg, bool expectPenetration)
        {
            var outcome = HitResolver.ResolveArmor(
                ShellType.He,
                HeCaliber,
                HePenetration,
                plate,
                angleDeg * DegToRad,
                false);

            Assert.That(
                outcome,
                Is.EqualTo(expectPenetration
                    ? HitOutcome.Penetration
                    : HitOutcome.Bounce),
                plate + " mm at " + angleDeg + " degrees");
        }

        /// <summary>
        /// The angle still counts for an explosive shell, so the same 40 mm
        /// plate that gives way head on holds once it leans over.
        /// </summary>
        [Test]
        public void HighExplosive_LosesTheThinPlateOnceItLeansOver()
        {
            var straight = HitResolver.ResolveArmor(
                ShellType.He, HeCaliber, HePenetration, 30, 0f, false);
            var sloped = HitResolver.ResolveArmor(
                ShellType.He, HeCaliber, HePenetration, 30, 60f * DegToRad, false);

            Assert.That(straight, Is.EqualTo(HitOutcome.Penetration));
            Assert.That(sloped, Is.EqualTo(HitOutcome.Bounce),
                "60 degrees doubles the 30 mm plate past the 38 mm rating");
        }

        /// <summary>
        /// The 85 mm UOF-365 of the T-34-85 (43 mm) still fails against 45 mm
        /// of armour, which is why the sniper scope paints a T-34 hull amber
        /// rather than clear.
        /// </summary>
        [Test]
        public void EightyFiveMillimetreHe_StillFailsAgainstFortyFiveMillimetres()
        {
            Assert.That(
                HitResolver.ResolveArmor(ShellType.He, 85, 43f, 45, 0f, false),
                Is.EqualTo(HitOutcome.Bounce));
            Assert.That(
                HitResolver.ResolveArmor(ShellType.He, 85, 43f, 40, 0f, false),
                Is.EqualTo(HitOutcome.Penetration));
        }

        /// <summary>
        /// A blocked explosive shell is not a blank: the source still doubles
        /// its damage against destructible props, which is the splash half of
        /// the rule the overlay paints amber.
        /// </summary>
        [Test]
        public void BlockedHighExplosive_StillCarriesItsSplash()
        {
            Assert.That(
                HitResolver.DestructibleDamage(ShellType.He, 100, HePenetration),
                Is.EqualTo(200));
            // A solid shot instead spends twice its penetration on the prop.
            Assert.That(
                HitResolver.DestructibleDamage(ShellType.Ap, 100, HePenetration),
                Is.EqualTo(76));
        }
    }
}
