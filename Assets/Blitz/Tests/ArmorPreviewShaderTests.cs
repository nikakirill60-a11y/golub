using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Guards the overlay the sniper scope paints over enemy armour, the port
    /// of <c>armor_new.gdshader</c>. The colour rule itself lives in the
    /// shader, so this checks that the shader ships, that it exposes every
    /// uniform <c>preview_armor</c> writes, and that a plate material draws
    /// additively over the hull like <c>render_priority = 1</c> does.
    /// </summary>
    public sealed class ArmorPreviewShaderTests
    {
        private const float Tolerance = 1e-4f;

        /// <summary>The only branch the port ships models and armour for.</summary>
        private static readonly string[] Branch =
        {
            "ms-1", "bt-2", "bt-7", "a-20", "t-34",
            "t-34-85", "t-43", "t-44", "t-54", "t62a",
        };

        private GameObject _root;

        [TearDown]
        public void TearDown()
        {
            if (_root != null)
            {
                Object.DestroyImmediate(_root);
                _root = null;
            }
        }

        [Test]
        public void Shader_ShipsWithEveryUniformThePreviewWrites()
        {
            var shader = ArmorPreviewShader.Load();

            Assert.That(shader, Is.Not.Null, ArmorPreviewShader.Name + " is missing");
            Assert.That(
                shader.FindPropertyIndex("_NominalArmor"), Is.GreaterThanOrEqualTo(0));
            Assert.That(shader.FindPropertyIndex("_Caliber"), Is.GreaterThanOrEqualTo(0));
            Assert.That(
                shader.FindPropertyIndex("_Penetration"), Is.GreaterThanOrEqualTo(0));
            Assert.That(
                shader.FindPropertyIndex("_Normalization"), Is.GreaterThanOrEqualTo(0));
            Assert.That(shader.FindPropertyIndex("_HE"), Is.GreaterThanOrEqualTo(0));
            Assert.That(shader.FindPropertyIndex("_HEAT"), Is.GreaterThanOrEqualTo(0));
        }

        [Test]
        public void NewMaterial_DrawsOverTheHullItCovers()
        {
            var shader = ArmorPreviewShader.Load();
            Assert.That(shader, Is.Not.Null);

            var material = ArmorPreviewShader.NewMaterial(shader);
            try
            {
                Assert.That(
                    material.renderQueue,
                    Is.GreaterThan((int)RenderQueue.Transparent));

                material.SetFloat(ArmorPreviewShader.NominalArmor, 75f);
                Assert.That(
                    material.GetFloat(ArmorPreviewShader.NominalArmor),
                    Is.EqualTo(75f).Within(1e-4f));
            }
            finally
            {
                Object.DestroyImmediate(material);
            }
        }

        /// <summary>
        /// The hangar viewer reuses this shader and retunes it per mode, so
        /// the knobs it turns have to exist and default to the additive scope
        /// look the battle overlay has always had.
        /// </summary>
        [Test]
        public void NewMaterial_DefaultsToTheAdditiveScopeLook()
        {
            var shader = ArmorPreviewShader.Load();
            Assert.That(shader, Is.Not.Null);

            var material = ArmorPreviewShader.NewMaterial(shader);
            try
            {
                Assert.That(
                    material.GetFloat(ArmorPreviewShader.SrcBlend),
                    Is.EqualTo((float)BlendMode.SrcAlpha).Within(Tolerance));
                Assert.That(
                    material.GetFloat(ArmorPreviewShader.DstBlend),
                    Is.EqualTo((float)BlendMode.One).Within(Tolerance));
                Assert.That(
                    material.GetFloat(ArmorPreviewShader.ZTest),
                    Is.EqualTo((float)CompareFunction.LessEqual).Within(Tolerance));
                Assert.That(
                    material.GetFloat(ArmorPreviewShader.Extrude),
                    Is.EqualTo(1f).Within(Tolerance));
                Assert.That(
                    material.GetFloat(ArmorPreviewShader.Alpha),
                    Is.EqualTo(ArmorPreviewShader.SourceAlpha).Within(Tolerance));
            }
            finally
            {
                Object.DestroyImmediate(material);
            }
        }

        /// <summary>
        /// The scope paints the whole tank, not just the hull: every plate the
        /// porter exported carries its own overlay, the turret and gun mask
        /// included, and they ride their pivots so they keep covering the
        /// turret once it traverses.
        /// </summary>
        [TestCase("ms-1")]
        [TestCase("bt-2")]
        [TestCase("bt-7")]
        [TestCase("a-20")]
        [TestCase("t-34")]
        [TestCase("t-34-85")]
        [TestCase("t-43")]
        [TestCase("t-44")]
        [TestCase("t-54")]
        [TestCase("t62a")]
        public void PreviewArmor_CoversTheTurretAndGunOfEveryBranchTank(string name)
        {
            var database = TanksDatabase.Parse(
                BlitzResources.LoadText(BlitzResources.TanksData));
            var card = database.Get(name);
            Assert.That(card, Is.Not.Null, name + " card");

            _root = new GameObject("ArmorPreviewCoverage");
            var spawn = new GameObject("Spawn").transform;
            spawn.SetParent(_root.transform, false);
            var tank = BattleTank.Spawn(
                _root.transform,
                name,
                card,
                spawn,
                new Bounds(Vector3.zero, new Vector3(1000f, 100f, 1000f)),
                1);

            // A 76 mm high-explosive round: the shell the scope colours with.
            var shell = new ShellInfo
            {
                Caliber = 76,
                Penetration = 38f,
                Type = ShellType.He,
            };

            var turretPlates = 0;
            var gunPlates = 0;
            for (var i = 0; i < tank.Plates.Count; i++)
            {
                var plate = tank.Plates[i];
                var label = name + " " + plate.PlateName;
                Assert.That(plate.Preview, Is.Not.Null, label + " overlay");
                Assert.That(plate.PreviewMaterial, Is.Not.Null, label + " material");
                Assert.That(plate.Preview.enabled, Is.False,
                    label + " starts hidden like mdl.hide()");
                Assert.That(
                    plate.PreviewMaterial.GetFloat(ArmorPreviewShader.NominalArmor),
                    Is.EqualTo((float)plate.Armor).Within(Tolerance),
                    label + " thickness");

                if (plate.Mount == tank.Turret.TurretNodeName)
                {
                    turretPlates++;
                    Assert.That(
                        plate.FollowMount, Is.SameAs(tank.Turret.TurretPivot),
                        label + " follows the traversing turret");
                }
                else if (plate.Mount == tank.Turret.GunNodeName)
                {
                    gunPlates++;
                    Assert.That(
                        plate.FollowMount, Is.SameAs(tank.Turret.GunPivot),
                        label + " follows the elevating gun");
                }
            }

            Assert.That(turretPlates, Is.GreaterThan(0),
                name + " exports no turret armour, so the scope shows the "
                    + "turret as if it were a hole");
            Assert.That(gunPlates, Is.GreaterThan(0), name + " gun mask armour");

            tank.PreviewArmor(true, shell);
            Assert.That(tank.ArmorShown, Is.True);
            for (var i = 0; i < tank.Plates.Count; i++)
            {
                var plate = tank.Plates[i];
                var label = name + " " + plate.PlateName;
                Assert.That(plate.Preview.enabled, Is.True, label + " is painted");
                Assert.That(
                    plate.PreviewMaterial.GetFloat(ArmorPreviewShader.Caliber),
                    Is.EqualTo(76f).Within(Tolerance), label + " calibre");
                Assert.That(
                    plate.PreviewMaterial.GetFloat(ArmorPreviewShader.Penetration),
                    Is.EqualTo(38f).Within(Tolerance), label + " penetration");
                Assert.That(
                    plate.PreviewMaterial.GetFloat(ArmorPreviewShader.He),
                    Is.EqualTo(1f).Within(Tolerance),
                    label + " is coloured by the explosive rule");
            }

            tank.PreviewArmor(false, null);
            Assert.That(tank.ArmorShown, Is.False);
            for (var i = 0; i < tank.Plates.Count; i++)
            {
                Assert.That(tank.Plates[i].Preview.enabled, Is.False,
                    name + " " + tank.Plates[i].PlateName + " is put away");
            }
        }
    }
}
