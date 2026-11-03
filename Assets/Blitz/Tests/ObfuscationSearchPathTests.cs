using System.IO;
using BlitzLegacy.EditorTools;
using NUnit.Framework;
using UnityEditor;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Obfuz resolves BCL references (netstandard) of the staged script
    /// assemblies from the CoreCLR runtime of the player being built. A path
    /// pointing at another platform's playback engine is missing in that
    /// platform's editor and fails the Windows IL2CPP build.
    /// </summary>
    public sealed class ObfuscationSearchPathTests
    {
        private const string Contents = "/unity/Editor/Data";

        [Test]
        public void WindowsPlayerUsesWindowsCoreClrRuntime()
        {
            var paths = ObfuscationSetup.FallbackSearchPaths(Contents, BuildTarget.StandaloneWindows64);
            Assert.AreEqual(Path.Combine(Contents, "PlaybackEngines", "windowsstandalonesupport",
                "Variations", "CoreCLRShared", "x64", "CoreCLR", "lib"), paths[0]);
        }

        [TestCase(BuildTarget.StandaloneLinux64)]
        [TestCase(BuildTarget.Android)]
        public void OtherPlayersKeepLinuxCoreClrRuntime(BuildTarget target)
        {
            var paths = ObfuscationSetup.FallbackSearchPaths(Contents, target);
            Assert.AreEqual(Path.Combine(Contents, "PlaybackEngines", "LinuxStandaloneSupport",
                "Variations", "CoreCLRShared", "x64", "CoreCLR", "lib"), paths[0]);
        }

        [Test]
        public void EditorFacadesStayAsSecondFallback()
        {
            var paths = ObfuscationSetup.FallbackSearchPaths(Contents, BuildTarget.StandaloneWindows64);
            Assert.AreEqual(2, paths.Length);
            Assert.AreEqual(Path.Combine(Contents, "NetStandard", "EditorExtensions"), paths[1]);
        }
    }
}
