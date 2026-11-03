using System.IO;
using Obfuz.EncryptionVM;
using Obfuz.ObfusPasses;
using Obfuz.Settings;
using Obfuz.Unity;
using UnityEditor;
using UnityEngine;

namespace BlitzLegacy.EditorTools
{
    /// <summary>
    /// Anti-dump symbol obfuscation (Obfuz, vendored in Packages/ with the
    /// BLITZ PATCH changes: CJK names + PDB passthrough for Sentry line maps).
    ///
    /// Only the SymbolObfus pass runs: it rewrites names in global-metadata.dat
    /// to Han glyphs, costs nothing at runtime, and leaves IL bodies alone so
    /// IL2CPP source mapping still resolves Sentry crashes to file:line.
    ///
    /// Core, Runtime and Network are renamed. Fusion binds the networked
    /// state of NetBattle and the weaved INetworkStruct/INetworkInput layouts
    /// by member name (GetArrayReader(nameof(...)), [Networked] backing
    /// fields), so the rule file keeps exactly those; session, seat-token and
    /// input-guard logic is renamed. Settings are forced on every build so the
    /// ProjectSettings/Obfuz.asset in git can never drift into a broken state.
    /// </summary>
    public static class ObfuscationSetup
    {
        public const string RuleFile = "Assets/Obfuz/blitz-symbol-rules.xml";
        public const string MappingFile = "Assets/Obfuz/SymbolObfus/symbol-mapping.xml";

        /// <summary>Every shipped assembly of the project's own code.</summary>
        public static readonly string[] Assemblies = { "BlitzLegacy.Core", "BlitzLegacy.Runtime", "BlitzLegacy.Network", "BlitzLegacy.Direct" };

        /// <summary>Applies the project policy for the active build target.</summary>
        public static void Apply(bool enable)
        {
            Apply(enable, EditorUserBuildSettings.activeBuildTarget);
        }

        /// <summary>Applies the project policy; enable toggles the build hook.</summary>
        public static void Apply(bool enable, BuildTarget target)
        {
            var s = ObfuzSettings.Instance;
            // A settings object created headless (no ProjectSettings/Obfuz.asset
            // yet) skips serializer defaults, so fill every section first.
            foreach (var field in typeof(ObfuzSettings).GetFields(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
            {
                if (field.FieldType.IsClass && field.FieldType != typeof(string)
                    && field.GetValue(s) == null)
                {
                    field.SetValue(s, System.Activator.CreateInstance(field.FieldType));
                }
            }
            s.buildPipelineSettings.enable = enable;

            s.assemblySettings.assembliesToObfuscate = Assemblies;
            s.assemblySettings.nonObfuscatedButReferencingObfuscatedAssemblies = new string[0];
            // Unity 7 CoreCLR desktop players stage only the script assemblies, so
            // BCL references such as netstandard must resolve from the CoreCLR player
            // runtime; the staging directory still wins when it has them.
            // Only set during a build so the committed settings stay portable.
            s.assemblySettings.additionalAssemblySearchPaths = enable
                ? FallbackSearchPaths(EditorApplication.applicationContentsPath, target)
                : new string[0];
            s.assemblySettings.obfuscateObfuzRuntime = false;

            s.obfuscationPassSettings.enabledPasses = ObfuscationPassType.SymbolObfus;

            var sym = s.symbolObfusSettings;
            sym.debug = false;
            sym.useCjkCharSet = true;
            sym.obfuscatedNamePrefix = "";
            sym.useConsistentNamespaceObfuscation = true;
            sym.detectReflectionCompatibility = true;
            sym.keepUnknownSymbolInSymbolMappingFile = true;
            sym.symbolMappingFile = MappingFile;
            sym.ruleFiles = new[] { RuleFile };

            Directory.CreateDirectory(Path.GetDirectoryName(MappingFile));
            ObfuzSettings.Save();
            EnsureGeneratedVm();
        }

        /// <summary>Player BCL folders used only when the staging area lacks a reference.</summary>
        public static string[] FallbackSearchPaths(string contentsPath, BuildTarget target)
        {
            var coreClr = Path.Combine(contentsPath, "PlaybackEngines",
                PlayerSupportFolder(target), "Variations", "CoreCLRShared", "x64", "CoreCLR", "lib");
            // A few runtime assemblies keep dangling references to desktop-only
            // facades (System.Security.Permissions); the editor ships those.
            var facades = Path.Combine(contentsPath, "NetStandard", "EditorExtensions");
            return new[] { coreClr, facades };
        }

        /// <summary>
        /// The playback-engine folder whose CoreCLR runtime matches the player
        /// being built. A Windows player must resolve netstandard from the
        /// Windows runtime: the Linux folder does not exist in a Windows editor,
        /// so Obfuz would fail with "Assembly netstandard not found".
        /// Other targets keep the Linux runtime they were verified with.
        /// </summary>
        public static string PlayerSupportFolder(BuildTarget target)
        {
            return target == BuildTarget.StandaloneWindows64
                ? "windowsstandalonesupport"
                : "LinuxStandaloneSupport";
        }

        /// <summary>Obfuz validates its encryption VM even for symbol-only runs.</summary>
        private static void EnsureGeneratedVm()
        {
            var vm = ObfuzSettings.Instance.encryptionVMSettings;
            if (!File.Exists(vm.codeOutputPath))
            {
                new VirtualMachineCodeGenerator(vm.codeGenerationSecretKey, vm.encryptionOpCodeCount)
                    .Generate(vm.codeOutputPath);
                AssetDatabase.Refresh();
                Debug.Log("[Blitz] obfuscation: generated encryption VM at " + vm.codeOutputPath);
            }
        }

        /// <summary>CLI: -executeMethod BlitzLegacy.EditorTools.ObfuscationSetup.Configure</summary>
        public static void Configure()
        {
            Apply(false);
            Debug.Log("[Blitz] obfuscation: settings applied");
            EditorApplication.Exit(0);
        }
    }
}
