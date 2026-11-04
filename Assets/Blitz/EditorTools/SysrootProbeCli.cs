using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

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
                    Debug.Log("[BlitzSysroot]   Initialize=" + Invoke(instance, "Initialize"));
                    var sysrootPath = Convert.ToString(Invoke(instance, "GetSysrootPath"));
                    var toolchainPath = Convert.ToString(Invoke(instance, "GetToolchainPath"));
                    Debug.Log("[BlitzSysroot]   sysrootPath=" + (sysrootPath ?? "<null>")
                        + " toolchainPath=" + (toolchainPath ?? "<null>"));
                    Debug.Log("[BlitzSysroot]   compilerFlags=" + Convert.ToString(Invoke(instance, "GetIl2CppCompilerFlags"))
                        + " linkerFlags=" + Convert.ToString(Invoke(instance, "GetIl2CppLinkerFlags")));
                    if (!string.IsNullOrEmpty(sysrootPath)) sysroots++;
                    if (!string.IsNullOrEmpty(toolchainPath)) toolchains++;
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
    }
}
