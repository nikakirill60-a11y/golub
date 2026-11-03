using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Ui;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Locks the two contracts the first device run broke: the canvas the
    /// battle UI is laid out against, and the readability every tank mesh
    /// needs because <see cref="BattleTank"/> cuts its colliders and armour
    /// plates out of the imported meshes at runtime.
    /// </summary>
    public sealed class BattleCanvasTests
    {
        private const float Tolerance = 1e-3f;

        /// <summary>
        /// save_settings.gd keeps ui_scale_battle at 1.0 on a phone, so the
        /// logical canvas is the authored 1040x550 rather than the roughly
        /// 1387x733 the menu factor stretches it to.
        /// </summary>
        [Test]
        public void BattleCanvas_UsesTheAuthoredSizeOnAPhone()
        {
            Assert.AreEqual(1f, UiLayout.BattleScale(true), Tolerance);
            Assert.AreEqual(1040f, UiLayout.BattleReferenceWidth(true), Tolerance);
            Assert.AreEqual(550f, UiLayout.BattleReferenceHeight(true), Tolerance);
        }

        /// <summary>Desktop keeps the menu factor, as the source does.</summary>
        [Test]
        public void BattleCanvas_KeepsTheMenuFactorOnDesktop()
        {
            Assert.AreEqual(
                UiLayout.GodotStretchScale, UiLayout.BattleScale(false), Tolerance);
            Assert.AreEqual(
                UiLayout.ReferenceWidth, UiLayout.BattleReferenceWidth(false), Tolerance);
            Assert.AreEqual(
                UiLayout.ReferenceHeight, UiLayout.BattleReferenceHeight(false), Tolerance);
        }

        /// <summary>
        /// The phone canvas has to be the smaller of the two, which is what
        /// makes the HUD and the thumb buttons grow instead of drifting up.
        /// </summary>
        [Test]
        public void MenuCanvas_StaysLargerThanTheBattleCanvas()
        {
            Assert.Greater(UiLayout.ReferenceHeight, UiLayout.BattleReferenceHeight(true));
            Assert.Greater(UiLayout.ReferenceWidth, UiLayout.BattleReferenceWidth(true));
        }

        /// <summary>
        /// A player build refuses to read a mesh that was imported unreadable,
        /// so Mirror and BuildCollider would hand empty geometry to the hull
        /// collider and to every armour plate. In the editor this property is
        /// the importer setting, which is exactly what has to stay true.
        /// </summary>
        [TestCase(BattleTank.ModelResource)]
        [TestCase(BattleTank.PhysicsHullResource)]
        [TestCase(TankArmorTable.ModelResource)]
        public void TankMeshes_StayReadableForTheRuntimeCutter(string resource)
        {
            var prefab = Resources.Load<GameObject>(resource);
            Assert.IsNotNull(prefab, "missing tank model: " + resource);

            var filters = prefab.GetComponentsInChildren<MeshFilter>(true);
            Assert.Greater(filters.Length, 0, "no mesh under " + resource);

            foreach (var filter in filters)
            {
                var mesh = filter.sharedMesh;
                Assert.IsNotNull(mesh, resource + " has an empty mesh filter");
                Assert.IsTrue(
                    mesh.isReadable,
                    resource + " imported unreadable, the player build would get "
                    + "no collider: " + mesh.name);
                Assert.Greater(
                    mesh.triangles.Length, 0, resource + " holds no triangles");
            }
        }
    }
}
