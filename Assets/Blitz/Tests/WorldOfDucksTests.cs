using System.Collections.Generic;
using BlitzLegacy.Core.Audio;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Rng;
using BlitzLegacy.Core.Sandbox;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle.WorldOfDucks;
using BlitzLegacy.Runtime.Ui;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>War of Ducks: the event data, its 4.8 assets and the offline-only rules.</summary>
    public sealed class WorldOfDucksTests
    {
        private static WorldOfDucksRules Load()
        {
            return WorldOfDucksRules.Parse(BlitzResources.LoadText(WorldOfDucksRules.DataResource));
        }

        [Test]
        public void EventDataParses()
        {
            var rules = Load();
            Assert.AreEqual("sea_of_ducks", rules.MapId);
            Assert.AreEqual(7, rules.TeamSize);
            Assert.AreEqual(420, rules.TimeLimitSeconds);
            Assert.IsFalse(rules.FriendlyFire);
            Assert.IsFalse(rules.BaseCapture);
            var card = rules.CreateCard();
            Assert.IsNotNull(card);
            Assert.GreaterOrEqual(card.Guns.Count, WorldOfDucksRules.GunCount);
            for (var i = 0; i < WorldOfDucksRules.GunCount; i++)
            {
                Assert.AreEqual(i, WorldOfDucksBattle.GunIndexOf(card, card.Guns[i].Name));
            }
            Assert.IsTrue(rules.Document["mapEffects"].Count > 0);
            Assert.IsTrue(rules.Document["mapSounds"].Count > 0);
        }

        [Test]
        public void TeamSizeFollowsTheMap()
        {
            var rules = Load();
            Assert.AreEqual(7, rules.TeamSizeFor(15));
            Assert.AreEqual(4, rules.TeamSizeFor(4));
        }

        [Test]
        public void GunForSlotIsSeededAndInRange()
        {
            var seen = new HashSet<int>();
            for (var seed = 0L; seed < 20; seed++)
            {
                for (var team = 0; team < 2; team++)
                {
                    for (var slot = 0; slot < 7; slot++)
                    {
                        var gun = WorldOfDucksRules.GunForSlot(seed, team, slot);
                        Assert.That(gun, Is.InRange(0, WorldOfDucksRules.GunCount - 1));
                        Assert.AreEqual(gun, WorldOfDucksRules.GunForSlot(seed, team, slot));
                        seen.Add(gun);
                    }
                }
            }
            Assert.AreEqual(WorldOfDucksRules.GunCount, seen.Count);
            Assert.AreEqual(0, WorldOfDucksRules.GunForSlot(5, 1, 3, 1));
        }

        [Test]
        public void ModeIsOfflineOnly()
        {
            Assert.Contains(BattleMode.WorldOfDucks, BattleModeCatalog.MenuModes);
            Assert.AreEqual("world_of_ducks", BattleModeCatalog.Id(BattleMode.WorldOfDucks));
            Assert.IsTrue(BattleModeCatalog.OfflineOnly(BattleMode.WorldOfDucks));
            Assert.IsTrue(BattleModeCatalog.OfflineOnly(BattleMode.Sandbox));
            Assert.IsFalse(BattleModeCatalog.OfflineOnly(BattleMode.Normal));
            Assert.IsFalse(BattleModeCatalog.AllowsOnline(BattleMode.WorldOfDucks));
        }

        [Test]
        public void ResultPaysNothing()
        {
            var result = new BattleResultData
            {
                PremiumAccount = true, CreditsEarned = 900, CreditCosts = 50, Credits = 850,
                Gold = 3, Xp = 400, FreeXp = 20,
            };
            result.Rewards.Add(new BattleRewardData { Id = "x", Gold = 1 });
            result.Participants.Add(new BattleParticipantResult { IsPlayer = true, Damage = 120, Kills = 2, Xp = 400, XpRaw = 300 });
            WorldOfDucksRules.StripProgression(result);
            Assert.AreEqual(0, result.CreditsEarned + result.CreditCosts + result.Credits + result.Gold + result.Xp + result.FreeXp);
            Assert.IsFalse(result.PremiumAccount);
            Assert.AreEqual(0, result.Rewards.Count);
            Assert.AreEqual(0, result.Participants[0].Xp + result.Participants[0].XpRaw);
            Assert.AreEqual(120, result.Participants[0].Damage);
            Assert.AreEqual(2, result.Participants[0].Kills);
        }

        /// <summary>Any shell sound that is neither a penetration nor a bounce.</summary>
        private static ShellSoundKind Surface()
        {
            foreach (ShellSoundKind kind in System.Enum.GetValues(typeof(ShellSoundKind)))
            {
                if (kind != ShellSoundKind.Pen && kind != ShellSoundKind.Crit
                    && kind != ShellSoundKind.Bounce && kind != ShellSoundKind.Ricoshet) { return kind; }
            }
            return (ShellSoundKind)99;
        }

        [Test]
        public void ImpactsClassifyBySurface()
        {
            Assert.AreEqual(DuckImpactSurface.Pierced, WorldOfDucksBattle.Classify(ShellSoundKind.Pen, "hull", 20f));
            Assert.AreEqual(DuckImpactSurface.Resisted, WorldOfDucksBattle.Classify(ShellSoundKind.Ricoshet, "hull", 20f));
            Assert.AreEqual(DuckImpactSurface.Water, WorldOfDucksBattle.Classify(Surface(), "SeaTile_04", 30f));
            Assert.AreEqual(DuckImpactSurface.Water, WorldOfDucksBattle.Classify(Surface(), "rock", WorldOfDucksRules.WaterLevel));
            Assert.AreEqual(DuckImpactSurface.Ground, WorldOfDucksBattle.Classify(Surface(), "rock", 25f));
            Assert.AreEqual("hit_surface/DeepWaterHitMedium_HE2", WorldOfDucksBattle.ImpactScene(DuckImpactSurface.Water, true));
            Assert.AreEqual("world_of_ducks/duck_hits_03", WorldOfDucksBattle.ClipName("duck_hits", 3));
        }

        [Test]
        public void WaterDragKeepsTopSpeedAndKillsTheSlide()
        {
            var drag = WorldOfDucksTank.WaterDrag(new Vector3(4f, 2f, 10f), Vector3.forward, Vector3.right);
            Assert.AreEqual(0f, drag.y, 1e-5f);
            Assert.Less(drag.x, 0f);
            Assert.LessOrEqual(Mathf.Abs(drag.z), Mathf.Abs(drag.x) * 10f / 4f);
            Assert.Less(WorldOfDucksTank.CollisionVolume(0f), WorldOfDucksTank.CollisionVolume(1e6f) + 1e-5f);
            Assert.IsTrue(WorldOfDucksTank.InWater(WorldOfDucksRules.WaterLevel + WorldOfDucksTank.InWaterOffset));
        }

        [Test]
        public void EventSamplesLoad()
        {
            var groups = new Dictionary<string, int>
            {
                { "duck_collision", 3 }, { "duck_death", 4 }, { "duck_gui", 4 }, { "duck_gun", 4 },
                { "duck_hits", 5 }, { "duck_hits_squek", 3 }, { "duck_hits_water", 5 },
                { "duck_not_pierce", 3 }, { "duck_torpedo", 3 }, { "duck_water_gun", 5 },
                { "river", 3 }, { "seagull", 3 },
            };
            foreach (var pair in groups)
            {
                for (var i = 1; i <= pair.Value; i++)
                {
                    var clip = WorldOfDucksBattle.ClipName(pair.Key, i);
                    Assert.IsNotNull(BlitzResources.LoadAudio(clip), clip);
                }
            }
            Assert.IsNotNull(BlitzResources.LoadAudio("world_of_ducks/duck_surfaces_water_moving"));
            Assert.IsNotNull(BlitzResources.LoadAudio("world_of_ducks/lake_01"));
        }

        [Test]
        public void ModeCardAndMapAssetsExist()
        {
            Assert.IsNotNull(Resources.Load<Texture2D>("Textures/WorldOfDucks/mode_card"));
            Assert.IsNotNull(BlitzResources.LoadText("map_sea_of_ducks"));
        }

        /// <summary>The loading team lists and the scoreboard (small icon) and the queue (big
        /// icon) draw the event card with the 4.8 WarDuck art of the event pack.</summary>
        [Test]
        public void EventCardCarriesItsSmallAndBigBattleIcons()
        {
            var card = Load().CreateCard();
            var atlas = BattleFlowUi.LoadAtlas();
            var small = atlas.Get(BattleFlowUi.SmallTankIconRegion(card));
            Assert.IsNotNull(small, "small icon");
            Assert.AreEqual("warduck_small", small.texture.name);
            Assert.AreEqual(new Vector2(47f, 47f), small.rect.size);
            var big = atlas.Get("tank_icon_" + TankModelAssets.For(card).DataId);
            Assert.IsNotNull(big, "big icon");
            Assert.AreEqual("warduck_big", big.texture.name);
            Assert.AreEqual(new Vector2(161f, 194f), big.rect.size);
        }

        /// <summary>The shared WarDuck card belongs to World of Ducks: no bot of another mode
        /// rolls it, and neither the hangar nor the Sandbox offers it.</summary>
        [Test]
        public void SharedWarDuckStaysInsideTheEvent()
        {
            var tanks = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
            var duck = tanks.Get("oth10_warduck");
            Assert.IsNotNull(duck);
            Assert.IsTrue(duck.Hidden);
            Assert.IsTrue(duck.IgnoreMatchmaker);
            Assert.IsFalse(tanks.HasVisible(duck.Name));
            Assert.IsTrue(tanks.HasVisible("ms-1"));

            var random = new SeededRandomSource(1120);
            var matchmaker = new Matchmaker(tanks, random);
            for (var i = 0; i < 2000; i++)
            {
                Assert.AreNotEqual(duck.Name, matchmaker.GetByLevelRandom(duck.Level, i % 2 == 0));
                Assert.AreNotEqual(duck.Name, matchmaker.RollBotName(duck.Level, duck.Level));
            }

            CollectionAssert.DoesNotContain(SandboxEditor.PickerTanks(tanks, info => info.Name), duck);
            var slot = new SandboxBotSlot { RandomTank = false, TankName = duck.Name };
            Assert.IsFalse(SandboxEditor.IsExact(slot, tanks));
            var player = tanks.Get("ms-1");
            Assert.AreEqual(duck.Level, player.Level);
            Assert.AreNotEqual(duck.Name, SandboxRosterPlanner.PickName(new SandboxConfig(), slot, tanks, matchmaker, player, random));
        }
    

        [Test]
        public void FloatingHeight_RaisesSeaBedSpawnersOntoTheFloor()
        {
            var plane = BlitzLegacy.Core.Battle.WorldOfDucksRules.CollisionPlaneLevel;
            Assert.AreEqual(plane, BlitzLegacy.Core.Battle.WorldOfDucksRules.FloatingHeight(7.37f));
            Assert.AreEqual(20f, BlitzLegacy.Core.Battle.WorldOfDucksRules.FloatingHeight(20f));
            Assert.IsTrue(BlitzLegacy.Runtime.Battle.WorldOfDucks.WorldOfDucksTank.InWater(plane));
            Assert.IsTrue(BlitzLegacy.Runtime.Battle.BattleObjects.IsFloorPlainName("entity_154_CollisionPlain_sc2"));
            Assert.IsFalse(BlitzLegacy.Runtime.Battle.BattleObjects.IsFloorPlainName("entity_1_sea_surf_sc2"));
        }

        [Test]
        public void SeaOfDucks_WheelRaysLandOnTheFloorNotTheSeaBed()
        {
            var root = new UnityEngine.GameObject("sea-of-ducks-floor-test");
            try
            {
                var world = BlitzLegacy.Runtime.Battle.BattleWorld.Create(root.transform, "sea_of_ducks");
                Assert.Greater(world.Objects.FloorSlabCount, 0, "no CollisionPlain slab was built");
                UnityEngine.Physics.SyncTransforms();
                var spawns = new System.Collections.Generic.List<UnityEngine.Transform>(world.PlayerSpawns);
                spawns.AddRange(world.BotSpawns);
                Assert.Greater(spawns.Count, 0);
                foreach (var spawn in spawns)
                {
                    var p = spawn.position;
                    UnityEngine.RaycastHit hit;
                    Assert.IsTrue(UnityEngine.Physics.Raycast(new UnityEngine.Vector3(p.x, 60f, p.z),
                            UnityEngine.Vector3.down, out hit, 120f, BlitzLegacy.Runtime.Battle.BattleLayers.WheelMask,
                            UnityEngine.QueryTriggerInteraction.Ignore),
                        spawn.name + " has no ground");
                    Assert.AreEqual(BlitzLegacy.Core.Battle.WorldOfDucksRules.CollisionPlaneLevel, hit.point.y, 0.05f,
                        spawn.name + " rides on " + hit.collider.name);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void TorpedoWake_StripeCurvesAreConverted()
        {
            // Left as raw 4.8 keyframe lists they read as (0, 1, 0, 1) - the
            // green rectangle - and as a constant 8 m wide ribbon.
            var layer = JsonResources.Load("Particles/WorldOfDucks/Sources/gun_effects/shot_torpedo")
                ["legacyEmitters"][0]["emitter"]["layers"][0];
            Assert.AreEqual("particlesStripe", layer["layerType"].AsString());
            Assert.IsTrue(layer["stripeInheritPositionForBase"].AsBool());
            for (var channel = 0; channel < 3; channel++)
            {
                Assert.AreEqual(1.0, DavaProperty.Component(layer["stripeColorOverLife"], 0.5, channel, -1), 1e-3, "channel " + channel);
            }
            Assert.AreEqual(1.0, DavaProperty.Component(layer["stripeColorOverLife"], 0.0, 3, -1), 1e-3);
            Assert.AreEqual(0.0, DavaProperty.Component(layer["stripeColorOverLife"], 1.0, 3, -1), 1e-3);
            Assert.AreEqual(0.1, DavaProperty.Scalar(layer["stripeSizeOverLifeProp"], 0.0, -1), 1e-3);
            Assert.AreEqual(1.0, DavaProperty.Scalar(layer["stripeSizeOverLifeProp"], 1.0, -1), 1e-3);
        }

        private static readonly string[] ScalarCurveKeys =
        {
            "alphaOverLife", "velocityOverLife", "animSpeedOverLife", "stripeSizeOverLifeProp",
            "stripeTextureTileOverLife", "noiseScaleOverLife", "noiseUScrollSpeedOverLife", "noiseVScrollSpeedOverLife",
        };

        private static readonly string[] ColourCurveKeys = { "colorOverLife", "colorRandom", "stripeColorOverLife" };

        [Test]
        public void EventScenes_HaveNoRawKeyframeCurves()
        {
            var scenes = JsonResources.Load("Data/world_of_ducks_fx_manifest")["scenes"];
            Assert.Greater(scenes.Count, 0);
            foreach (var scene in scenes.Fields)
            {
                var source = JsonResources.Load("Particles/WorldOfDucks/Sources/" + scene.Key);
                Assert.IsNotNull(source, scene.Key);
                AssertConvertedCurves(source, scene.Key);
            }
        }

        private static void AssertConvertedCurves(JsonNode node, string scene)
        {
            if (node.Kind == JsonKind.Array)
            {
                foreach (var item in node.Items) { AssertConvertedCurves(item, scene); }
                return;
            }
            if (node.Kind != JsonKind.Object) { return; }
            foreach (var key in ScalarCurveKeys)
            {
                var value = node[key];
                if (value.Kind != JsonKind.Array) { continue; }
                Assert.IsTrue(value[0].AsInt() == 2 && value[1].Kind == JsonKind.Array, scene + ": raw " + key);
            }
            foreach (var key in ColourCurveKeys)
            {
                var value = node[key];
                if (value.Kind != JsonKind.Array) { continue; }
                for (var channel = 0; channel < 4; channel++)
                {
                    foreach (var time in new[] { 0.0, 0.5, 1.0 })
                    {
                        Assert.That(DavaProperty.Component(value, time, channel, -1), Is.InRange(0.0, 1.0 + 1e-6),
                            scene + ": " + key + " channel " + channel);
                    }
                }
            }
            foreach (var field in node.Fields) { AssertConvertedCurves(field.Value, scene); }
        }

        [Test]
        public void TorpedoWake_RidesTheShellAndFadesInBehindIt()
        {
            var root = new GameObject("ducks-wake-test");
            try
            {
                var camera = new GameObject("camera").AddComponent<Camera>();
                camera.transform.SetParent(root.transform);
                camera.transform.position = new Vector3(12f, 20f, 15f);
                camera.transform.LookAt(new Vector3(0f, 0f, 15f));
                var fx = BlitzLegacy.Runtime.Battle.DavaFx.CreateWorldOfDucks(root.transform, "gun_effects/shot_torpedo", 30f, true);
                for (var frame = 1; frame <= 90; frame++)
                {
                    fx.transform.position = new Vector3(0f, 0f, frame * 30f / 90f);
                    fx.Simulate(1f / 60f);
                }
                fx.Render(camera);
                MeshFilter ribbon = null;
                foreach (var filter in fx.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (filter.gameObject.name == "treil") { ribbon = filter; }
                }
                Assert.IsNotNull(ribbon, "no torpedo ribbon");
                var vertices = ribbon.sharedMesh.vertices;
                var colours = ribbon.sharedMesh.colors;
                Assert.Greater(vertices.Length, 8);
                float farthest = float.MinValue, nearest = float.MaxValue, headAlpha = 1f, bodyAlpha = 0f;
                for (var i = 0; i < vertices.Length; i++)
                {
                    var z = ribbon.transform.TransformPoint(vertices[i]).z;
                    farthest = Mathf.Max(farthest, z);
                    // The 4.8 white envelope times the layer's sea tint, never the raw green.
                    Assert.GreaterOrEqual(colours[i].r, colours[i].g * 0.5f, "green ribbon vertex " + i);
                    var behind = fx.transform.position.z - z;
                    if (behind < nearest) { nearest = behind; headAlpha = colours[i].a; }
                    if (behind > 12f) { bodyAlpha = Mathf.Max(bodyAlpha, colours[i].a); }
                }
                Assert.Greater(farthest, 20f, "the ribbon stayed at the launch point");
                Assert.Less(headAlpha, 0.1f, "stripeFadeDistanceFromTop: the ribbon must fade in behind the shell");
                Assert.Greater(bodyAlpha, 0.1f);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void TorpedoWake_StaysOneRibbonForTheWholeRun()
        {
            // The green rectangle: the stripe base lives 4 s and did not ride
            // the torpedo, so when it respawned under the shell the ribbon
            // joined the launch point to the shell with one raw-green (0, 1, 0)
            // quad, full 8 m wide (the raw size line fell back to 1).
            var root = new GameObject("ducks-wake-run-test");
            try
            {
                var camera = new GameObject("camera").AddComponent<Camera>();
                camera.transform.SetParent(root.transform);
                var fx = BlitzLegacy.Runtime.Battle.DavaFx.CreateWorldOfDucks(root.transform, "gun_effects/shot_torpedo", 30f, true);
                const float speed = 50f, dt = 1f / 60f;
                var checks = 0;
                for (var frame = 1; frame <= 330; frame++)
                {
                    var z = frame * dt * speed;
                    fx.transform.position = new Vector3(0f, 0f, z);
                    fx.Simulate(dt);
                    if (frame != 270 && frame != 330) { continue; }
                    checks++;
                    camera.transform.position = new Vector3(40f, 120f, z - 80f);
                    camera.transform.LookAt(new Vector3(0f, 0f, z - 80f));
                    fx.Render(camera);
                    MeshFilter ribbon = null;
                    foreach (var filter in fx.GetComponentsInChildren<MeshFilter>(true))
                    {
                        if (filter.gameObject.name == "treil") { ribbon = filter; }
                    }
                    Assert.IsNotNull(ribbon, "no torpedo ribbon at frame " + frame);
                    var vertices = ribbon.sharedMesh.vertices;
                    var colours = ribbon.sharedMesh.colors;
                    Assert.Greater(vertices.Length, 40, "frame " + frame);
                    float longest = 0f, tail = float.MaxValue, widest = 0f;
                    for (var i = 0; i + 1 < vertices.Length; i += 2)
                    {
                        var a = ribbon.transform.TransformPoint(vertices[i]);
                        var b = ribbon.transform.TransformPoint(vertices[i + 1]);
                        widest = Mathf.Max(widest, Vector3.Distance(a, b));
                        var centre = (a + b) * 0.5f;
                        tail = Mathf.Min(tail, centre.z);
                        if (i >= 2)
                        {
                            var previous = (ribbon.transform.TransformPoint(vertices[i - 2]) + ribbon.transform.TransformPoint(vertices[i - 1])) * 0.5f;
                            longest = Mathf.Max(longest, Vector3.Distance(previous, centre));
                        }
                        Assert.GreaterOrEqual(colours[i].r, colours[i].g * 0.5f, "green ribbon vertex " + i + " at frame " + frame);
                    }
                    Assert.Less(longest, 10f, "a ribbon segment spans the run at frame " + frame);
                    Assert.Greater(z - tail, 100f, "the ribbon does not trail the shell at frame " + frame);
                    Assert.Less(widest, 8.5f, "frame " + frame);
                }
                Assert.AreEqual(2, checks);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void SurfaceScenesSitJustAboveTheWater()
        {
            Assert.Greater(WorldOfDucksBattle.SurfaceLift, 0f);
            Assert.Less(WorldOfDucksBattle.SurfaceLift, 0.2f);
            var root = new GameObject("ducks-death-fx-test");
            try
            {
                // Fire_and_Smoke hangs its whirl 2 m above (and its foam 0.4 m
                // under) the HP_Fire point the WarDuck does not have.
                var fx = BlitzLegacy.Runtime.Battle.DavaFx.CreateWorldOfDucks(root.transform, "Fire_and_Smoke", 30f, true);
                var heights = new List<float>(fx.EmitterHeights);
                Assert.IsTrue(heights.Exists(h => Mathf.Abs(h - 2f) < 0.01f), string.Join(", ", heights));
                fx.LevelEmitters(0f);
                foreach (var height in fx.EmitterHeights) { Assert.AreEqual(0f, height, 1e-5f); }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }
    }
}
