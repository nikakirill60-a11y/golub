using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using BlitzLegacy.Core.Tanks;

namespace BlitzLegacy.Core.Mods
{
    /// <summary>One installed mod as the library remembers it.</summary>
    public sealed class InstalledMod
    {
        public string Id = string.Empty;
        public string DisplayName = string.Empty;
        public string FolderPath = string.Empty;
        public string SourceArchive = string.Empty;
        public bool Enabled = true;
        public int PackageVersion;
        public bool Portable;
        public readonly List<ModVehicleAssets> Tanks = new List<ModVehicleAssets>();

        public int TankCount
        {
            get { return Tanks.Count; }
        }
    }

    /// <summary>The outcome of one install attempt, ready to be shown as-is.</summary>
    public sealed class ModInstallResult
    {
        public bool Ok;
        public string ErrorKey = string.Empty;
        public InstalledMod Mod;
    }

    /// <summary>
    /// The installed legacy mods (Godot folders and <c>.blitzmod</c> packages).
    ///
    /// A mod is unpacked once into &lt;root&gt;/&lt;id&gt; and never touched
    /// again: the game reads the cards straight out of that folder on every
    /// boot, so a player can also drop an already unpacked mod there by hand,
    /// the way the Godot build let them. Which ones are switched on lives in
    /// <c>mods.json</c> (<see cref="ModState"/>), shared with the Remaster
    /// Packages; it took over from <c>mods.txt</c>, which it imports once.
    /// </summary>
    public sealed class ModLibrary
    {
        private readonly List<InstalledMod> _mods = new List<InstalledMod>();

        public ModLibrary(string rootPath)
        {
            RootPath = rootPath;
        }

        public string RootPath { get; private set; }

        public IReadOnlyList<InstalledMod> Mods
        {
            get { return _mods; }
        }

        /// <summary>Re-reads the folder. Cheap enough to call when the screen opens.</summary>
        public void Refresh()
        {
            _mods.Clear();
            if (string.IsNullOrEmpty(RootPath) || !Directory.Exists(RootPath))
            {
                return;
            }

            // Recover a power loss between moving the previous install and promoting staging.
            foreach (var backup in Directory.GetDirectories(RootPath, ".backup_pw_*"))
            {
                var target = Path.Combine(RootPath, new DirectoryInfo(backup).Name.Substring(".backup_".Length));
                if (!Directory.Exists(target)) { Directory.Move(backup, target); }
            }
            var state = ModState.Load(RootPath);
            var folders = Directory.GetDirectories(RootPath);
            Array.Sort(folders, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < folders.Length; i++)
            {
                if (new DirectoryInfo(folders[i]).Name.StartsWith(".", StringComparison.Ordinal)) { continue; }
                var unwrapped = GodotModPackage.Unwrap(folders[i]);
                if (PortableModPackage.Present(unwrapped))
                {
                    try
                    {
                        var portable = PortableModPackage.Read(unwrapped);
                        var installed = new InstalledMod { Id = portable.Id, DisplayName = portable.DisplayName,
                            FolderPath = folders[i], Enabled = state.IsEnabled(portable.Id),
                            PackageVersion = portable.Version, Portable = true };
                        installed.Tanks.AddRange(portable.Tanks); _mods.Add(installed);
                    }
                    catch (Exception) { /* Invalid packages never register partially. */ }
                    continue;
                }
                var package = GodotModPackage.Scan(unwrapped);
                if (package.IsEmpty)
                {
                    continue;
                }

                var id = GodotModPackage.Slug(new DirectoryInfo(folders[i]).Name);
                var mod = new InstalledMod
                {
                    Id = id,
                    DisplayName = new DirectoryInfo(folders[i]).Name,
                    FolderPath = folders[i],
                    Enabled = state.IsEnabled(id),
                };
                mod.Tanks.AddRange(package.Tanks);
                _mods.Add(mod);
            }
        }

        public InstalledMod Find(string id)
        {
            for (var i = 0; i < _mods.Count; i++)
            {
                if (string.Equals(_mods[i].Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return _mods[i];
                }
            }

            return null;
        }

        /// <summary>
        /// Unpacks an archive into the library. The work happens in a sibling
        /// staging folder so a half-written mod never shows up in the list.
        /// </summary>
        public ModInstallResult Install(string archivePath)
        {
            var result = new ModInstallResult();
            if (string.IsNullOrEmpty(archivePath) || !File.Exists(archivePath))
            {
                result.ErrorKey = "mods_error_missing_file";
                return result;
            }

            if (string.Equals(Path.GetExtension(archivePath), ".modpack", StringComparison.OrdinalIgnoreCase))
            { result.ErrorKey = "mods_error_conversion_required"; return result; }
            if (string.Equals(Path.GetExtension(archivePath), ".blitzmod", StringComparison.OrdinalIgnoreCase))
            { return InstallPortable(archivePath); }
            // Content-based dispatch prevents renamed portable/source ZIPs from
            // bypassing the bounded portable parser through the legacy unpacker.
            try
            {
                if (ModArchive.DetectFile(archivePath) == ModArchiveFormat.Zip)
                {
                    var portable = false; var source = false;
                    using (var input = File.OpenRead(archivePath))
                    using (var zip = new ZipArchive(input, ZipArchiveMode.Read))
                    {
                        portable = zip.GetEntry("manifest.json") != null;
                        source = zip.GetEntry("package.json") != null;
                    }
                    if (portable) { return InstallPortable(archivePath); }
                    if (source) { result.ErrorKey = "mods_error_conversion_required"; return result; }
                }
            }
            catch (Exception) { result.ErrorKey = "mods_error_unpack_failed"; return result; }
            Directory.CreateDirectory(RootPath);
            var name = Path.GetFileNameWithoutExtension(archivePath);
            if (name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
            {
                name = name.Substring(0, name.Length - 4);
            }

            var id = GodotModPackage.Slug(name);
            var staging = Path.Combine(RootPath, ".staging_" + id);
            Delete(staging);
            try
            {
                // WinRAR / 7-Zip compress by default, which the built-in reader
                // cannot decode, so a system unpacker gets the first go.
                var format = ModArchive.DetectFile(archivePath);
                var external = (format == ModArchiveFormat.Rar4
                        || format == ModArchiveFormat.Rar5
                        || format == ModArchiveFormat.SevenZip)
                    && ModUnpacker.TryExtract(archivePath, staging);
                if (!external)
                {
                    Delete(staging);
                    ModArchive.Extract(archivePath, staging);
                }
            }
            catch (ModArchiveException error)
            {
                Delete(staging);
                result.ErrorKey = error.Reason;
                return result;
            }
            catch (Exception)
            {
                Delete(staging);
                result.ErrorKey = "mods_error_unpack_failed";
                return result;
            }

            var extracted = GodotModPackage.Unwrap(staging);
            if (PortableModPackage.Present(extracted)) { Delete(staging); return InstallPortable(archivePath); }
            if (File.Exists(Path.Combine(extracted, "package.json")))
            { Delete(staging); result.ErrorKey = "mods_error_conversion_required"; return result; }
            var package = GodotModPackage.Scan(extracted);
            if (package.IsEmpty)
            {
                Delete(staging);
                result.ErrorKey = "mods_error_no_cards";
                return result;
            }

            var target = Path.Combine(RootPath, id);
            Delete(target);
            Directory.Move(staging, target);
            Refresh();
            var mod = Find(id);
            if (mod == null)
            {
                result.ErrorKey = "mods_error_no_cards";
                return result;
            }

            result.Ok = true;
            result.Mod = mod;
            return result;
        }

        private ModInstallResult InstallPortable(string archivePath)
        {
            Directory.CreateDirectory(RootPath);
            var staging = Path.Combine(RootPath, ".staging_" + Guid.NewGuid().ToString("N"));
            string target = null, backup = null;
            var moved = false;
            try
            {
                PortableModPackage.Extract(archivePath, staging);
                var package = PortableModPackage.Read(staging);
                Refresh();
                var old = Find(package.Id);
                if (old != null && old.PackageVersion > package.Version)
                { throw new ModArchiveException("mods_error_portable_downgrade"); }
                target = Path.Combine(RootPath, package.Id);
                backup = Path.Combine(RootPath, ".backup_" + package.Id);
                // Preserve the old installation until the fully validated staging tree is ready.
                if (Directory.Exists(backup))
                {
                    if (!Directory.Exists(target)) { Directory.Move(backup, target); }
                    else { PortableModPackage.Read(target); Directory.Delete(backup, true); }
                }
                if (Directory.Exists(target)) { Directory.Move(target, backup); }
                Directory.Move(staging, target); moved = true;
                Refresh(); var mod = Find(package.Id);
                if (mod == null) { throw new ModArchiveException("mods_error_portable_invalid"); }
                Delete(backup);
                return new ModInstallResult { Ok = true, Mod = mod };
            }
            catch (Exception error)
            {
                if (backup != null && Directory.Exists(backup))
                {
                    if (moved && target != null) { Delete(target); }
                    if (!Directory.Exists(target)) { Directory.Move(backup, target); }
                }
                var archiveError = error as ModArchiveException;
                return new ModInstallResult { ErrorKey = archiveError != null ? archiveError.Reason : "mods_error_portable_invalid" };
            }
            finally { Delete(staging); }
        }

        public void Remove(string id)
        {
            var mod = Find(id);
            if (mod == null)
            {
                return;
            }

            Delete(mod.FolderPath);
            ModState.Update(RootPath, delegate(ModState state) { state.Forget(mod.Id); });
            Refresh();
        }

        public void SetEnabled(string id, bool enabled)
        {
            var mod = Find(id);
            if (mod == null)
            {
                return;
            }

            mod.Enabled = enabled;
            ModState.Update(RootPath, delegate(ModState state) { state.SetEnabled(mod.Id, enabled); });
        }

        /// <summary>
        /// The cards of every enabled mod, ready for the tanks database. A mod
        /// never replaces a stock card: the database keeps the first name it
        /// sees, and a shadowed mod card would otherwise silently rewrite a
        /// vehicle the player already owns.
        /// </summary>
        public List<TankInfo> Cards(TanksDatabase stock)
        {
            var cards = new List<TankInfo>();
            for (var i = 0; i < _mods.Count; i++)
            {
                var mod = _mods[i];
                if (!mod.Enabled)
                {
                    continue;
                }

                for (var t = 0; t < mod.Tanks.Count; t++)
                {
                    var card = mod.Tanks[t].PortableCard ?? (mod.Tanks[t].HasCard
                        ? GodotModCardReader.Read(mod.Tanks[t].CardPath)
                        : null);
                    if (card == null || string.IsNullOrEmpty(card.Name))
                    {
                        card = Placeholder(mod.Tanks[t]);
                    }

                    MarkAsMod(card);

                    if (stock != null && stock.Has(card.Name))
                    {
                        continue;
                    }

                    cards.Add(card);
                }
            }

            return cards;
        }

        /// <summary>
        /// Every mod card lands in the mod class: light green like a
        /// collector, outside the research tree and never rolled for bots
        /// (there is no stock model to give them). The nation is the one
        /// the card declares, so a mod can fly any stock flag; only a card
        /// that names no nation (or a value outside the enum) falls back
        /// to the mixed <see cref="Nation.Other"/> flag.
        /// </summary>
        public static void MarkAsMod(TankInfo card)
        {
            if (card == null)
            {
                return;
            }

            card.Mod = true;
            card.Collector = false;
            if (!IsDeclaredNation(card.Nation))
            {
                card.Nation = Nation.Other;
            }

            card.TreeTank = false;
            card.IgnoreMatchmaker = true;
        }

        /// <summary>True for a nation a card may name: stock trees and the mixed flag.</summary>
        private static bool IsDeclaredNation(Nation nation)
        {
            return nation >= Nation.Ussr && nation <= Nation.European;
        }

        /// <summary>The stand-in card of a mod that shipped a model without a .tres.</summary>
        private static TankInfo Placeholder(ModVehicleAssets tank)
        {
            var id = string.IsNullOrEmpty(tank.Id) ? "mod" : tank.Id;
            return new TankInfo
            {
                Name = id.ToLowerInvariant(),
                DisplayName = id,
                DisplayNameShort = id,
                Level = 1,
                Health = 1,
            };
        }

        /// <summary>Adds every enabled mod card to <paramref name="database"/>.</summary>
        public int Apply(TanksDatabase database)
        {
            if (database == null)
            {
                return 0;
            }

            var cards = Cards(database);
            for (var i = 0; i < cards.Count; i++)
            {
                database.Add(cards[i]);
            }

            return cards.Count;
        }

        private static void Delete(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
