using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace BlitzLegacy.Core.Mods
{
    /// <summary>
    /// Hands RAR and 7z archives to an unpacker installed on the machine.
    ///
    /// The Godot mods were packed with WinRAR's default settings, i.e. real
    /// compression, which the built-in <see cref="ModArchive"/> reader cannot
    /// decode (it only lifts stored entries). Every desktop the game targets
    /// usually has one of unrar / 7z / bsdtar / unar, so the installer tries
    /// them first and only falls back to the built-in reader when none is
    /// present or all of them failed.
    /// </summary>
    public static class ModUnpacker
    {
        private const int TimeoutMs = 180000;

        private static readonly string[] ExtraFolders =
        {
            "/usr/bin",
            "/usr/local/bin",
            "/opt/homebrew/bin",
            "/bin",
            @"C:\Program Files\7-Zip",
            @"C:\Program Files (x86)\7-Zip",
            @"C:\Program Files\WinRAR",
            @"C:\Program Files (x86)\WinRAR",
            @"C:\Windows\System32",
        };

        /// <summary>True when an external tool unpacked something into <paramref name="destination"/>.</summary>
        public static bool TryExtract(string archivePath, string destination)
        {
            if (string.IsNullOrEmpty(archivePath) || !File.Exists(archivePath)
                || string.IsNullOrEmpty(destination))
            {
                return false;
            }

            var archive = Path.GetFullPath(archivePath);
            var target = Path.GetFullPath(destination);
            var commands = new List<KeyValuePair<string, string>>
            {
                Command("unrar", "x -o+ -y -idq " + Quote(archive) + " " + Quote(target + Path.DirectorySeparatorChar)),
                Command("UnRAR", "x -o+ -y -idq " + Quote(archive) + " " + Quote(target + Path.DirectorySeparatorChar)),
                Command("7z", "x -y -bso0 -bsp0 " + Quote("-o" + target) + " " + Quote(archive)),
                Command("7zz", "x -y -bso0 -bsp0 " + Quote("-o" + target) + " " + Quote(archive)),
                Command("7za", "x -y -bso0 -bsp0 " + Quote("-o" + target) + " " + Quote(archive)),
                Command("unar", "-q -f -D -o " + Quote(target) + " " + Quote(archive)),
                Command("bsdtar", "-xf " + Quote(archive) + " -C " + Quote(target)),
                Command("tar", "-xf " + Quote(archive) + " -C " + Quote(target)),
            };

            for (var i = 0; i < commands.Count; i++)
            {
                var exe = Find(commands[i].Key);
                if (exe == null)
                {
                    continue;
                }

                Clean(target);
                Directory.CreateDirectory(target);
                if (Run(exe, commands[i].Value) && HasFiles(target))
                {
                    return true;
                }
            }

            Clean(target);
            return false;
        }

        /// <summary>Whether any supported external tool exists on this machine.</summary>
        public static bool Available
        {
            get
            {
                return Find("unrar") != null || Find("UnRAR") != null || Find("7z") != null
                    || Find("7zz") != null || Find("7za") != null || Find("unar") != null
                    || Find("bsdtar") != null;
            }
        }

        private static KeyValuePair<string, string> Command(string tool, string arguments)
        {
            return new KeyValuePair<string, string>(tool, arguments);
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static string Find(string tool)
        {
            var names = new List<string> { tool };
            if (Path.DirectorySeparatorChar == '\\')
            {
                names.Insert(0, tool + ".exe");
            }

            var folders = new List<string>();
            var path = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(path))
            {
                folders.AddRange(path.Split(Path.PathSeparator));
            }

            folders.AddRange(ExtraFolders);
            for (var f = 0; f < folders.Count; f++)
            {
                if (string.IsNullOrEmpty(folders[f]))
                {
                    continue;
                }

                for (var n = 0; n < names.Count; n++)
                {
                    try
                    {
                        var candidate = Path.Combine(folders[f].Trim(), names[n]);
                        if (File.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                    catch (ArgumentException)
                    {
                    }
                }
            }

            return null;
        }

        private static bool Run(string exe, string arguments)
        {
            try
            {
                var info = new ProcessStartInfo(exe, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                };

                using (var process = Process.Start(info))
                {
                    if (process == null)
                    {
                        return false;
                    }

                    process.StandardInput.Close();
                    process.OutputDataReceived += (s, e) => { };
                    process.ErrorDataReceived += (s, e) => { };
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    if (!process.WaitForExit(TimeoutMs))
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch (Exception)
                        {
                        }

                        return false;
                    }

                    return process.ExitCode == 0;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool HasFiles(string folder)
        {
            try
            {
                return Directory.Exists(folder)
                    && Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Length > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void Clean(string folder)
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, true);
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
