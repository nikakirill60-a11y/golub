using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace BlitzLegacy.Tests
{
    public sealed class BattleCombatTests
    {
        private const float Tolerance = 1e-4f;
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
        public void ReleaseMetadataMatchesLoginVersion()
        {
            Assert.AreEqual("2.0b1", BlitzLegacy.Core.BlitzVersion.Current);
            Assert.AreEqual(205, BlitzLegacy.Core.BlitzVersion.Code);
            var settings = System.IO.File.ReadAllText("ProjectSettings/ProjectSettings.asset");
            StringAssert.Contains("bundleVersion: " + BlitzLegacy.Core.BlitzVersion.Current, settings);
            StringAssert.Contains("AndroidBundleVersionCode: " + BlitzLegacy.Core.BlitzVersion.Code, settings);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SpottingPreservesScopeOnlyArmorAndOutlineInBothViews(bool sniper)
        {
            var session = BattleSession.Create(null, false, autoStart: true);
            _root = session.gameObject;
            var target = session.Enemies[0];
            session.View.SetSniperMode(sniper);
            target.SetSpotted(true);
            session.Player.AimTraceHit(target.Plates[0].Shape);
            Assert.AreEqual(sniper, target.ArmorShown);
            Assert.IsTrue(target.OutlineShown);
            var outline = OutlineAlpha(target);
            for (var frame = 0; frame < 3; frame++)
            {
                target.RefreshSpottedVisibility();
                foreach (var plate in target.Plates)
                    Assert.AreEqual(sniper, plate.Preview.enabled,
                        "Spotting must not enable a scope-only overlay: " + plate.PlateName);
            }
            Assert.AreEqual(outline, OutlineAlpha(target), Tolerance);
            target.SetSpotted(false);
            foreach (var plate in target.Plates) Assert.IsFalse(plate.Preview.enabled);
            target.SetSpotted(true);
            foreach (var plate in target.Plates) Assert.AreEqual(sniper, plate.Preview.enabled);
        }

        [Test]
        public void LeavingScopeImmediatelyClearsEveryArmorOverlayButKeepsOutlines()
        {
            var session = BattleSession.Create(null, false, autoStart: true);
            _root = session.gameObject;
            session.View.SetSniperMode(true);
            foreach (var target in session.Enemies)
            {
                target.SetSpotted(true);
                target.SwitchArmorOutline(true, true, BattleTank.ArmorPreviewHold, session.Player.GunInfo.Shells[0]);
            }
            session.View.SetSniperMode(false);
            foreach (var target in session.Enemies)
            {
                Assert.IsFalse(target.ArmorShown, "Scope exit must not wait for the hold timer");
                Assert.IsTrue(target.OutlineShown);
                foreach (var plate in target.Plates) Assert.IsFalse(plate.Preview.enabled);
                target.RefreshSpottedVisibility();
                foreach (var plate in target.Plates) Assert.IsFalse(plate.Preview.enabled);
            }
        }

        /// <summary>
        /// A guided rocket draws the client's own rocket trail
        /// (<c>trace_HC_3</c> / <c>trace_HE_4</c>): one unbroken column on a
        /// much faster clock that stays up far longer than a shell ribbon, and
        /// the high-explosive rocket paints it pale instead of HEAT gold.
        /// </summary>
        [Test]
        public void GuidedRocket_DrawsItsOwnTracerInsteadOfTheShellRibbon()
        {
            var rocket = new ShellInfo
            {
                Type = ShellType.He,
                Atgm = true,
                Speed = 75f,
                TurnSpeed = 0.5f,
                MaxDistance = 720f,
            };
            var shell = new ShellInfo { Type = ShellType.Ap, Speed = 442f };

            var forced = BlitzLegacy.Runtime.Battle.CombatFx.GodotParticleEffect.ForceEnabled;
            BlitzLegacy.Runtime.Battle.CombatFx.GodotParticleEffect.ForceEnabled = true;
            var guided = BattleProjectile.Create(
                rocket, null, Vector3.zero, Vector3.zero, Vector3.forward);
            var plain = BattleProjectile.Create(
                shell, null, Vector3.zero, Vector3.zero, Vector3.forward);

            try
            {
                Assert.IsTrue(guided.Guided);
                Assert.IsTrue(guided.Trail.Guided);
                Assert.AreEqual(
                    BattleShellTrail.AtgmRepaintInterval,
                    guided.Trail.Interval,
                    Tolerance);
                Assert.AreEqual(
                    BattleShellTrail.AtgmSegmentLifeTime,
                    guided.Trail.SegmentLife,
                    Tolerance);
                Assert.AreEqual(
                    BattleShellTrail.AtgmExplosiveColour, guided.Trail.Colour);

                Assert.IsNull(guided.Tracer);
                Assert.IsNull(guided.Sprite);

                // An ordinary shell draws the GPUTrail3D pair and its sprite.
                Assert.IsFalse(plain.Guided);
                Assert.IsNull(plain.Trail);
                Assert.IsNotNull(plain.Tracer);
                Assert.AreEqual(2, plain.Tracer.Emitters.Count);
                Assert.IsFalse(plain.Tracer.Heat);
                Assert.IsNotNull(plain.Sprite);
            }
            finally
            {
                BlitzLegacy.Runtime.Battle.CombatFx.GodotParticleEffect.ForceEnabled = forced;
                Object.DestroyImmediate(guided.Trail.gameObject);
                Object.DestroyImmediate(guided.gameObject);
                if (plain.Tracer != null) Object.DestroyImmediate(plain.Tracer.gameObject);
                Object.DestroyImmediate(plain.gameObject);
            }
        }

        [Test]
        public void ReloadClock_IsStrictAndReportsAFullLoadedRing()
        {
            Assert.IsFalse(GunMath.CanShoot(5f, 5f));
            Assert.IsTrue(GunMath.CanShoot(5.001f, 5f));
            Assert.AreEqual(4f, GunMath.ReloadRemaining(2f, 6f, 5f), Tolerance);
            Assert.AreEqual(5f, GunMath.ReloadRemaining(7f, 6f, 5f), Tolerance);
            Assert.AreEqual(0.8f, GunMath.ReloadCoefficient(4f, 5f), Tolerance);
            Assert.AreEqual(1f, GunMath.ReloadCoefficient(5f, 5f), Tolerance);
        }

        [Test]
        public void JustShotWindow_EndsAfterAQuarterSecond()
        {
            Assert.IsTrue(GunMath.JustShot(10.25f, 10f));
            Assert.IsFalse(GunMath.JustShot(10.251f, 10f));
        }

        [Test]
        public void ConeAndRecoil_MatchTheSourceScalars()
        {
            Assert.AreEqual(0.4f, GunMath.ConeMultiplier(0.5f, 0.5f), Tolerance);
            Assert.AreEqual(0.5f, GunMath.ConeMultiplier(0.5f, 1f), Tolerance);
            Assert.AreEqual(0.75f, GunMath.ConeMultiplier(0.5f, 10f), Tolerance);

            var angle = GunMath.ConeAngle(100f, 0.5f);
            Assert.AreEqual(Mathf.Atan(0.033f * 0.5f), angle, Tolerance);
            Assert.AreEqual(
                0.4f * Mathf.Pow(45f, BattleTank.RecoilHeight + 0.15f),
                GunMath.Recoil(0.4f, 45),
                0.001f);
        }

        [Test]
        public void DamagePenetrationAndLifetime_ClampLikeGodot()
        {
            Assert.AreEqual(75, GunMath.DamageRoll(100f, 0f));
            Assert.AreEqual(125, GunMath.DamageRoll(100f, 2f));
            Assert.AreEqual(95, GunMath.PenetrationRoll(100f, 0f));
            Assert.AreEqual(105, GunMath.PenetrationRoll(100f, 2f));
            Assert.AreEqual(2f, GunMath.ShellLifetime(720f, -360f), Tolerance);
            Assert.AreEqual(0f, GunMath.ShellLifetime(720f, 0f), Tolerance);
        }

        [Test]
        public void HitResolver_UsesOvermatchThenRicochetThenThickness()
        {
            Assert.AreEqual(
                HitOutcome.Overmatch,
                HitResolver.ResolveArmor(ShellType.Ap, 61, 1f, 20, Mathf.PI * 0.49f, false));
            Assert.AreEqual(
                HitOutcome.Ricochet,
                HitResolver.ResolveArmor(ShellType.Ap, 45, 500f, 30, 75f * Mathf.Deg2Rad, false));
            Assert.AreEqual(
                HitOutcome.Penetration,
                HitResolver.ResolveArmor(ShellType.Ap, 45, 500f, 30, 0f, false));
            Assert.AreEqual(
                HitOutcome.Bounce,
                HitResolver.ResolveArmor(ShellType.Ap, 45, 1f, 30, 0f, false));
        }

        [Test]
        public void ScreenAndDestructibleCosts_AreAppliedBeforeTheNextHit()
        {
            Assert.AreEqual(70f, HitResolver.PenetrationAfterPlate(100f, 30f), Tolerance);
            Assert.AreEqual(
                67f,
                HitResolver.PenetrationAfterGap(ShellType.Ap, 100f, 1f),
                Tolerance);
            Assert.AreEqual(
                0f,
                HitResolver.PenetrationAfterGap(ShellType.Heat, 100f, 1f),
                Tolerance);
            Assert.AreEqual(120, HitResolver.DestructibleDamage(ShellType.He, 60, 45f));
            Assert.AreEqual(90, HitResolver.DestructibleDamage(ShellType.Ap, 60, 45f));
            Assert.AreEqual(35f, HitResolver.PenetrationAfterDestructible(45f, 10), Tolerance);
        }

        [Test]
        public void RandomCone_NeverLeavesItsRequestedHalfAngle()
        {
            Random.InitState(9917);
            var direction = new Vector3(0.2f, 0.95f, 0.1f).normalized;
            var requested = 12f * Mathf.Deg2Rad;
            for (var i = 0; i < 128; i++)
            {
                var rolled = BattleTank.RandomVectorInCone(direction, requested);
                Assert.AreEqual(1f, rolled.magnitude, 0.0001f);
                Assert.LessOrEqual(Vector3.Angle(direction, rolled), 6.001f);
            }
        }

        [Test]
        public void SpawnedMs1_OwnsGunHealthAndTheInstalledArmorPlates()
        {
            _root = new GameObject("BattleCombatTests");
            var spawn = new GameObject("Spawn").transform;
            spawn.SetParent(_root.transform, false);
            spawn.position = new Vector3(12f, 3f, -5f);
            spawn.rotation = Quaternion.Euler(0f, 180f, 0f);

            var tank = BattleTank.Spawn(
                _root.transform,
                "TargetMS1",
                LoadMs1(),
                spawn,
                new Bounds(Vector3.zero, new Vector3(1000f, 100f, 1000f)),
                1);
            tank.Blocked = true;
            tank.TurretBlock = true;

            Assert.AreEqual(1, tank.Team);
            Assert.IsTrue(tank.Blocked);
            Assert.IsTrue(tank.TurretBlock);
            Assert.IsNotNull(tank.Gun);
            Assert.AreSame(tank.GunInfo, tank.Gun.Info);
            Assert.Greater(tank.GunInfo.Shells.Count, 0);
            Assert.IsNotNull(tank.Health);
            // 160 hull plus the 40 of the turret it spawned with: a Blitz
            // tank fights with the sum of the two pools.
            Assert.AreEqual(200, tank.Health.MaxHealth);
            Assert.AreEqual(200, tank.Health.Health);
            // The five authored hull plates, the four of the installed turret
            // and the two faces of its mantlet; the other turret and the other
            // mantlets stay off. The twelfth body is module_tracks, the track
            // module the source hangs off the hull next to the armour.
            Assert.AreEqual(12, tank.Plates.Count);

            for (var i = 0; i < tank.Plates.Count; i++)
            {
                var plate = tank.Plates[i];
                Assert.AreSame(tank, plate.Tank);
                Assert.IsNotNull(plate.Shape);
                Assert.IsFalse(plate.Shape.convex);
                Assert.AreEqual(
                    plate.Screen ? BattleLayers.Screen : BattleLayers.Armor,
                    plate.gameObject.layer);
            }
        }

        /// <summary>
        /// A tank is the one thing the map bake cannot carry. Its serialized
        /// URP variants must remain opaque and textured in a player, while the
        /// source SPECULAR=0 prevents both a sun highlight and a sky-glass coat.
        /// </summary>
        [Test]
        public void SpawnedMs1_UsesPinnedOpaqueTexturedMaterials()
        {
            var bodyPreset = Resources.Load<Material>(BattleTank.BodyMaterialResource);
            var trackPreset = Resources.Load<Material>(BattleTank.TrackMaterialResource);
            var texture = Resources.Load<Texture2D>(BattleTank.TextureResource);
            var trackTexture = Resources.Load<Texture2D>(
                TankModelAssets.For(TankModelAssets.DefaultTankName).TrackTextureResource);
            Assert.IsNotNull(bodyPreset, "the opaque Android shader variant is not pinned");
            Assert.IsNotNull(trackPreset, "the cutout Android shader variant is not pinned");
            Assert.IsNotNull(texture);
            Assert.IsNotNull(trackTexture);

            _root = new GameObject("BattleCombatLighting");
            var spawn = new GameObject("Spawn").transform;
            spawn.SetParent(_root.transform, false);

            var tank = BattleTank.Spawn(
                _root.transform,
                "LitMS1",
                LoadMs1(),
                spawn,
                new Bounds(Vector3.zero, new Vector3(1000f, 100f, 1000f)),
                0);

            var renderers = tank.GetComponentsInChildren<MeshRenderer>(true);
            Assert.Greater(renderers.Length, 0, "the tank has to draw something");

            foreach (var renderer in renderers)
            {
                // The armour overlay of the sniper scope is not tank paint: it
                // is an additive, unlit, shadowless pass that stays off until
                // the scope asks for it, so it is checked on its own.
                if (renderer.gameObject.layer == BattleLayers.Armor ||
                    renderer.gameObject.layer == BattleLayers.Screen)
                {
                    continue;
                }

                Assert.AreEqual(
                    ShadowCastingMode.On, renderer.shadowCastingMode, renderer.name);
                Assert.IsTrue(renderer.receiveShadows, renderer.name);

                var materials = renderer.sharedMaterials;
                Assert.Greater(materials.Length, 0, renderer.name);
                for (var materialIndex = 0;
                    materialIndex < materials.Length; materialIndex++)
                {
                    var material = materials[materialIndex];
                    Assert.IsNotNull(material, renderer.name);
                    Assert.IsNotNull(material.shader, material.name);
                    Assert.IsTrue(material.shader.isSupported, material.name);

                    // The targeting outline rides along as one more pass on
                    // every tank material, the way the source hangs it off
                    // next_pass. It is unlit and invisible until the reticle
                    // asks for it, so it is not tank paint either.
                    if (material.shader.name == TankOutlineShader.Name ||
                        material.shader.name == TankOutlineShader.MaskName)
                    {
                        continue;
                    }

                    Assert.AreEqual(BattleTank.TankShaderName,
                        material.shader.name, material.name);
                    if (material.HasProperty("_BaseMap"))
                    {
                        var expectedTexture = material.name.Contains("Track")
                            ? trackTexture
                            : texture;
                        Assert.AreSame(expectedTexture,
                            material.GetTexture("_BaseMap"), material.name);
                    }
                    if (material.HasProperty("_Surface"))
                    {
                        Assert.AreEqual(0f,
                            material.GetFloat("_Surface"), Tolerance, material.name);
                    }
                    if (material.HasProperty("_SrcBlend"))
                    {
                        Assert.AreEqual((float)BlendMode.One,
                            material.GetFloat("_SrcBlend"), Tolerance, material.name);
                    }
                    if (material.HasProperty("_DstBlend"))
                    {
                        Assert.AreEqual((float)BlendMode.Zero,
                            material.GetFloat("_DstBlend"), Tolerance, material.name);
                    }
                    if (material.HasProperty("_Cull"))
                    {
                        Assert.AreEqual((float)CullMode.Back,
                            material.GetFloat("_Cull"), Tolerance, material.name);
                    }
                    if (material.HasProperty("_ZWrite"))
                    {
                        Assert.AreEqual(1f,
                            material.GetFloat("_ZWrite"), Tolerance, material.name);
                    }
                    if (material.HasProperty("_SpecularHighlights"))
                    {
                        Assert.AreEqual(0f,
                            material.GetFloat("_SpecularHighlights"),
                            Tolerance,
                            material.name);
                    }
                    if (material.HasProperty("_EnvironmentReflections"))
                    {
                        Assert.AreEqual(0f,
                            material.GetFloat("_EnvironmentReflections"),
                            Tolerance,
                            material.name);
                    }

                    Assert.IsFalse(
                        material.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT"),
                        material.name);
                    Assert.IsTrue(
                        material.IsKeywordEnabled("_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A"),
                        material.name);
                    Assert.IsTrue(
                        material.IsKeywordEnabled("_SPECULARHIGHLIGHTS_OFF"),
                        material.name);
                    Assert.IsTrue(
                        material.IsKeywordEnabled("_ENVIRONMENTREFLECTIONS_OFF"),
                        material.name);
                }
            }

            // MS1.obj carries a `g` record per object, so the importer hands
            // every object its own renderer: the hull stays opaque while the
            // two tracks clip their alpha.
            var hull = Material(renderers, "hull_vis");
            var track = Material(renderers, "trackL");
            Assert.IsFalse(hull.IsKeywordEnabled("_ALPHATEST_ON"), hull.name);
            Assert.AreEqual((int)RenderQueue.Geometry, hull.renderQueue);
            Assert.IsTrue(track.IsKeywordEnabled("_ALPHATEST_ON"), track.name);
            Assert.AreEqual((int)RenderQueue.AlphaTest, track.renderQueue);
            Assert.AreNotSame(
                Material(renderers, "trackL"),
                Material(renderers, "trackR"),
                "each track scrolls on its own material");
        }

        /// <summary>
        /// <c>rotate_turret</c> spins the turret object itself. The importer
        /// only splits an OBJ on its `g` records, so a file that carries `o`
        /// alone collapses into one merged mesh and the turret cannot move
        /// while the reticle still tracks the pivot.
        /// </summary>
        [Test]
        public void SpawnedMs1_HangsTheTurretAndGunMeshesOffTheirPivots()
        {
            _root = new GameObject("BattleTurretRig");
            var spawn = new GameObject("Spawn").transform;
            spawn.SetParent(_root.transform, false);

            var tank = BattleTank.Spawn(
                _root.transform,
                "RiggedMS1",
                LoadMs1(),
                spawn,
                new Bounds(Vector3.zero, new Vector3(1000f, 100f, 1000f)),
                0);

            Assert.IsNotNull(tank.Turret.TurretPivot);
            Assert.IsNotNull(tank.Turret.GunPivot);

            var turret = tank.Turret.TurretPivot
                .GetComponentsInChildren<MeshRenderer>(true);
            var gun = tank.Turret.GunPivot
                .GetComponentsInChildren<MeshRenderer>(true);
            Assert.Greater(turret.Length, 0,
                "the turret pivot drives no geometry: the OBJ import merged");
            Assert.Greater(gun.Length, 0,
                "the gun pivot drives no geometry: the OBJ import merged");

            // The gun rides the turret, so the hull renderer must not.
            var hull = Renderer(
                tank.GetComponentsInChildren<MeshRenderer>(true), "hull_vis");
            Assert.IsFalse(
                hull.transform.IsChildOf(tank.Turret.TurretPivot),
                "the hull must not traverse with the turret");

            // A quarter turn of the turret has to move the barrel with it.
            var before = tank.Turret.Muzzle.position;
            tank.Turret.TurretPivot.Rotate(Vector3.up, 90f, Space.Self);
            Assert.Greater(
                Vector3.Distance(before, tank.Turret.Muzzle.position),
                0.1f,
                "the muzzle does not follow the turret");
        }

        private static MeshRenderer Renderer(MeshRenderer[] renderers, string name)
        {
            for (var i = 0; i < renderers.Length; i++)
            {
                if (renderers[i].gameObject.name.StartsWith(name))
                {
                    return renderers[i];
                }
            }

            Assert.Fail("the MS-1 import has no " + name + " renderer");
            return null;
        }

        private static Material Material(MeshRenderer[] renderers, string name)
        {
            return Renderer(renderers, name).sharedMaterial;
        }

        [Test]
        public void BattleMasks_OnlyIncludeSourceTraceLayers()
        {
            Assert.AreNotEqual(0, BattleLayers.AimMask & (1 << BattleLayers.Static));
            Assert.AreNotEqual(0, BattleLayers.AimMask & (1 << BattleLayers.Armor));
            Assert.AreNotEqual(0, BattleLayers.AimMask & (1 << BattleLayers.Screen));
            Assert.AreEqual(0, BattleLayers.AimMask & (1 << BattleLayers.Physics));
            Assert.AreNotEqual(
                0, BattleLayers.ProjectileMask & (1 << BattleLayers.Destructibles));
            Assert.AreEqual(
                0, BattleLayers.ProjectileMask & (1 << BattleLayers.Physics));
        }

        /// <summary>
        /// <c>get_destroyed</c> calls <c>outline_switch(false)</c> before
        /// anything else. Without it a tank that died while the reticle was on
        /// it kept its team trace for the rest of the battle, because the aim
        /// trace drops a dead tank and so never comes back to clear it.
        /// </summary>
        [Test]
        public void DestroyedTank_DropsTheOutlineItWasTracedWith()
        {
            _root = new GameObject("BattleCombatOutline");
            var spawn = new GameObject("Spawn").transform;
            spawn.SetParent(_root.transform, false);

            var tank = BattleTank.Spawn(
                _root.transform,
                "TracedMS1",
                LoadMs1(),
                spawn,
                new Bounds(Vector3.zero, new Vector3(1000f, 100f, 1000f)),
                1);

            tank.OutlineSetup(true);
            tank.SwitchOutline(true);
            Assert.IsTrue(tank.OutlineShown, "the tank refused to be traced");
            Assert.Greater(OutlineAlpha(tank), 0f, "the trace never reached the shader");

            tank.Health.ReceiveDamageBasic(10000);
            Assert.IsTrue(tank.Dead, "the tank survived a lethal hit");

            Assert.IsFalse(tank.OutlineShown, "a wreck is still traced");
            Assert.AreEqual(
                0f, OutlineAlpha(tank), Tolerance, "the trace is still on screen");
        }

        /// <summary>
        /// `show_hide_tracks()`: a dead hull wears the client's `tracks_crash`
        /// mesh and the running pair goes away. Before the mesh was exported
        /// a wreck kept rolling its intact tracks.
        /// </summary>
        [Test]
        public void DestroyedTank_WearsTheClientBrokenTracks()
        {
            _root = new GameObject("BattleCombatBrokenTracks");
            var spawn = new GameObject("Spawn").transform;
            spawn.SetParent(_root.transform, false);

            var tank = BattleTank.Spawn(
                _root.transform,
                "WreckedMS1",
                LoadMs1(),
                spawn,
                new Bounds(Vector3.zero, new Vector3(1000f, 100f, 1000f)),
                1);

            var left = FindPart(tank, "trackL");
            var right = FindPart(tank, "trackR");
            var crashed = FindPart(tank, "tracks_crash");
            Assert.IsNotNull(crashed, "the client broken-track mesh was not ported");
            Assert.IsTrue(left.activeSelf, "a live tank runs on its tracks");
            Assert.IsTrue(right.activeSelf, "a live tank runs on its tracks");
            Assert.IsFalse(crashed.activeSelf, "a live tank shows broken tracks");
            Assert.AreSame(
                right.GetComponent<MeshRenderer>().sharedMaterial,
                crashed.GetComponent<MeshRenderer>().sharedMaterial,
                "tracks_crashed.material_override = trackR_mat");

            tank.Health.ReceiveDamageBasic(10000);
            Assert.IsTrue(tank.Dead, "the tank survived a lethal hit");

            Assert.IsTrue(crashed.activeSelf, "a wreck kept its running tracks");
            Assert.IsFalse(left.activeSelf, "a wreck kept its running tracks");
            Assert.IsFalse(right.activeSelf, "a wreck kept its running tracks");
        }

        /// <summary>The model object a client part name was imported as.</summary>
        private static GameObject FindPart(BattleTank tank, string part)
        {
            var renderers = tank.GetComponentsInChildren<MeshRenderer>(true);
            for (var i = 0; i < renderers.Length; i++)
            {
                if (TankModuleView.TrimCloneSuffix(renderers[i].gameObject.name) == part)
                {
                    return renderers[i].gameObject;
                }
            }

            return null;
        }

        /// <summary>The strongest alpha any outline pass on the tank carries.</summary>
        private static float OutlineAlpha(BattleTank tank)
        {
            var alpha = 0f;
            var renderers = tank.GetComponentsInChildren<MeshRenderer>(true);
            foreach (var renderer in renderers)
            {
                foreach (var material in renderer.sharedMaterials)
                {
                    if (material == null || material.shader == null) { continue; }
                    if (material.shader.name != TankOutlineShader.Name) { continue; }

                    alpha = Mathf.Max(
                        alpha, material.GetColor(TankOutlineShader.Color).a);
                }
            }

            return alpha;
        }

        private static TankInfo LoadMs1()
        {
            var database = TanksDatabase.Parse(
                BlitzResources.LoadText(BlitzResources.TanksData));
            var card = database.Get(BattleSession.DefaultTankId);
            Assert.IsNotNull(card, "MS-1 card is missing from tanks.json");
            return card;
        }
    }
}
