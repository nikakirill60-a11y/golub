using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace BlitzLegacy.EditorTools
{
    /// <summary>
    /// Fail-fast CI probe for the Linux IL2CPP sysroot/toolchain packages.
    ///
    /// A full Linux player build only discovers a missing sysroot in the Bee
    /// post-process, after it has spent a quarter of an hour writing the
    /// player data. This probe runs right after the import instead: it lists
    /// the UPM packages on disk, instantiates every SysrootPackage provider
    /// by reflection (no compile-time package reference needed), initializes
    /// it so its payload is extracted into the sysroot cache, and reports the
    /// sysroot/toolchain paths IL2CPP will be given. Exit code 2 means the
    /// real build would fail the same way.
    /// </summary>
    public static class SysrootProbeCli
    {
        public static void Run()
        {
            var ok = true;
            Debug.Log("[BlitzSysroot] unity=" + Application.unityVersion
                + " activeTarget=" + EditorUserBuildSettings.activeBuildTarget
                + " standaloneBackend=" + PlayerSettings.GetScriptingBackend(NamedBuildTarget.Standalone));

            ListMatchingPackages();
            ListSysrootCache();

            var providerTypes = FindProviderTypes();
            Debug.Log("[BlitzSysroot] provider types: " + providerTypes.Count);
            var sysroots = 0;
            var toolchains = 0;
            foreach (var type in providerTypes)
            {
                try
                {
                    var instance = Activator.CreateInstance(type);
                    Debug.Log("[BlitzSysroot] provider " + type.FullName
                        + " name=" + Prop(instance, "Name")
                        + " host=" + Prop(instance, "HostPlatform") + "/" + Prop(instance, "HostArch")
                        + " target=" + Prop(instance, "TargetPlatform") + "/" + Prop(instance, "TargetArch")
                        + " interfaces=" + string.Join(",", type.GetInterfaces().Select(i => i.FullName)));
                    // Unity 7000.0.0a8's SysrootPackage extracts its payload by
                    // running /bin/sh with broken argument quoting, which fails
                    // on the CI runner ("Unterminated quoted string"), so the
                    // packages can never install themselves headlessly. Extract
                    // the payload with a correctly quoted tar call first; the
                    // package's own Initialize() then finds it in place.
                    TryManualInstall(instance);
                    Debug.Log("[BlitzSysroot]   Initialize=" + InvokeSafe(instance, "Initialize"));
                    var sysrootPath = Convert.ToString(InvokeSafe(instance, "GetSysrootPath"));
                    var toolchainPath = Convert.ToString(InvokeSafe(instance, "GetToolchainPath"));
                    Debug.Log("[BlitzSysroot]   sysrootPath=" + (sysrootPath ?? "<null>")
                        + " toolchainPath=" + (toolchainPath ?? "<null>"));
                    Debug.Log("[BlitzSysroot]   compilerFlags=" + Convert.ToString(InvokeSafe(instance, "GetIl2CppCompilerFlags"))
                        + " linkerFlags=" + Convert.ToString(InvokeSafe(instance, "GetIl2CppLinkerFlags")));
                    if (IsInstalled(sysrootPath)) sysroots++;
                    if (IsInstalled(toolchainPath)) toolchains++;
                }
                catch (TargetInvocationException ex)
                {
                    ok = false;
                    Debug.LogError("[BlitzSysroot] provider " + type.FullName + " failed: " + ex.InnerException);
                }
                catch (Exception ex)
                {
                    ok = false;
                    Debug.LogError("[BlitzSysroot] provider " + type.FullName + " failed: " + ex);
                }
            }

            if (providerTypes.Count == 0) ok = false;
            if (sysroots == 0) ok = false;
            if (toolchains == 0) ok = false;
            Debug.Log("[BlitzSysroot] providers=" + providerTypes.Count
                + " sysroots=" + sysroots + " toolchains=" + toolchains + " ok=" + ok);
            EditorApplication.Exit(ok ? 0 : 2);
        }

        private static void ListMatchingPackages()
        {
            var packageCache = Path.Combine(Directory.GetCurrentDirectory(), "Library", "PackageCache");
            if (!Directory.Exists(packageCache))
            {
                Debug.LogError("[BlitzSysroot] no Library/PackageCache at " + packageCache);
                return;
            }

            foreach (var dir in Directory.GetDirectories(packageCache).OrderBy(d => d))
            {
                var name = Path.GetFileName(dir);
                if (name.IndexOf("sysroot", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("toolchain", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("sdk.linux", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Debug.Log("[BlitzSysroot] packageCache: " + name);
                }
            }
        }

        private static void ListSysrootCache()
        {
            var cache = Environment.GetEnvironmentVariable("UNITY_SYSROOT_CACHE");
            if (string.IsNullOrEmpty(cache))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                cache = Path.Combine(home, ".local", "share", "unity3d", "cache", "sysroots");
            }

            Debug.Log("[BlitzSysroot] sysroot cache root: " + cache
                + " exists=" + Directory.Exists(cache));
            if (!Directory.Exists(cache)) return;
            foreach (var entry in Directory.GetFileSystemEntries(cache, "*", SearchOption.AllDirectories)
                         .OrderBy(e => e).Take(80))
            {
                Debug.Log("[BlitzSysroot]   cache entry: " + entry.Substring(cache.Length));
            }
        }

        private static List<Type> FindProviderTypes()
        {
            // Package assemblies are compiled into Library/ScriptAssemblies;
            // make sure the sysroot/toolchain ones are loaded before scanning.
            var scriptAssemblies = Path.Combine(Directory.GetCurrentDirectory(), "Library", "ScriptAssemblies");
            if (Directory.Exists(scriptAssemblies))
            {
                foreach (var dll in Directory.GetFiles(scriptAssemblies, "*.dll"))
                {
                    var name = Path.GetFileName(dll);
                    if (name.IndexOf("Sysroot", StringComparison.OrdinalIgnoreCase) >= 0
                        || name.IndexOf("Toolchain", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        try { Assembly.LoadFrom(dll); }
                        catch (Exception ex) { Debug.LogWarning("[BlitzSysroot] load " + name + ": " + ex.Message); }
                    }
                }
            }

            var result = new List<Type>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (var type in SafeTypes(assembly))
                {
                    if (type == null || type.IsAbstract) continue;
                    for (var b = type.BaseType; b != null; b = b.BaseType)
                    {
                        if (b.FullName == "UnityEditor.Il2Cpp.SysrootPackage")
                        {
                            result.Add(type);
                            break;
                        }
                    }
                }
            }

            return result.OrderBy(t => t.FullName).ToList();
        }

        private static IEnumerable<Type> SafeTypes(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null); }
            catch { return Array.Empty<Type>(); }
        }

        private static string Prop(object instance, string name)
        {
            try
            {
                var value = instance.GetType().GetProperty(name)?.GetValue(instance);
                return value == null ? "<null>" : value.ToString();
            }
            catch (Exception ex)
            {
                return "<error: " + ex.Message + ">";
            }
        }

        private static object Invoke(object instance, string method)
        {
            var info = instance.GetType().GetMethod(method, Type.EmptyTypes);
            return info == null ? "<no method>" : info.Invoke(instance, null);
        }

        private static bool IsInstalled(string path)
        {
            // An empty directory is what a failed extraction leaves behind;
            // it must not count as an installed sysroot/toolchain.
            return !string.IsNullOrEmpty(path)
                && Directory.Exists(path)
                && Directory.GetFileSystemEntries(path).Length > 0;
        }

        private static object InvokeSafe(object instance, string method)
        {
            try
            {
                return Invoke(instance, method);
            }
            catch (TargetInvocationException ex)
            {
                Debug.LogWarning("[BlitzSysroot]   " + method + " threw: "
                    + (ex.InnerException == null ? ex.Message : ex.InnerException.GetType().Name + ": " + ex.InnerException.Message));
                return null;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[BlitzSysroot]   " + method + " threw: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Workaround for the alpha editor's broken payload extraction:
        /// unpack the provider's payload tarball into the directory the
        /// package expects, using Process.ArgumentList (no shell quoting),
        /// so Initialize() finds the payload already installed.
        /// </summary>
        private static void TryManualInstall(object instance)
        {
            var name = Prop(instance, "Name");
            var payloadRoot = Convert.ToString(InvokeSafe(instance, "PathToPayload"));
            Debug.Log("[BlitzSysroot]   payloadRoot=" + (payloadRoot ?? "<null>"));
            if (!string.IsNullOrEmpty(payloadRoot) && Directory.Exists(payloadRoot))
            {
                foreach (var file in Directory.GetFiles(payloadRoot, "*", SearchOption.AllDirectories)
                             .OrderBy(f => f).Take(40))
                {
                    Debug.Log("[BlitzSysroot]     payload file: "
                        + file.Substring(payloadRoot.Length) + " (" + new FileInfo(file).Length + " bytes)");
                }
            }

            string dest = null;
            try
            {
                var installDir = instance.GetType().GetMethod("PayloadInstallDirectory",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (installDir != null)
                {
                    dest = installDir.Invoke(instance, new object[] { name }) as string;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[BlitzSysroot]   PayloadInstallDirectory failed: " + ex.Message);
            }

            if (string.IsNullOrEmpty(dest))
            {
                dest = Convert.ToString(InvokeSafe(instance, "GetToolchainPath"))
                    ?? Convert.ToString(InvokeSafe(instance, "GetSysrootPath"));
            }

            Debug.Log("[BlitzSysroot]   install dest=" + (dest ?? "<null>")
                + " exists=" + (!string.IsNullOrEmpty(dest) && Directory.Exists(dest)));
            if (string.IsNullOrEmpty(dest)) return;
            if (Directory.Exists(dest) && Directory.GetFileSystemEntries(dest).Length > 0) return;

            var packageDir = FindPackageDir(name);
            var tar = FindPayloadTar(payloadRoot) ?? FindPayloadTar(packageDir);
            if (tar == null)
            {
                // The payload may ship as an already-extracted directory
                // (bin/, usr/, lib/...) instead of an archive; list what is
                // actually there and copy such a directory into place.
                ListAllFiles(payloadRoot, "payloadRoot");
                ListAllFiles(packageDir, "packageDir");
                var candidate = FindPayloadDirectory(payloadRoot) ?? FindPayloadDirectory(packageDir);
                if (candidate == null)
                {
                    Debug.LogWarning("[BlitzSysroot]   no payload archive or directory found for " + name);
                    return;
                }

                Debug.Log("[BlitzSysroot]   copying payload dir " + candidate + " -> " + dest);
                Directory.CreateDirectory(dest);
                CopyDirectory(candidate, dest);
                return;
            }

            Debug.Log("[BlitzSysroot]   extracting " + tar + " -> " + dest);
            Directory.CreateDirectory(dest);
            ExtractArchive(tar, dest);
        }

        private static void ListAllFiles(string root, string label)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                Debug.Log("[BlitzSysroot]   " + label + " missing: " + (root ?? "<null>"));
                return;
            }

            foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                         .OrderBy(f => f).Take(80))
            {
                Debug.Log("[BlitzSysroot]   " + label + " file: "
                    + file.Substring(root.Length) + " (" + new FileInfo(file).Length + " bytes)");
            }
        }

        private static string FindPayloadDirectory(string root)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return null;
            var candidates = new List<string> { root };
            candidates.AddRange(Directory.GetDirectories(root, "*", SearchOption.AllDirectories));
            return candidates
                .Where(d => Directory.Exists(Path.Combine(d, "bin"))
                    || Directory.Exists(Path.Combine(d, "usr", "include"))
                    || Directory.Exists(Path.Combine(d, "usr", "lib"))
                    || Directory.Exists(Path.Combine(d, "include")))
                .OrderBy(d => d.Length)
                .FirstOrDefault();
        }

        private static void CopyDirectory(string source, string dest)
        {
            var start = new ProcessStartInfo
            {
                FileName = "/bin/cp",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add("-a");
            start.ArgumentList.Add(source.TrimEnd('/') + "/.");
            start.ArgumentList.Add(dest);
            using (var process = Process.Start(start))
            {
                var stderr = process.StandardError.ReadToEnd();
                process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                Debug.Log("[BlitzSysroot]   copy exit=" + process.ExitCode
                    + (string.IsNullOrWhiteSpace(stderr) ? "" : " stderr: " + stderr.Trim()));
            }
        }

        private static string FindPackageDir(string packageName)
        {
            var packageCache = Path.Combine(Directory.GetCurrentDirectory(), "Library", "PackageCache");
            if (string.IsNullOrEmpty(packageName) || !Directory.Exists(packageCache)) return null;
            return Directory.GetDirectories(packageCache)
                .FirstOrDefault(d => Path.GetFileName(d).StartsWith(packageName + "@", StringComparison.Ordinal));
        }

        private static string FindPayloadTar(string root)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return null;
            string[] extensions = { ".tar.gz", ".tgz", ".tar.xz", ".tar.bz2", ".tar", ".zip", ".zst", ".7z" };
            var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
            var archive = files
                .Where(f => extensions.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(f => new FileInfo(f).Length)
                .FirstOrDefault();
            if (archive != null) return archive;
            // Payload archives in these packages do not always carry a
            // recognizable extension; fall back to the largest payload-sized
            // file under the payload root.
            return files
                .Select(f => new FileInfo(f))
                .Where(f => f.Length > 1024 * 1024)
                .OrderByDescending(f => f.Length)
                .Select(f => f.FullName)
                .FirstOrDefault();
        }

        private static void ExtractArchive(string archive, string dest)
        {
            // payload.tar.7z is a tar stream compressed with 7z: unpack the
            // outer layer first, then untar what came out of it.
            if (archive.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
            {
                RunTool("/usr/bin/7z", new[] { "x", "-y", archive, "-o" + dest }, "7z");
                var innerTar = Directory.GetFiles(dest, "*.tar", SearchOption.AllDirectories)
                    .OrderByDescending(f => new FileInfo(f).Length)
                    .FirstOrDefault();
                if (innerTar != null)
                {
                    RunTool("/bin/tar", new[] { "-xf", innerTar, "-C", dest }, "tar");
                    try { File.Delete(innerTar); } catch { /* keep the cache inspectable */ }
                }

                return;
            }

            var isZip = archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            if (isZip)
            {
                RunTool("/usr/bin/unzip", new[] { "-q", "-o", archive, "-d", dest }, "unzip");
            }
            else
            {
                RunTool("/bin/tar", new[] { "-xf", archive, "-C", dest }, "tar");
            }
        }

        private static void RunTool(string fileName, string[] args, string label)
        {
            var start = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using (var process = Process.Start(start))
            {
                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();
                Debug.Log("[BlitzSysroot]   " + label + " exit=" + process.ExitCode
                    + (string.IsNullOrWhiteSpace(stderr) ? "" : " stderr: " + stderr.Trim())
                    + (string.IsNullOrWhiteSpace(stdout) ? "" : " stdout: " + stdout.Trim()));
            }
        }
    }
}
