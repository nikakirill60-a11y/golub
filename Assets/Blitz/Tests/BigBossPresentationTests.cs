using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Localization;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Ui;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace BlitzLegacy.Tests
{
    public sealed class BigBossPresentationTests
    {
        private GameObject _root;
        private Camera _camera;
        [SetUp] public void Setup()
        {
            _root = new GameObject("BigBossPresentationTests");
            var cameraHost = new GameObject("Camera"); cameraHost.transform.SetParent(_root.transform);
            _camera = cameraHost.AddComponent<Camera>();
            _camera.transform.position = new Vector3(0, 5, -14); _camera.transform.LookAt(Vector3.up);
        }
        [TearDown] public void Cleanup() { Object.DestroyImmediate(_root); }
        private DavaFx Create(string source)
        {
            var fx = DavaFx.CreateBigBoss(_root.transform, "Boss_mode_" + source, 3);
            fx.ViewCamera = _camera; return fx;
        }
        private static object Field(object value, string name)
        { return value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(value); }

        [Test] public void ModeCardsShareOneFootprintAndBigBossTitleIsUppercaseInEveryLocale()
        {
            var menu = BattleModeScreen.Create(_root.transform, BattleMode.BigBoss, null);
            var content = (RectTransform)menu.transform.Find("mode_viewport/mode_cards");
            var boss = (RectTransform)content.Find("card_bossmode"); Assert.NotNull(boss);
            foreach (RectTransform card in content) Assert.AreEqual(new Vector2(210, 340), card.sizeDelta, card.name);
            Assert.AreEqual(content.childCount * 210 + (content.childCount - 1) * 14, content.sizeDelta.x);
            Assert.AreEqual("BIG BOSS", boss.Find("mode_name").GetComponent<Text>().text);
            var lang = LocalizationTable.Parse(BlitzResources.LoadText("lang"));
            foreach (var code in new[] { "en", "ru" })
            { lang.Language = code; Assert.AreEqual("BIG BOSS", lang.Get("battle_mode:bossmode/name")); }
        }

        [Test] public void SourceLongParticlesWithZeroOrientationProduceQuadsAndRespectPivot()
        {
            _camera.transform.position = new Vector3(0, 0, -100);
            var fx = Create("Kamikaze_Explosion");
            for (var i = 0; i < 8; i++) fx.Simulate(1f / 60);
            fx.Render(_camera); var inspected = 0;
            foreach (var group in (IEnumerable)Field(fx, "_groups"))
            {
                var layer = (JsonNode)Field(group, "Layer");
                if (!layer["isLong"].AsBool() || layer["layerType"].AsString() != "particles" || layer["particleOrientation"].AsInt() != 0) continue;
                var particles = (IList)Field(group, "Particles"); if (particles.Count == 0) continue;
                var batch = Field(group, "Batch"); var mesh = (Mesh)Field(batch, "Mesh");
                Assert.AreEqual(particles.Count * 4, mesh.vertexCount, layer["name"].AsString());
                var vertices = mesh.vertices; var center = (vertices[0] + vertices[1] + vertices[2] + vertices[3]) / 4;
                var right = (vertices[1] - vertices[0]) / 2; var up = (vertices[3] - vertices[0]) / 2;
                var position = fx.transform.InverseTransformPoint((Vector3)Field(particles[0], "Position"));
                // ParticleRenderObject scales each half extent by (1 +/- pivot); only a legacy pivot uses the full size.
                var legacy = layer["legacyPivot"].Kind == JsonKind.Array;
                var pivot = legacy ? layer["legacyPivot"] : layer["pivotPoint3d"]; var factor = legacy ? 2 : 1;
                var expected = position - right * (factor * pivot[0].AsFloat()) - up * (factor * pivot[1].AsFloat());
                Assert.Less(Vector3.Distance(expected, center), .001f); inspected++;
            }
            Assert.Greater(inspected, 0);
        }

        [Test] public void TiledSourceRibbonsDoNotUseClampedSpriteSampling()
        {
            _camera.transform.position = new Vector3(0, 0, -100);
            var fx = Create("Kamikaze_Explosion"); var inspected = 0;
            for (var i = 0; i < 8; i++) fx.Simulate(1f / 60);
            foreach (var renderer in fx.GetComponentsInChildren<MeshRenderer>())
                if (renderer.name == "Trail") { Assert.AreEqual(1, renderer.sharedMaterial.GetFloat("_RepeatSprite")); inspected++; }
            Assert.Greater(inspected, 0);
        }

        [Test] public void RepeatedEffectsShareImmutableMaterialsAndSurviveAnotherInstanceDisposal()
        {
            var first = Create("BossAura"); var second = Create("BossAura");
            var a = first.GetComponentsInChildren<MeshRenderer>(); var b = second.GetComponentsInChildren<MeshRenderer>();
            Assert.AreEqual(a.Length, b.Length); Assert.Greater(a.Length, 0);
            for (var i = 0; i < a.Length; i++) Assert.AreSame(a[i].sharedMaterial, b[i].sharedMaterial);
            Object.DestroyImmediate(first.gameObject);
            second.Simulate(.1f); second.Render(_camera);
            foreach (var renderer in b) Assert.IsNotNull(renderer.sharedMaterial);
        }

        [Test] public void LoadingPreloadKeepsSharedResourcesButNoLiveEffectObjects()
        {
            typeof(DavaFx).GetMethod("ClearBigBossMaterials",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null);
            var before=Create("BossAura");
            var material=before.GetComponentInChildren<MeshRenderer>().sharedMaterial;
            Object.DestroyImmediate(before.gameObject);
            var preload=DavaFx.PreloadBigBoss(_root.transform); var scenes=0;
            while(preload.MoveNext())scenes++;
            Assert.AreEqual(15,scenes);
            Assert.Zero(_root.GetComponentsInChildren<DavaFx>(true).Length);
            Assert.IsNull(_root.transform.Find("BigBossFxPreload"));
            var materials=(IDictionary)typeof(DavaFx).GetField("BigBossMaterials",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
            var nested=0;
            foreach(var emitter in JsonResources.Load("BigBoss/Sources/Boss_mode_Kamikaze_Explosion")["#dataNodes"].Items)
            {
                if(emitter["name"].AsString()!="Embers")continue;
                foreach(var layer in emitter["layers"][0]["innerEmitter"]["layers"].Items)
                    if(layer["layerType"].AsString()=="particlesStripe" && layer["spritePath"].AsString()!="")
                    { Assert.True(materials.Contains(layer),"Nested explosion ribbon was not preloaded; cache="+string.Join(",",System.Linq.Enumerable.Select(System.Linq.Enumerable.Cast<JsonNode>(materials.Keys),x=>x["name"].AsString()))); nested++; }
                break;
            }
            Assert.Greater(nested,0);
            var after=Create("BossAura");
            Assert.AreSame(material,after.GetComponentInChildren<MeshRenderer>().sharedMaterial);
        }

        [Test] public void AuthoredDistantLodDisablesEmissionWithoutReducingNearQuality()
        {
            _camera.transform.position = new Vector3(0, 0, -100);
            var near = Create("Kamikaze_Explosion");
            for (var i = 0; i < 60; i++) near.Simulate(1f / 60);
            var closeCount = near.LiveParticles; Object.DestroyImmediate(near.gameObject);
            _camera.transform.position = new Vector3(0, 0, -400);
            var far = Create("Kamikaze_Explosion");
            for (var i = 0; i < 60; i++) far.Simulate(1f / 60);
            Assert.Less(far.LiveParticles, closeCount);
        }

        [Test] public void NearCameraUsesTheDefaultSourceLodInsteadOfTheReducedEffectsLevel()
        {
            // activeLODS [false,true,true,false] layers (shock wave, debris) are absent only from
            // level 0, the client's reduced-effects option; a near camera must still emit them.
            var fx = Create("Kamikaze_Explosion"); var emitted = 0;
            for (var i = 0; i < 30; i++) fx.Simulate(1f / 60);
            foreach (var group in (IEnumerable)Field(fx, "_groups"))
            {
                var lods = ((JsonNode)Field(group, "Layer"))["activeLODS"];
                if (lods.Kind != JsonKind.Array || lods[0].AsBool() || !lods[1].AsBool()) continue;
                emitted += ((IList)Field(group, "Particles")).Count;
            }
            Assert.Greater(emitted, 0);
        }

        [TestCase("Minion_LvlUP")][TestCase("Graviton")]
        public void FuseBlendLayersCarryTheirAuthoredEdges(string source)
        {
            var fx = Create(source); fx.Simulate(1f / 60); var inspected = 0;
            foreach (var group in (IEnumerable)Field(fx, "_groups"))
            {
                var layer = (JsonNode)Field(group, "Layer"); var batch = Field(group, "Batch");
                if (layer["blending"].AsInt() != 8 || batch == null) continue;
                var material = (Material)Field(batch, "Material"); var edges = material.GetVector("_FuseEdges");
                Assert.AreEqual(1f, material.GetFloat("_Fuse"), layer["name"].AsString());
                Assert.AreEqual(layer["fuseBlendAddEdges"][0].AsFloat(.45f), edges.x, 1e-5f);
                Assert.AreEqual(layer["fuseBlendAddEdges"][1].AsFloat(.55f), edges.y, 1e-5f);
                inspected++;
            }
            Assert.Greater(inspected, 0);
        }

        [Test] public void AlphaErosionLayersEnableSourceErosion()
        {
            var fx = Create("HealerShield"); fx.Simulate(1f / 60); var eroded = 0;
            foreach (var group in (IEnumerable)Field(fx, "_groups"))
            {
                var layer = (JsonNode)Field(group, "Layer"); var batch = Field(group, "Batch"); if (batch == null) continue;
                var material = (Material)Field(batch, "Material"); var erosion = layer["alphaErosionOverLife"].Kind != JsonKind.Null;
                Assert.AreEqual(erosion ? 1f : 0f, material.GetFloat("_Erosion"), layer["name"].AsString());
                if (!erosion) continue;
                Assert.AreEqual(layer["alphaErosionAcceleration"].AsFloat(2), material.GetFloat("_ErosionAccel"), 1e-5f);
                eroded++;
            }
            Assert.Greater(eroded, 0);
        }

        [Test] public void EveryAbilityEffectPostsItsSourceWwiseCue()
        {
            foreach (var effect in new[] { "gravityBurst", "railgunShot", "gravityShot", "kamikazeAbility", "poisonedShell", "levelUp", "permanentDamageShield" })
            {
                var voices = new BigBossAudio.CueTimeline().Post(BigBossAudio.EventFor(effect), 0);
                Assert.IsNotNull(voices, effect); Assert.Greater(voices.Count, 0, effect);
                foreach (var voice in voices) Assert.IsNotNull(voice.Clip, effect);
            }
            foreach (var state in new[] { "shield_on", "shield_off", "heal_aura_on", "heal_aura_off", "killer_damage_on", "killer_damage_off" })
                Assert.True(BigBossAudio.CueTimeline.Exists(BigBossAudio.EventPrefix + state), state);
        }

        [Test] public void BomberBlastLayersTheExplosionWithOneDirtVariant()
        {
            var voices = new BigBossAudio.CueTimeline().Post(BigBossAudio.EventFor("kamikazeAbility"), 2);
            var names = new List<string>();
            foreach (var voice in voices) { Assert.AreEqual(2f, voice.At); names.Add(voice.Clip.name); }
            Assert.AreEqual(2, names.Count);
            CollectionAssert.Contains(names, "745509252");
            Assert.True(names.Contains("700864561") ^ names.Contains("1066676211"));
        }

        [Test] public void HealAuraRepeatsItsWaveAtTheTriggerRateUntilTheOffEventStopsIt()
        {
            var timeline = new BigBossAudio.CueTimeline(); var on = BigBossAudio.EventPrefix + "heal_aura_on";
            var voices = timeline.Post(on, 0);
            Assert.AreEqual(2, voices.Count);
            var intro = voices[0]; var wave = voices[1];
            Assert.AreEqual("726781756", intro.Clip.name); Assert.AreEqual("570352417", wave.Clip.name);
            Assert.AreEqual(intro.Clip.length - .5f, wave.At, 1e-4f); Assert.AreEqual(.5f, wave.FadeIn, 1e-4f);
            Assert.AreEqual(wave.At, intro.FadeOutAt, 1e-4f);
            timeline.Advance(wave.At + 2.2f + .01f);
            var waves = 0; foreach (var voice in timeline.Voices) if (voice.Clip.name == "570352417") waves++;
            Assert.AreEqual(2, waves);
            timeline.Post(BigBossAudio.EventPrefix + "heal_aura_off", 6); timeline.Advance(20);
            Assert.False(timeline.IsPlaying(on));
            Assert.True(timeline.IsPlaying(BigBossAudio.EventPrefix + "heal_aura_off"));
        }

        [Test] public void HealerShieldCrossfadesItsStingIntoAnEndlessLoop()
        {
            var voices = new BigBossAudio.CueTimeline().Post(BigBossAudio.EventPrefix + "shield_on", 0);
            Assert.AreEqual(2, voices.Count);
            var sting = voices[0]; var loop = voices[1];
            Assert.False(sting.Loop); Assert.True(loop.Loop); Assert.AreEqual("322190344", loop.Clip.name);
            Assert.AreEqual(sting.Clip.length * .5f, loop.At, 1e-4f); Assert.AreEqual(loop.At, sting.FadeOutAt, 1e-4f);
        }

        [Test] public void GivenShieldConfirmsWithTheStingAloneAndNeverStartsTheLoop()
        {
            var timeline = new BigBossAudio.CueTimeline();
            var voices = timeline.PostIntro(BigBossAudio.ShieldGivenEvent, 3);
            Assert.IsNotNull(voices); Assert.AreEqual(1, voices.Count);
            var sting = voices[0];
            Assert.False(sting.Loop); Assert.AreEqual(3f, sting.At);
            CollectionAssert.Contains(new[] { "927568578", "928409561" }, sting.Clip.name);
            timeline.Advance(60);
            foreach (var voice in timeline.Voices) Assert.AreNotEqual("322190344", voice.Clip.name);
            Assert.IsNull(timeline.PostIntro("unknown", 0));
        }

        [Test] public void RandomContainersAvoidRepeatingTheLastPick()
        {
            var timeline = new BigBossAudio.CueTimeline(); string previous = null;
            for (var i = 0; i < 6; i++)
            {
                var clip = timeline.Post(BigBossAudio.EventFor("gravityBurst"), i)[0].Clip.name;
                Assert.AreNotEqual(previous, clip); previous = clip;
            }
        }

        [TestCase("Kamikaze_Explosion")][TestCase("Repulsion_Explosion")][TestCase("HealerShield")]
        public void SourceGeometryIsFiniteAndIndependentOfMapCoordinates(string source)
        {
            var fx = Create(source); var other = Create(source); var offset = new Vector3(220, 40, -180);
            other.transform.position += offset;
            var otherCamera = new GameObject("TranslatedCamera"); otherCamera.transform.SetParent(_root.transform);
            otherCamera.transform.SetPositionAndRotation(_camera.transform.position + offset, _camera.transform.rotation);
            var camera = otherCamera.AddComponent<Camera>(); other.ViewCamera = camera;
            for (var tick = 0; tick < 90; tick++)
            {
                fx.Simulate(1f / 60); other.Simulate(1f / 60); fx.Render(_camera); other.Render(camera);
                var a = fx.GetComponentsInChildren<MeshFilter>(); var b = other.GetComponentsInChildren<MeshFilter>();
                for (var i = 0; i < a.Length; i++)
                {
                    Assert.AreEqual(a[i].sharedMesh.vertexCount, b[i].sharedMesh.vertexCount);
                    Assert.Less(Vector3.Distance(a[i].sharedMesh.bounds.size, b[i].sharedMesh.bounds.size), .02f);
                    foreach (var vertex in a[i].sharedMesh.vertices)
                        Assert.True(float.IsFinite(vertex.x) && float.IsFinite(vertex.y) && float.IsFinite(vertex.z));
                }
            }
            fx.Stop(); for (var i = 0; i < 1200 && fx.LiveParticles > 0; i++) fx.Simulate(1f / 60);
            Assert.Zero(fx.LiveParticles);
        }
    }
}
