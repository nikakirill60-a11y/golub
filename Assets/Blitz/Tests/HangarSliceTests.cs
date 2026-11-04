using BlitzLegacy.Core.Input;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Hangar;
using BlitzLegacy.Runtime.Ui;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    public sealed class HangarSliceTests
    {
        [Test]
        public void InputMath_PreservesGodotSensitivityAndPitchContract()
        {
            Assert.AreEqual(0.6f, HangarInputMath.DefaultSensitivity, 0.0001f);
            Assert.AreEqual(2.7f,
                HangarInputMath.DegreesForPixels(100f, 0.6f), 0.0001f);
            Assert.AreEqual(HangarInputMath.MinPitch,
                HangarInputMath.ClampPitch(-100f), 0.0001f);
            Assert.AreEqual(HangarInputMath.MaxPitch,
                HangarInputMath.ClampPitch(100f), 0.0001f);
            Assert.AreEqual(HangarInputMath.DefaultSensitivity,
                HangarInputMath.NormalizeSensitivity(float.NaN), 0.0001f);
        }

        /// <summary>
        /// The tank has to follow the finger. The port kept the
        /// <c>-relative.x</c> of add_look() while placing the camera on the
        /// other side of the pivot than the Godot rig, so the two minus signs
        /// cancelled and a swipe to the left spun the tank to the right.
        /// </summary>
        [Test]
        public void Orbit_TurnsTheTankTowardsTheSwipe()
        {
            var pivot = new GameObject(
                "hangar_orbit_probe", typeof(HangarOrbitController));
            var eye = new GameObject("hangar_camera_probe", typeof(Camera));
            try
            {
                var camera = eye.GetComponent<Camera>();
                var orbit = pivot.GetComponent<HangarOrbitController>();
                orbit.Configure(camera, HangarInputMath.DefaultSensitivity);

                // A point on the hull right in front of the lens: its offset
                // along the camera's right axis is where it sits on screen.
                var hull = pivot.transform.position + ((eye.transform.position
                    - pivot.transform.position).normalized * 1.5f);
                var before = eye.transform.InverseTransformPoint(hull);
                Assert.AreEqual(0f, before.x, 0.001f);

                orbit.BeginDrag();
                orbit.Drag(new Vector2(-120f, 0f));
                orbit.EndDrag();

                var after = eye.transform.InverseTransformPoint(hull);
                Assert.Less(
                    after.x, -0.001f, "a swipe to the left turned the tank right");
                Assert.AreEqual(
                    HangarInputMath.DegreesForPixels(
                        -120f, HangarInputMath.DefaultSensitivity),
                    orbit.Yaw - HangarOrbitController.SourceYaw,
                    0.0001f);
                Assert.AreEqual(
                    HangarOrbitController.SourceDistance,
                    Vector3.Distance(eye.transform.position, pivot.transform.position),
                    0.001f);

                // Vertically the port already matched: Godot counts screen y
                // downwards, so a swipe down lifts the camera over the tank.
                var height = eye.transform.position.y;
                orbit.BeginDrag();
                orbit.Drag(new Vector2(0f, -120f));
                orbit.EndDrag();
                Assert.Greater(
                    eye.transform.position.y, height,
                    "a swipe down pushed the camera under the tank");
            }
            finally
            {
                Object.DestroyImmediate(eye);
                Object.DestroyImmediate(pivot);
            }
        }

        [Test]
        public void Settings_RoundTripIncludesHangarSubsetAndNormalizesFps()
        {
            var settings = new SettingsData
            {
                FpsLimit = 90,
                MouseSensitivity = 1.25f,
            };
            var restored = SettingsData.FromJson(JsonNode.Parse(settings.ToJson().ToJson()));
            Assert.AreEqual(90, restored.FpsLimit);
            Assert.AreEqual(1.25f, restored.MouseSensitivity, 0.0001f);

            // FPS_LIMIT_UNCAPPED is a real choice on desktop and mobile. A
            // new phone still defaults to 60, but an explicit infinity must
            // survive so runtime can select its fastest display mode.
            Assert.AreEqual(0, SettingsData.NormalizeFpsLimit(0, false));
            Assert.AreEqual(0, SettingsData.NormalizeFpsLimit(0, true));
            Assert.AreEqual(0, SettingsData.NormalizeFpsLimit(45, false));
            Assert.AreEqual(SettingsData.DefaultFpsLimit,
                SettingsData.NormalizeFpsLimit(45, true));

            var legacy = SettingsData.FromJson(
                JsonNode.NewObject().Set("fps_limit", 45));
            Assert.AreEqual(
                SettingsData.DefaultFps(SettingsData.MobilePlatform),
                legacy.FpsLimit);
            Assert.AreEqual(HangarInputMath.DefaultSensitivity,
                legacy.MouseSensitivity, 0.0001f);
        }

        [Test]
        public void SourceManifest_DescribesExactCurrentModelSlice()
        {
            var text = Resources.Load<TextAsset>("Data/hangar_source_manifest");
            Assert.IsNotNull(text, "hangar source manifest is missing");
            var manifest = JsonNode.Parse(text.text);

            var hangar = manifest["models"]["hangar"];
            Assert.AreEqual(76, hangar["objects"].AsInt());
            Assert.AreEqual(148308, hangar["vertices"].AsInt());
            Assert.AreEqual(74987, hangar["triangles"].AsInt());

            // The vehicles themselves are no longer cut out of the decompiled
            // Godot build: Tools/port_blitz_branch_models.py exports every
            // researchable turret and gun of the branch from the client pack
            // and records that slice in its own manifest.
            Assert.AreEqual(
                1, manifest["models"].Count, "the hangar manifest only holds the hangar");

            var models = Resources.Load<TextAsset>("Data/tank_models_source_manifest");
            Assert.IsNotNull(models, "tank model manifest is missing");
            var ms1 = JsonNode.Parse(models.text)["tanks"]["ms-1"];
            Assert.AreEqual("3d/Tanks/USSR/MS-1.sc2", ms1["source"].AsString());
            // 37 live parts plus the client tracks_crash mesh.
            Assert.AreEqual(38, ms1["model"]["objects"].AsInt());
            Assert.AreEqual(7314, ms1["model"]["vertices"].AsInt());
            Assert.AreEqual(5265, ms1["model"]["triangles"].AsInt());

            // Godot builds the armour colliders by walking the model at load
            // time; the port ships that walk as one plate per authored
            // (thickness, screen) pair of the hull, of every turret and of
            // every gun mantlet. The triangles are the collision mesh's own.
            var armor = ms1["armor"];
            Assert.AreEqual(32, armor["objects"].AsInt());
            Assert.AreEqual(2898, armor["vertices"].AsInt());
            Assert.AreEqual(1246, armor["triangles"].AsInt());
            Assert.AreEqual(32, armor["plates"].AsInt());

            // The localization and atlas tables are shared with every later
            // slice - porting the battle HUD added strings to lang.json and
            // regions to the atlas - so literal totals here would only pin how
            // far the port had got when this test was written. Check the
            // manifest against the tables it describes instead, which is the
            // drift it exists to catch.
            var lang = Resources.Load<TextAsset>("Data/lang");
            Assert.IsNotNull(lang, "localization table is missing");
            var localization = JsonNode.Parse(lang.text);
            Assert.AreEqual(localization["keys"].Count,
                manifest["localization"]["keys"].AsInt());
            Assert.AreEqual(localization["languages"].Count,
                manifest["localization"]["languages"].AsInt());

            var atlasText = Resources.Load<TextAsset>("Data/atlas_regions");
            Assert.IsNotNull(atlasText, "atlas region table is missing");
            var atlas = JsonNode.Parse(atlasText.text);
            Assert.AreEqual(atlas["textures"].Count,
                manifest["atlas"]["textures"].AsInt());
            Assert.AreEqual(atlas["regions"].Count,
                manifest["atlas"]["regions"].AsInt());
        }

        /// <summary>
        /// The complete 4.8 roster uses the client's individual Gfx2 sprites.
        /// They carry the same authored frame dimensions as the original atlas
        /// regions, without hand-packing 229 vehicles into replacement sheets.
        /// </summary>
        [Test]
        public void EveryResearchableTankCarriesItsExactCarouselIcon()
        {
            var text = Resources.Load<TextAsset>("Data/atlas_regions");
            Assert.IsNotNull(text, "atlas region table is missing");
            var regions = JsonNode.Parse(text.text)["regions"];
            var database = TanksDatabase.Parse(
                BlitzResources.LoadText(BlitzResources.TanksData));
            Assert.AreEqual(523, database.Count);

            foreach (var nation in new[]
            {
                Nation.Ussr, Nation.Germany, Nation.Usa, Nation.Gb,
                Nation.Japan, Nation.France, Nation.China, Nation.European,
            })
            {
                foreach (var card in database.TreeTanks(nation))
                {
                    var id = TankModelAssets.For(card).DataId;
                    var region = regions["tank_icon_" + id];
                    Assert.IsNotEmpty(region["texture"].AsString(), card.Name);
                    Assert.AreEqual("TankIcons/" + id,
                        region["texture"].AsString(), card.Name);
                    Assert.AreEqual(0, region["x"].AsInt(), card.Name);
                    Assert.AreEqual(0, region["y"].AsInt(), card.Name);
                    Assert.Greater(region["w"].AsInt(), 0, card.Name);
                    Assert.Greater(region["h"].AsInt(), 0, card.Name);
                    Assert.IsNotNull(
                        Resources.Load<Texture2D>("Textures/TankIcons/" + id),
                        card.Name);
                }
            }

            var ms1 = regions["tank_icon_ms1"];
            Assert.AreEqual(218, ms1["w"].AsInt());
            Assert.AreEqual(193, ms1["h"].AsInt());
        }

        /// <summary>
        /// carousel_tank.tscn draws the icon, class and level marks with
        /// stretch_mode KEEP_CENTERED: the source pixel size multiplied by the
        /// node scale, centred inside the node rect. Fitting them to the rect
        /// instead resized every mark on the card.
        /// </summary>
        [Test]
        public void CardMarks_KeepTheirSourceSizeAndStayCentred()
        {
            var icon = HangarScreen.KeepCenteredRect(
                new Rect(0.43f, 4.5f, 195.2f, 122f), new Vector2(218f, 193f), 0.61f);
            Assert.AreEqual(132.98f, icon.width, 0.01f);
            Assert.AreEqual(117.73f, icon.height, 0.01f);
            Assert.AreEqual(31.54f, icon.x, 0.01f);
            Assert.AreEqual(6.635f, icon.y, 0.01f);

            var tankClass = HangarScreen.KeepCenteredRect(
                new Rect(6.48f, 1.92f, 27.04f, 30.16f), new Vector2(48f, 54f), 0.52f);
            Assert.AreEqual(24.96f, tankClass.width, 0.01f);
            Assert.AreEqual(28.08f, tankClass.height, 0.01f);
            Assert.AreEqual(7.52f, tankClass.x, 0.01f);
            Assert.AreEqual(2.96f, tankClass.y, 0.01f);

            var level = HangarScreen.KeepCenteredRect(
                new Rect(32.42f, 2.88f, 29.7f, 30.78f), new Vector2(35f, 57f), 0.54f);
            Assert.AreEqual(18.9f, level.width, 0.01f);
            Assert.AreEqual(30.78f, level.height, 0.01f);
            Assert.AreEqual(37.82f, level.x, 0.01f);
            Assert.AreEqual(2.88f, level.y, 0.01f);
        }

        /// <summary>
        /// Godot buttons draw their icon centred and clamped to the theme's
        /// icon_max_width, keeping the source aspect ratio. Drawing the atlas
        /// region at its native size is what made every hangar icon oversized.
        /// </summary>
        [Test]
        public void ButtonIcons_ClampToGodotIconMaxWidthKeepingAspect()
        {
            var texture = new Texture2D(128, 128);

            // hangar_menu: lobbyCommon_Out 88x62, icon_max_width 50.
            var wide = Sprite.Create(
                texture, new Rect(0f, 0f, 88f, 62f), new Vector2(0.5f, 0.5f));
            var clamped = HangarScreen.IconSize(wide, 50f);
            Assert.AreEqual(50f, clamped.x, 0.001f);
            Assert.AreEqual(35.2273f, clamped.y, 0.01f);

            // hangar_news: 74x60, icon_max_width 38.
            var news = Sprite.Create(
                texture, new Rect(0f, 0f, 74f, 60f), new Vector2(0.5f, 0.5f));
            var newsSize = HangarScreen.IconSize(news, 38f);
            Assert.AreEqual(38f, newsSize.x, 0.001f);
            Assert.AreEqual(30.8108f, newsSize.y, 0.01f);

            // Narrower than the limit means the icon keeps its native size.
            var small = Sprite.Create(
                texture, new Rect(0f, 0f, 40f, 38f), new Vector2(0.5f, 0.5f));
            var native = HangarScreen.IconSize(small, 64f);
            Assert.AreEqual(40f, native.x, 0.001f);
            Assert.AreEqual(38f, native.y, 0.001f);

            Assert.AreEqual(
                Vector2.zero, HangarScreen.IconSize(null, 50f));

            Object.DestroyImmediate(texture);
        }

        [Test]
        public void SourceModelsAndTextures_AreImportableFromResources()
        {
            // The hangar itself no longer ships as a model: its geometry
            // comes out of the bake payload, together with the atlas that lit
            // it, so those two assets are what has to be importable.
            Assert.IsNotNull(Resources.Load<GameObject>(HangarWorld.TankResource));
            Assert.IsNotNull(Resources.Load<TextAsset>("Models/Hangar/lightmap_meshes"));
            Assert.IsNotNull(
                Resources.Load<Texture2D>("Models/Hangar/Lightmaps/hangar_low"));

            var hangar = Resources.Load<Texture2D>("Models/Hangar/Textures/Hangar1");
            var ms1 = Resources.Load<Texture2D>("Models/Tanks/MS1/Textures/MS1");
            var icon = Resources.Load<Texture2D>("Textures/bigTankIcons1");
            Assert.IsNotNull(hangar);
            Assert.IsNotNull(ms1);
            Assert.IsNotNull(icon);
            Assert.AreEqual(2048, hangar.width);
            Assert.AreEqual(1024, ms1.width);
            Assert.AreEqual(2048, icon.width);
        }
    }
}
