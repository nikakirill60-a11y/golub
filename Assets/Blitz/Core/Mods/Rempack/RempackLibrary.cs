using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace BlitzLegacy.Core.Mods.Rempack
{
    public enum RempackModStatus
    {
        /// <summary>Loads.</summary>
        Active,

        /// <summary>Switched off by the player, or by the game after a crash while it loaded.</summary>
        Disabled,

        /// <summary>Broken or unsafe; <see cref="RempackMod.Report"/> says why.</summary>
        Invalid,

        /// <summary>Made for a newer game: the game needs an update.</summary>
        Unsupported,

        /// <summary>Fine on its own, but a dependency, a conflict or another copy keeps it off.</summary>
        Blocked,
    }

    /// <summary>One package file in the mods folder, as the library last read it.</summary>
    public sealed class RempackMod
    {
        private static readonly IReadOnlyList<RempackIssue> NoNotes = new RempackIssue[0];

        internal RempackMod(string filePath, RempackManifest manifest, RempackReport report, long size, DateTime modified)
        {
            FilePath = filePath ?? string.Empty;
            Manifest = manifest;
            Report = report ?? new RempackReport();
            Size = size;
            Modified = modified;
            Id = manifest != null && RempackIds.IsModId(manifest.Id) ? manifest.Id : string.Empty;
            Notes = NoNotes;
        }

        /// <summary>The manifest's mod id; empty when the file has no readable, valid one.</summary>
        public string Id { get; private set; }

        public string FilePath { get; private set; }

        public string FileName
        {
            get { return Path.GetFileName(FilePath); }
        }

        /// <summary>Null when the file is not a readable package at all.</summary>
        public RempackManifest Manifest { get; private set; }

        /// <summary>What validation found (errors make the package <see cref="RempackModStatus.Invalid"/>).</summary>
        public RempackReport Report { get; private set; }

        public long Size { get; private set; }

        /// <summary>Last write time of the file, UTC.</summary>
        public DateTime Modified { get; private set; }

        public bool Enabled { get; internal set; }

        /// <summary>Switched off because the game closed while it loaded; shown until acknowledged.</summary>
        public bool Crashed { get; internal set; }

        public RempackModStatus Status { get; internal set; }

        /// <summary>The issue behind a status other than active or a plain switch-off.</summary>
        public RempackIssue Reason { get; internal set; }

        /// <summary>Warnings about how the package loads, such as replacements another package shadows.</summary>
        public IReadOnlyList<RempackIssue> Notes { get; internal set; }

        /// <summary>Valid, and the copy of its id the library uses: a candidate for loading.</summary>
        public bool Usable { get; internal set; }

        public bool IsActive
        {
            get { return Status == RempackModStatus.Active; }
        }

        public SemanticVersion Version
        {
            get { return Manifest == null ? null : Manifest.Version; }
        }

        public bool ReplacesStock
        {
            get { return Manifest != null && Manifest.ReplacesStock; }
        }

        /// <summary>Over a soft limit: the MODS screen marks it as heavy.</summary>
        public bool Heavy
        {
            get
            {
                foreach (var issue in Report.Issues)
                {
                    if (issue.Severity == RempackSeverity.Warning && RempackCodes.IsHeavy(issue.Code))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        public string DisplayName(string language)
        {
            return Manifest != null && Id.Length > 0 ? Manifest.DisplayName(language) : FileName;
        }

        public override string ToString()
        {
            return (Id.Length > 0 ? Id + " " + Version : FileName) + " (" + Status.ToString().ToLowerInvariant() + ")";
        }
    }

    public enum RempackInstallKind
    {
        New,
        Update,
        Reinstall,
        Downgrade,
    }

    /// <summary>
    /// A package copied into the game's folder and checked, waiting for the player to
    /// confirm. <see cref="RempackLibrary.Install"/> or <see cref="RempackLibrary.Discard"/> ends it.
    /// </summary>
    public sealed class RempackInstallPlan
    {
        internal RempackInstallPlan(string sourceName)
        {
            SourceName = sourceName ?? string.Empty;
            Report = new RempackReport();
        }

        /// <summary>The name of the file the player picked.</summary>
        public string SourceName { get; private set; }

        /// <summary>The checked copy, or null once it is installed, discarded or refused.</summary>
        public string StagedPath { get; internal set; }

        public long Size { get; internal set; }

        /// <summary>Null when the file is not a package at all.</summary>
        public RempackManifest Manifest { get; internal set; }

        /// <summary>Validation of the whole file, plus what installing it changes.</summary>
        public RempackReport Report { get; private set; }

        public RempackInstallKind Kind { get; internal set; }

        /// <summary>The installed package with the same id, if any.</summary>
        public RempackMod Existing { get; internal set; }

        internal string ManifestHash { get; set; }

        public bool CanInstall
        {
            get { return StagedPath != null && Manifest != null && RempackIds.IsModId(Manifest.Id) && !Report.HasErrors; }
        }
    }

    /// <summary>
    /// The installed Remaster Packages: <c>&lt;root&gt;/*.rempack</c>, kept packed. Reads and
    /// validates every file (hashing each one only until it is known to be intact), picks
    /// one copy per id, keeps the load order and the switches in <c>mods.json</c> and
    /// resolves dependencies, conflicts and stock replacements.
    ///
    /// Installing goes through a checked copy in <c>.incoming</c>: the file the player
    /// picked is copied once, validated in full, shown, and only then renamed to
    /// <c>&lt;id&gt;.rempack</c>, with the previous version kept aside until the rename is done.
    /// </summary>
    public sealed class RempackLibrary
    {
        public const string IncomingFolder = ".incoming";
        private const string BackupSuffix = ".old";

        private readonly object _gate = new object();
        private readonly HashSet<string> _pending = new HashSet<string>(StringComparer.Ordinal);
        private List<RempackMod> _mods = new List<RempackMod>();
        private RempackResolution _resolution = RempackResolver.Resolve(new RempackMod[0]);
        private ModState _state = new ModState();

        public RempackLibrary(string rootPath, RempackValidationOptions options = null)
        {
            RootPath = rootPath ?? string.Empty;
            Options = options ?? new RempackValidationOptions();
        }

        public string RootPath { get; private set; }

        public RempackValidationOptions Options { get; private set; }

        public string IncomingPath
        {
            get { return Path.Combine(RootPath, IncomingFolder); }
        }

        /// <summary>Every package file, in load order; files without a usable id come last.</summary>
        public IReadOnlyList<RempackMod> Mods
        {
            get { return _mods; }
        }

        public RempackResolution Resolution
        {
            get { return _resolution; }
        }

        /// <summary>The packages that load, in load order.</summary>
        public IReadOnlyList<RempackMod> Active
        {
            get { return _resolution.Active; }
        }

        /// <summary>Ids a crash switched off that the player has not been told about yet.</summary>
        public IReadOnlyList<string> Crashed
        {
            get { return _state.Crashed; }
        }

        /// <summary>Where a package with this id is installed.</summary>
        public string PathFor(string id)
        {
            return Path.Combine(RootPath, id + RempackFormat.Extension);
        }

        /// <summary>The copy of <paramref name="id"/> the library uses, else any file with that id.</summary>
        public RempackMod Find(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }

            RempackMod any = null;
            foreach (var mod in _mods)
            {
                if (string.Equals(mod.Id, id, StringComparison.Ordinal))
                {
                    if (mod.Usable)
                    {
                        return mod;
                    }

                    any = any ?? mod;
                }
            }

            return any;
        }

        /// <summary>Re-reads the folder. Hashes only files that changed since they last checked out.</summary>
        public void Refresh()
        {
            lock (_gate)
            {
                var mods = new List<RempackMod>();
                if (RootPath.Length == 0 || !Directory.Exists(RootPath))
                {
                    _state = new ModState();
                    _mods = mods;
                    Resolve();
                    return;
                }

                RecoverIncoming();
                var cache = RempackVerifiedCache.Load(RootPath);
                var names = new List<string>();
                foreach (var path in PackageFiles())
                {
                    var mod = Read(path, cache);
                    mods.Add(mod);
                    names.Add(mod.FileName);
                }

                cache.Save(names);
                PickCopies(mods);

                var unknown = new List<string>();
                ModState state;
                try
                {
                    state = ModState.Load(RootPath);
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                    // mods.json is locked right now: keep the switches read last time.
                    state = _state;
                }

                foreach (var mod in mods)
                {
                    if (mod.Usable && !state.IsKnown(mod.Id) && !unknown.Contains(mod.Id))
                    {
                        unknown.Add(mod.Id);
                    }
                }

                if (unknown.Count > 0)
                {
                    // New files join the end of the load order, switched on, in name order.
                    state = Update(delegate(ModState s)
                    {
                        foreach (var id in unknown)
                        {
                            s.Append(id);
                        }
                    }) ?? state;
                }

                _state = state;
                _mods = mods;
                Resolve();
            }
        }

        /// <summary>Copies a package file into the game's folder and checks it; nothing is installed yet.</summary>
        public RempackInstallPlan Prepare(string sourcePath)
        {
            var plan = new RempackInstallPlan(string.IsNullOrEmpty(sourcePath) ? string.Empty : Path.GetFileName(sourcePath));
            FileStream source;
            try
            {
                source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            catch (Exception error)
            {
                plan.Report.Add(new RempackIssue(RempackSeverity.Error, RempackCodes.FileUnreadable, plan.SourceName).WithDetail(error.Message));
                return plan;
            }

            using (source)
            {
                return Prepare(source, plan);
            }
        }

        /// <summary>The same from a stream, such as an Android <c>content://</c> document.</summary>
        public RempackInstallPlan Prepare(Stream source, string sourceName)
        {
            return Prepare(source, new RempackInstallPlan(sourceName));
        }

        /// <summary>
        /// Puts a checked package in place as <c>&lt;id&gt;.rempack</c>, replacing every other
        /// file with the same id. Returns the installed package, or null when the file could
        /// not be moved (the report of the plan says why; the previous version stays).
        /// </summary>
        public RempackMod Install(RempackInstallPlan plan)
        {
            if (plan == null || !plan.CanInstall)
            {
                throw new InvalidOperationException("The package cannot be installed.");
            }

            lock (_gate)
            {
                var id = plan.Manifest.Id;
                var target = PathFor(id);
                var backup = Path.Combine(IncomingPath, id + RempackFormat.Extension + BackupSuffix);
                try
                {
                    if (File.Exists(backup))
                    {
                        File.Delete(backup);
                    }

                    if (File.Exists(target))
                    {
                        File.Move(target, backup);
                    }

                    try
                    {
                        File.Move(plan.StagedPath, target);
                    }
                    catch
                    {
                        if (!File.Exists(target) && File.Exists(backup))
                        {
                            File.Move(backup, target);
                        }

                        throw;
                    }
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                    plan.Report.Add(new RempackIssue(RempackSeverity.Error, RempackCodes.InstallCopyFailed, string.Empty).WithDetail(error.Message));
                    Discard(plan);
                    return null;
                }

                _pending.Remove(plan.StagedPath);
                plan.StagedPath = null;
                TryDelete(backup);
                foreach (var mod in _mods)
                {
                    if (string.Equals(mod.Id, id, StringComparison.Ordinal) && !SamePath(mod.FilePath, target))
                    {
                        TryDelete(mod.FilePath);
                    }
                }

                if (!string.IsNullOrEmpty(plan.ManifestHash))
                {
                    // Hashed in full as the staged copy; the rename keeps size and time.
                    var cache = RempackVerifiedCache.Load(RootPath);
                    var info = new FileInfo(target);
                    cache.Remember(info.Name, info.Length, info.LastWriteTimeUtc.Ticks, plan.ManifestHash);
                    cache.Save(null);
                }

                Update(delegate(ModState s) { s.Append(id); });
                Refresh();
                return Find(id);
            }
        }

        /// <summary>Drops a plan the player did not confirm.</summary>
        public void Discard(RempackInstallPlan plan)
        {
            if (plan == null || plan.StagedPath == null)
            {
                return;
            }

            lock (_gate)
            {
                TryDelete(plan.StagedPath);
                _pending.Remove(plan.StagedPath);
                plan.StagedPath = null;
            }
        }

        /// <summary>Deletes the package and every other file with its id; false when a file stayed.</summary>
        public bool Remove(RempackMod mod)
        {
            if (mod == null)
            {
                return false;
            }

            lock (_gate)
            {
                var removed = true;
                foreach (var other in _mods)
                {
                    if (other == mod || (mod.Id.Length > 0 && string.Equals(other.Id, mod.Id, StringComparison.Ordinal)))
                    {
                        removed &= TryDelete(other.FilePath);
                    }
                }

                if (removed && mod.Id.Length > 0)
                {
                    Update(delegate(ModState s) { s.Forget(mod.Id); });
                }

                Refresh();
                return removed;
            }
        }

        public void SetEnabled(string id, bool enabled)
        {
            lock (_gate)
            {
                _state = Update(delegate(ModState s) { s.SetEnabled(id, enabled); }) ?? _state;
                Resolve();
            }
        }

        /// <summary>Loads <paramref name="id"/> right before <paramref name="beforeId"/> (null: last).</summary>
        public void MoveBefore(string id, string beforeId)
        {
            lock (_gate)
            {
                _state = Update(delegate(ModState s) { s.MoveBefore(id, beforeId); }) ?? _state;
                Resolve();
            }
        }

        /// <summary>The player has seen which packages a crash switched off.</summary>
        public void AcknowledgeCrashes()
        {
            lock (_gate)
            {
                _state = Update(delegate(ModState s) { s.ClearCrashed(); }) ?? _state;
                Resolve();
            }
        }

        // ------------------------------------------------------------------

        private RempackInstallPlan Prepare(Stream source, RempackInstallPlan plan)
        {
            string staged;
            try
            {
                Directory.CreateDirectory(IncomingPath);
                staged = Path.Combine(IncomingPath, Guid.NewGuid().ToString("N") + RempackFormat.Extension);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                plan.Report.Add(new RempackIssue(RempackSeverity.Error, RempackCodes.InstallCopyFailed, string.Empty).WithDetail(error.Message));
                return plan;
            }

            lock (_gate)
            {
                _pending.Add(staged);
            }

            plan.StagedPath = staged;
            if (Copy(source, staged, plan))
            {
                using (var package = RempackPackage.Open(staged, plan.Report))
                {
                    if (package != null)
                    {
                        plan.Manifest = package.Manifest;
                        try
                        {
                            plan.ManifestHash = package.Sha256(RempackFormat.ManifestFile);
                        }
                        catch (RempackException error)
                        {
                            plan.Report.Add(error.Issue);
                        }

                        RempackValidator.Validate(package, Options.Copy(true), plan.Report);
                    }
                }
            }

            lock (_gate)
            {
                Compare(plan);
                if (!plan.CanInstall)
                {
                    Discard(plan);
                }
            }

            return plan;
        }

        private static bool Copy(Stream source, string staged, RempackInstallPlan plan)
        {
            try
            {
                if (source.CanSeek && source.Length > RempackLimits.ArchiveBytes)
                {
                    plan.Size = source.Length;
                    plan.Report.Error(RempackCodes.ArchiveTooLarge, string.Empty, source.Length.ToString(CultureInfo.InvariantCulture));
                    return false;
                }

                long total = 0;
                using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        total += read;
                        if (total > RempackLimits.ArchiveBytes)
                        {
                            plan.Size = total;
                            plan.Report.Error(RempackCodes.ArchiveTooLarge, string.Empty, total.ToString(CultureInfo.InvariantCulture));
                            return false;
                        }

                        output.Write(buffer, 0, read);
                    }

                    output.Flush(true);
                }

                plan.Size = total;
                return true;
            }
            catch (Exception error)
            {
                // Whatever the source stream throws (a revoked Android document, a pulled
                // USB stick) means the same thing: the file could not be copied.
                plan.Report.Add(new RempackIssue(RempackSeverity.Error, RempackCodes.InstallCopyFailed, string.Empty).WithDetail(error.Message));
                return false;
            }
        }

        /// <summary>What installing the plan replaces, and what it would leave without a dependency.</summary>
        private void Compare(RempackInstallPlan plan)
        {
            var manifest = plan.Manifest;
            if (manifest == null || !RempackIds.IsModId(manifest.Id))
            {
                return;
            }

            var existing = Find(manifest.Id);
            plan.Existing = existing;
            plan.Kind = RempackInstallKind.New;
            if (existing != null && (existing.Version == null || manifest.Version == null))
            {
                // A broken copy without a readable version: the new file replaces it.
                plan.Kind = RempackInstallKind.Update;
            }
            else if (existing != null)
            {
                var installed = existing.Manifest.Version.ToString();
                var offered = manifest.Version.ToString();
                var order = manifest.Version.CompareTo(existing.Manifest.Version);
                if (order > 0)
                {
                    plan.Kind = RempackInstallKind.Update;
                    plan.Report.Info(RempackCodes.InstallUpdate, string.Empty, installed, offered);
                }
                else if (order == 0)
                {
                    plan.Kind = RempackInstallKind.Reinstall;
                    plan.Report.Info(RempackCodes.InstallReinstall, string.Empty, installed);
                }
                else
                {
                    plan.Kind = RempackInstallKind.Downgrade;
                    plan.Report.Warning(RempackCodes.InstallDowngrade, string.Empty, installed, offered);
                }
            }

            if (plan.Report.HasErrors)
            {
                return;
            }

            // Resolve the order as it would be with the new file in place of the old ones.
            var candidate = new RempackMod(plan.StagedPath, manifest, plan.Report, plan.Size, DateTime.UtcNow)
            {
                Usable = true,
                Enabled = existing == null || existing.Enabled,
            };
            var mods = new List<RempackMod>();
            var placed = false;
            foreach (var mod in _mods)
            {
                if (string.Equals(mod.Id, manifest.Id, StringComparison.Ordinal))
                {
                    // The installed file keeps its place in the load order.
                    if (!placed)
                    {
                        mods.Add(candidate);
                        placed = true;
                    }

                    continue;
                }

                mods.Add(mod);
            }

            if (!placed)
            {
                mods.Add(candidate);
            }

            var preview = RempackResolver.Resolve(mods);
            var reason = preview.BlockedBy(candidate);
            if (reason != null)
            {
                plan.Report.Add(new RempackIssue(RempackSeverity.Warning, reason.Code, string.Empty, reason.Args));
            }

            foreach (var mod in _mods)
            {
                if (mod.IsActive && !string.Equals(mod.Id, manifest.Id, StringComparison.Ordinal) && !preview.IsActive(mod))
                {
                    plan.Report.Warning(RempackCodes.InstallBreaks, string.Empty, mod.Id);
                }
            }
        }

        private RempackMod Read(string path, RempackVerifiedCache cache)
        {
            var report = new RempackReport();
            long size = 0;
            var modified = DateTime.MinValue;
            try
            {
                var info = new FileInfo(path);
                size = info.Length;
                modified = info.LastWriteTimeUtc;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                report.Add(new RempackIssue(RempackSeverity.Error, RempackCodes.FileUnreadable, Path.GetFileName(path)).WithDetail(error.Message));
            }

            RempackManifest manifest = null;
            if (!report.HasErrors)
            {
                using (var package = RempackPackage.Open(path, report))
                {
                    if (package != null)
                    {
                        manifest = package.Manifest;
                        string hash = null;
                        try
                        {
                            hash = package.Sha256(RempackFormat.ManifestFile);
                        }
                        catch (RempackException error)
                        {
                            report.Add(error.Issue);
                        }

                        var name = Path.GetFileName(path);
                        var verified = hash != null && cache.IsVerified(name, size, modified.Ticks, hash);
                        RempackValidator.Validate(package, Options.Copy(!verified), report);
                        if (!verified && hash != null && !report.HasErrors)
                        {
                            cache.Remember(name, size, modified.Ticks, hash);
                        }
                    }
                }
            }

            var mod = new RempackMod(path, manifest, report, size, modified);
            if (manifest == null || report.HasErrors)
            {
                var unsupported = Unsupported(report);
                mod.Status = unsupported != null ? RempackModStatus.Unsupported : RempackModStatus.Invalid;
                mod.Reason = unsupported ?? report.FirstError;
            }
            else
            {
                mod.Usable = true;
            }

            return mod;
        }

        /// <summary>The "update the game" kind of refusal, which the MODS screen words differently.</summary>
        private static RempackIssue Unsupported(RempackReport report)
        {
            foreach (var issue in report.Issues)
            {
                if (issue.Severity == RempackSeverity.Error && RempackCodes.NeedsNewerGame(issue.Code))
                {
                    return issue;
                }
            }

            return null;
        }

        /// <summary>
        /// One file per id is used: the highest version, then the one named
        /// <c>&lt;id&gt;.rempack</c>, then the first name. The others wait as duplicates.
        /// </summary>
        private void PickCopies(List<RempackMod> mods)
        {
            var chosen = new Dictionary<string, RempackMod>(StringComparer.Ordinal);
            foreach (var mod in mods)
            {
                if (!mod.Usable)
                {
                    continue;
                }

                RempackMod best;
                if (!chosen.TryGetValue(mod.Id, out best))
                {
                    chosen.Add(mod.Id, mod);
                    continue;
                }

                if (Prefer(mod, best))
                {
                    Shadow(best, mod);
                    chosen[mod.Id] = mod;
                }
                else
                {
                    Shadow(mod, best);
                }
            }
        }

        private bool Prefer(RempackMod candidate, RempackMod best)
        {
            var order = candidate.Manifest.Version.CompareTo(best.Manifest.Version);
            if (order != 0)
            {
                return order > 0;
            }

            var canonical = PathFor(candidate.Id);
            var candidateCanonical = SamePath(candidate.FilePath, canonical);
            if (candidateCanonical != SamePath(best.FilePath, canonical))
            {
                return candidateCanonical;
            }

            return string.CompareOrdinal(candidate.FileName, best.FileName) < 0;
        }

        private static void Shadow(RempackMod loser, RempackMod winner)
        {
            loser.Usable = false;
            loser.Status = RempackModStatus.Blocked;
            loser.Reason = new RempackIssue(RempackSeverity.Error, RempackCodes.Duplicate, string.Empty, winner.FileName);
        }

        /// <summary>Applies the state to every package, sorts them into load order and resolves.</summary>
        private void Resolve()
        {
            var crashed = new HashSet<string>(_state.Crashed, StringComparer.Ordinal);
            foreach (var mod in _mods)
            {
                mod.Enabled = mod.Id.Length > 0 && _state.IsEnabled(mod.Id);
                mod.Crashed = mod.Id.Length > 0 && crashed.Contains(mod.Id);
            }

            var sorted = new List<RempackMod>(_mods);
            sorted.Sort(delegate(RempackMod left, RempackMod right)
            {
                var l = left.Id.Length > 0 ? _state.IndexOf(left.Id) : -1;
                var r = right.Id.Length > 0 ? _state.IndexOf(right.Id) : -1;
                l = l < 0 ? int.MaxValue : l;
                r = r < 0 ? int.MaxValue : r;
                if (l != r)
                {
                    return l.CompareTo(r);
                }

                if (left.Usable != right.Usable)
                {
                    return left.Usable ? -1 : 1;
                }

                return string.CompareOrdinal(left.FileName, right.FileName);
            });

            var resolution = RempackResolver.Resolve(sorted);
            foreach (var mod in sorted)
            {
                if (!mod.Usable)
                {
                    continue;
                }

                mod.Notes = resolution.Notes(mod);
                if (!mod.Enabled)
                {
                    mod.Status = RempackModStatus.Disabled;
                    mod.Reason = mod.Crashed ? new RempackIssue(RempackSeverity.Warning, RempackCodes.SafeModeCrashed, string.Empty) : null;
                }
                else if (resolution.IsActive(mod))
                {
                    mod.Status = RempackModStatus.Active;
                    mod.Reason = null;
                }
                else
                {
                    mod.Status = RempackModStatus.Blocked;
                    mod.Reason = resolution.BlockedBy(mod);
                }
            }

            _mods = sorted;
            _resolution = resolution;
        }

        private ModState Update(Action<ModState> change)
        {
            try
            {
                return ModState.Update(RootPath, change);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                // The switches stay as they were; the next change tries again.
                return null;
            }
        }

        private IEnumerable<string> PackageFiles()
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(RootPath);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                files = new string[0];
            }

            Array.Sort(files, StringComparer.Ordinal);
            foreach (var file in files)
            {
                if (file.EndsWith(RempackFormat.Extension, StringComparison.OrdinalIgnoreCase)
                    && !Path.GetFileName(file).StartsWith(".", StringComparison.Ordinal))
                {
                    yield return file;
                }
            }
        }

        /// <summary>
        /// Puts back a previous version an install moved aside but never replaced (the game
        /// died between the two renames) and clears copies no plan is waiting for.
        /// </summary>
        private void RecoverIncoming()
        {
            string[] files;
            try
            {
                if (!Directory.Exists(IncomingPath))
                {
                    return;
                }

                files = Directory.GetFiles(IncomingPath);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                return;
            }

            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                if (name.EndsWith(RempackFormat.Extension + BackupSuffix, StringComparison.Ordinal))
                {
                    var target = Path.Combine(RootPath, name.Substring(0, name.Length - BackupSuffix.Length));
                    if (File.Exists(target))
                    {
                        TryDelete(file);
                    }
                    else
                    {
                        try
                        {
                            File.Move(file, target);
                        }
                        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                        {
                        }
                    }
                }
                else if (!_pending.Contains(file))
                {
                    TryDelete(file);
                }
            }
        }

        private static bool SamePath(string left, string right)
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
                Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        private static bool TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return true;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
