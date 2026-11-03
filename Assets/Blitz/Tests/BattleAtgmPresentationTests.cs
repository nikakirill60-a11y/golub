using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using UnityEditor;

namespace BlitzLegacy.Tests
{
    public sealed class BattleAtgmPresentationTests
    {
        private BattleProjectile _shot;
        private Camera _camera;
        private BattleAtgmPresentation _visual;

        [SetUp]
        public void IsolateScene()
        {
            UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                UnityEditor.SceneManagement.NewSceneMode.Single);
        }

        [TearDown]
        public void Cleanup()
        {
            if (_visual != null) _visual.Cancel();
            if (_shot != null) Object.DestroyImmediate(_shot.gameObject);
            if (_camera != null) Object.DestroyImmediate(_camera.gameObject);
            foreach (var fx in Object.FindObjectsByType<DavaFx>(FindObjectsSortMode.None))
                Object.DestroyImmediate(fx.gameObject);
        }

        private void Launch()
        {
            _camera = new GameObject("ATGM test camera").AddComponent<Camera>();
            _camera.pixelRect = new Rect(0, 0, 800, 600);
            _shot = BattleProjectile.Create(Modern(), null, Vector3.forward * 10,
                Vector3.forward * 10, Vector3.forward);
            _visual = _shot.AtgmVisual;
            Assert.IsNotNull(_visual);
            _visual.ViewCamera = _camera;
            Canvas.ForceUpdateCanvases();
        }
        private static ShellInfo Modern()
        {
            return ShellInfo.FromJson(JsonNode.Parse("{\"atgm\":true,\"atgm_visual\":\"blitz1120_marker\",\"speed\":45,\"max_dist\":300,\"turn_speed\":0.6}"));
        }

        [Test]
        public void ProfilesAreExplicitAndNeverTurnAnOrdinaryRoundIntoARocket()
        {
            Assert.IsTrue(Modern().UsesModernAtgmVisual);
            Assert.IsFalse(ShellInfo.FromJson(JsonNode.Parse("{\"atgm\":true}")).UsesModernAtgmVisual);
            Assert.IsFalse(ShellInfo.FromJson(JsonNode.Parse("{\"atgm_visual\":\"blitz1120_marker\"}")).UsesModernAtgmVisual);
            Assert.IsFalse(ShellInfo.FromJson(JsonNode.Parse("{\"atgm\":true,\"atgm_visual\":\"unknown\"}")).UsesModernAtgmVisual);
        }

        [Test]
        public void LiveKpzUsesModernVisualAndBothT49RocketsKeepLegacyTrail()
        {
            var db = TanksDatabase.Parse(Resources.Load<TextAsset>("Data/tanks").text);
            var kpz = db.Get("g14_kpfpz_70r");
            var modern = 0;
            foreach (var gun in kpz.Guns) foreach (var shell in gun.Shells)
            {
                if (shell.Atgm) { Assert.IsTrue(shell.UsesModernAtgmVisual); modern++; }
                else Assert.IsFalse(shell.UsesModernAtgmVisual);
            }
            Assert.AreEqual(1, modern);
            foreach (var gun in db.Get("t49_rocket").Guns) foreach (var shell in gun.Shells)
                Assert.IsFalse(shell.UsesModernAtgmVisual);
        }

        [Test]
        public void OriginalMarkerLoadsAsUncompressedFullRectSprite()
        {
            var sprite = Resources.Load<Sprite>("Atgm1120/Textures/aim_rocket_marker");
            Assert.IsNotNull(sprite);
            Assert.AreEqual(new Vector2(92, 68), sprite.rect.size);
            var importer = (TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(sprite));
            Assert.AreEqual(TextureImporterCompression.Uncompressed, importer.textureCompression);
            Assert.AreEqual(TextureImporterNPOTScale.None, importer.npotScale);
            Assert.IsFalse(importer.mipmapEnabled);
            foreach (var entry in JsonNode.Parse(Resources.Load<TextAsset>(BattleAtgmPresentation.ManifestResource).text)["textures"].Fields)
            {
                var tex = Resources.Load<Texture2D>(entry.Value["resource"].AsString());
                Assert.IsNotNull(tex, entry.Key);
                Assert.AreEqual(entry.Value["width"].AsInt(), tex.width, entry.Key);
                Assert.AreEqual(entry.Value["height"].AsInt(), tex.height, entry.Key);
            }
        }

        [Test]
        public void ModernRocketDoesNotCreateXm81RibbonAndKeepsSourceGeometry()
        {
            Launch();
            Assert.IsNull(_shot.Trail);
            Assert.IsTrue(_shot.Guided);
            Assert.AreEqual(0f, _shot.Gravity);
            Assert.AreEqual(new Vector2(46,34), _visual.LogicalSize);
            Assert.AreEqual(new Vector2(.5f,.6f), _visual.Root.pivot);
            Assert.AreEqual(new Vector2(.5f,.6f), _visual.Content.pivot);
            Assert.IsFalse(_visual.Marker.raycastTarget);
            Assert.AreEqual(Color.white, _visual.Marker.color);
            Assert.IsNotNull(_visual.FlightFx);
            Assert.IsNotNull(_visual.TrailFx);
        }

        [Test]
        public void MarkerTracksProjectionAndHidesBehindCameraOrOutsideViewport()
        {
            Launch();
            _visual.RenderAt(_camera, Time.time);
            Assert.IsTrue(_visual.Marker.enabled);
            var parent = (RectTransform)_visual.Root.parent;
            Vector2 expected;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(parent,
                _camera.WorldToScreenPoint(_shot.transform.position), null, out expected);
            Assert.Less(Vector2.Distance(expected, _visual.Root.localPosition), .01f);
            _shot.transform.position = -Vector3.forward * 10;
            _visual.RenderAt(_camera, Time.time);
            Assert.IsFalse(_visual.Marker.enabled);
            _shot.transform.position = new Vector3(1000,0,10);
            _visual.RenderAt(_camera, Time.time);
            Assert.IsFalse(_visual.Marker.enabled);
            _shot.transform.position = new Vector3(0,0,100);
            _visual.RenderAt(_camera, Time.time);
            Assert.IsTrue(_visual.Marker.enabled);
            Assert.AreEqual(new Vector2(46,34), _visual.Content.sizeDelta, "distance never scales the HUD marker");
        }

        [Test]
        public void ProjectionAlsoWorksUnderNonCentredParentPivot()
        {
            Launch();
            var parent = (RectTransform)_visual.Root.parent;
            parent.pivot = new Vector2(.2f,.8f);
            _visual.RenderAt(_camera, Time.time);
            Vector2 expected;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(parent,
                _camera.WorldToScreenPoint(_shot.transform.position), null, out expected);
            Assert.Less(Vector2.Distance(expected, _visual.Root.localPosition), .01f);
        }

        [Test]
        public void OriginalImpactGrowsTo175PercentAndFadesOver075Seconds()
        {
            Launch();
            _visual.BeginExplosion(_shot.transform.position);
            var now = Time.time;
            Assert.IsNull(_visual.FlightFx);
            Assert.IsNull(_visual.TrailFx);
            _visual.RenderAt(_camera, now + .375f);
            Assert.AreEqual(.5f, _visual.Marker.color.a, .001f);
            Assert.Less(Vector2.Distance(new Vector2(46,34)*1.375f, _visual.Content.sizeDelta), .01f);
            var canvas = _visual.Canvas;
            _visual.RenderAt(_camera, now + .751f);
            Assert.IsTrue(_visual == null);
            Assert.IsTrue(canvas == null);
        }

        [Test]
        public void ExternalDestructionCallbackRemovesMarkerWithoutFakeImpact()
        {
            Launch();
            // EditMode does not run Awake/OnDestroy for a newly created runtime
            // MonoBehaviour. Exercise the destruction callback explicitly.
            typeof(BattleProjectile).GetMethod("OnDestroy", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .Invoke(_shot, null);
            Object.DestroyImmediate(_shot.gameObject);
            Assert.IsTrue(_visual == null);
            foreach (var fx in Object.FindObjectsByType<DavaFx>(FindObjectsSortMode.None))
            {
                Assert.AreNotEqual("DavaFx_AGTM_Explosion", fx.name);
                Assert.IsFalse(fx.Sustain);
            }
        }

        [Test]
        public void RangeTimeoutRemovesMarkerWithoutExplosion()
        {
            Launch();
            var canvas = _visual.Canvas;
            typeof(BattleProjectile).GetField("_destroyTime", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(_shot, Time.time - 1);
            typeof(BattleProjectile).GetMethod("FixedUpdate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .Invoke(_shot, null);
            Assert.IsTrue(_shot == null);
            Assert.IsTrue(_visual == null);
            Assert.IsTrue(canvas == null);
            foreach (var fx in Object.FindObjectsByType<DavaFx>(FindObjectsSortMode.None))
                Assert.AreNotEqual("DavaFx_AGTM_Explosion", fx.name);
        }

        [Test]
        public void RealImpactUsesHitPositionAndMarkerOutlivesProjectile()
        {
            Launch();
            var hit = new Vector3(0,0,15);
            typeof(BattleProjectile).GetMethod("Despawn", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                null, new[] { typeof(Vector3) }, null).Invoke(_shot, new object[] { hit });
            Assert.IsTrue(_shot == null);
            Assert.IsTrue(_visual.Exploding);
            _visual.RenderAt(_camera, Time.time);
            Assert.IsTrue(_visual.Marker.enabled);
            var impacts = 0;
            foreach (var fx in Object.FindObjectsByType<DavaFx>(FindObjectsSortMode.None))
                if (fx.name == "DavaFx_AGTM_Explosion")
                { impacts++; Assert.Less(Vector3.Distance(hit,fx.transform.position),.001f); }
            Assert.AreEqual(1, impacts);
        }

        [Test]
        public void ModernStripeLeavesWorldSpaceNodesAlongMovingRocket()
        {
            Launch();
            var trail = _visual.TrailFx;
            for (var i=0;i<30;i++)
            {
                _shot.transform.position += Vector3.right * .15f;
                trail.Simulate(1f/60);
            }
            trail.Render(_camera);
            var meshes = trail.GetComponentsInChildren<MeshFilter>();
            Assert.AreEqual(1,meshes.Length);
            Assert.Greater(meshes[0].sharedMesh.vertexCount,4);
            Assert.Greater(meshes[0].sharedMesh.bounds.size.x,2f,"old stripe nodes must not follow the missile");
        }

        [Test]
        public void UsesExistingHudCanvasAndCancellationDoesNotDestroyHud()
        {
            var hud = BattleHud.Create(null,null,false);
            try
            {
                Launch();
                Assert.AreSame(hud.Root,_visual.Root.parent);
                Assert.AreSame(hud.Root.GetComponentInParent<Canvas>(),_visual.Canvas);
                _visual.Cancel();
                Assert.IsTrue(hud != null);
                Assert.IsTrue(hud.Root != null);
            }
            finally { Object.DestroyImmediate(hud.gameObject); }
        }

        [Test]
        public void PresentationDisabledCreatesNeitherModernMarkerNorLegacyRibbon()
        {
            _shot = BattleProjectile.Create(Modern(), null, Vector3.zero, Vector3.zero, Vector3.forward, false);
            Assert.IsNull(_shot.AtgmVisual);
            Assert.IsNull(_shot.Trail);
        }
    }
}
