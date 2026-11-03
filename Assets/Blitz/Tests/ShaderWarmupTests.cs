using System.Collections;
using System.IO;
using BlitzLegacy.Runtime.Ui;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace BlitzLegacy.Tests
{
    public sealed class ShaderWarmupTests
    {
        [Test]
        public void StampMatchesOnlyTheSameBuildAndGpu()
        {
            var key = ShaderWarmup.BuildKey("1.2.1", "6000.0", "GPU A", "Vulkan", "1.3");
            StringAssert.StartsWith("v2|", key,
                "the safe targeted warmup must invalidate old global-warmup stamps");
            Assert.IsTrue(ShaderWarmup.IsCurrent(key + "\n", key));
            Assert.IsFalse(ShaderWarmup.IsCurrent(null, key));
            Assert.IsFalse(ShaderWarmup.IsCurrent(
                ShaderWarmup.BuildKey("1.2.2", "6000.0", "GPU A", "Vulkan", "1.3"), key));
            Assert.IsFalse(ShaderWarmup.IsCurrent(
                ShaderWarmup.BuildKey("1.2.1", "6000.0", "GPU B", "Vulkan", "1.3"), key));
        }

        [Test]
        public void ProgressBarRegionsComeFromTheLobbyAtlas()
        {
            var atlas = BattleFlowUi.LoadAtlas();
            var under = atlas.Get("loading_bar_under");
            var fill = atlas.Get("loading_bar_fill");
            Assert.IsNotNull(under);
            Assert.IsNotNull(fill);
            Assert.AreEqual(380f, fill.rect.width);
            Assert.AreEqual(16f, fill.rect.height);
        }

        [Test]
        public void EveryTargetedShaderCanBeLoaded()
        {
            for (var i = 0; i < ShaderWarmup.ShaderNames.Length; i++)
            {
                var name = ShaderWarmup.ShaderNames[i];
                Assert.IsNotNull(Shader.Find(name), name);
            }
        }

        [UnityTest]
        public IEnumerator TargetedWarmupCompletesWithoutEngineErrors()
        {
            var progress = 0f;
            var warmup = ShaderWarmup.Run(value => progress = value);
            while (warmup.MoveNext())
            {
                yield return warmup.Current;
            }

            Assert.AreEqual(1f, progress, 0.0001f);
        }

        [Test]
        public void RuntimeNeverUsesUnsafeGlobalShaderWarmup()
        {
            var runtime = Path.Combine(Application.dataPath, "Blitz", "Runtime");
            foreach (var path in Directory.GetFiles(runtime, "*.cs", SearchOption.AllDirectories))
            {
                StringAssert.DoesNotContain(
                    "Shader." + "WarmupAllShaders(",
                    File.ReadAllText(path),
                    path + " reintroduced Unity's incompatible URP keyword-space warmup");
            }
        }

        [Test]
        public void EditorRunsSkipTheWarmup()
        {
            Assert.IsFalse(ShaderWarmup.Needed);
        }
    }
}
