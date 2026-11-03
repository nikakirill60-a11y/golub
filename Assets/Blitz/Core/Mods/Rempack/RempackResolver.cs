using System;
using System.Collections.Generic;

namespace BlitzLegacy.Core.Mods.Rempack
{
    /// <summary>What the resolver decided for every package of one load order.</summary>
    public sealed class RempackResolution
    {
        private readonly HashSet<RempackMod> _active = new HashSet<RempackMod>();
        private readonly Dictionary<RempackMod, RempackIssue> _blocked = new Dictionary<RempackMod, RempackIssue>();
        private readonly Dictionary<RempackMod, List<RempackIssue>> _notes = new Dictionary<RempackMod, List<RempackIssue>>();
        private readonly List<RempackMod> _order = new List<RempackMod>();
        private readonly Dictionary<RempackStockRef, RempackMod> _overrides = new Dictionary<RempackStockRef, RempackMod>();

        private static readonly IReadOnlyList<RempackIssue> NoNotes = new RempackIssue[0];

        /// <summary>The packages that load, in load order.</summary>
        public IReadOnlyList<RempackMod> Active
        {
            get { return _order; }
        }

        /// <summary>Stock content and the active package whose replacement is used (the last one in the order).</summary>
        public IReadOnlyDictionary<RempackStockRef, RempackMod> Overrides
        {
            get { return _overrides; }
        }

        public bool IsActive(RempackMod mod)
        {
            return mod != null && _active.Contains(mod);
        }

        /// <summary>The dependency or conflict that keeps a wanted package from loading, or null.</summary>
        public RempackIssue BlockedBy(RempackMod mod)
        {
            RempackIssue issue;
            return mod != null && _blocked.TryGetValue(mod, out issue) ? issue : null;
        }

        /// <summary>Warnings about an active package, such as replacements another package shadows.</summary>
        public IReadOnlyList<RempackIssue> Notes(RempackMod mod)
        {
            List<RempackIssue> notes;
            return mod != null && _notes.TryGetValue(mod, out notes) ? notes : NoNotes;
        }

        internal void Activate(RempackMod mod)
        {
            _active.Add(mod);
            _order.Add(mod);
        }

        internal void Block(RempackMod mod, RempackIssue issue)
        {
            _blocked[mod] = issue;
        }

        internal void Note(RempackMod mod, RempackIssue issue)
        {
            List<RempackIssue> notes;
            if (!_notes.TryGetValue(mod, out notes))
            {
                notes = new List<RempackIssue>();
                _notes.Add(mod, notes);
            }

            notes.Add(issue);
        }

        internal void Override(RempackStockRef reference, RempackMod mod)
        {
            _overrides[reference] = mod;
        }
    }

    /// <summary>
    /// Decides which packages load. Packages the player switched on are taken in load
    /// order; each one loads when its dependencies are installed in a matching version,
    /// switched on and loadable themselves (they are decided first, wherever they sit in
    /// the order), and no package already loading conflicts with it (either side may
    /// declare the conflict). Decisions are final, so the reason a package stays off
    /// still holds in the end. A stock replacement claimed by several active packages
    /// goes to the last one in the order.
    /// </summary>
    public static class RempackResolver
    {
        private sealed class Pass
        {
            public readonly Dictionary<string, RempackMod> Installed = new Dictionary<string, RempackMod>(StringComparer.Ordinal);
            public readonly Dictionary<RempackMod, RempackIssue> Decided = new Dictionary<RempackMod, RempackIssue>();
            public readonly HashSet<RempackMod> Visiting = new HashSet<RempackMod>();
            public readonly List<RempackMod> Kept = new List<RempackMod>();
        }

        /// <summary>
        /// <paramref name="mods"/> in load order. Only <see cref="RempackMod.Usable"/>
        /// packages that are <see cref="RempackMod.Enabled"/> are candidates; usable but
        /// switched-off packages still count as installed when a dependency names them.
        /// </summary>
        public static RempackResolution Resolve(IReadOnlyList<RempackMod> mods)
        {
            var pass = new Pass();
            var order = new Dictionary<RempackMod, int>();
            for (var i = 0; i < mods.Count; i++)
            {
                var mod = mods[i];
                order[mod] = i;
                if (mod.Usable && !pass.Installed.ContainsKey(mod.Id))
                {
                    pass.Installed.Add(mod.Id, mod);
                }
            }

            foreach (var mod in mods)
            {
                if (mod.Usable && mod.Enabled && pass.Installed[mod.Id] == mod)
                {
                    Decide(mod, pass);
                }
            }

            // A dependency cycle is assumed loadable while it is being decided; if a member
            // lost a conflict after all, whatever needs it cannot load either.
            var changed = true;
            while (changed)
            {
                changed = false;
                var ids = Ids(pass.Kept);
                for (var i = pass.Kept.Count - 1; i >= 0; i--)
                {
                    var mod = pass.Kept[i];
                    foreach (var dependency in mod.Manifest.Dependencies)
                    {
                        if (!ids.Contains(dependency.Id))
                        {
                            pass.Decided[mod] = Issue(RempackCodes.DependencyInactive, dependency.Id);
                            pass.Kept.RemoveAt(i);
                            changed = true;
                            break;
                        }
                    }
                }
            }

            pass.Kept.Sort((left, right) => order[left].CompareTo(order[right]));
            var resolution = new RempackResolution();
            foreach (var pair in pass.Decided)
            {
                if (pair.Value != null)
                {
                    resolution.Block(pair.Key, pair.Value);
                }
            }

            var owners = new Dictionary<RempackStockRef, RempackMod>();
            foreach (var mod in pass.Kept)
            {
                resolution.Activate(mod);
                foreach (var reference in mod.Manifest.Overrides)
                {
                    RempackMod earlier;
                    if (owners.TryGetValue(reference, out earlier))
                    {
                        resolution.Note(earlier, new RempackIssue(RempackSeverity.Warning, RempackCodes.OverrideShadowed, string.Empty,
                            reference.ToString(), mod.Id));
                    }

                    owners[reference] = mod;
                    resolution.Override(reference, mod);
                }
            }

            return resolution;
        }

        /// <summary>Null when <paramref name="mod"/> loads, otherwise why it does not.</summary>
        private static RempackIssue Decide(RempackMod mod, Pass pass)
        {
            RempackIssue issue;
            if (pass.Decided.TryGetValue(mod, out issue))
            {
                return issue;
            }

            if (!pass.Visiting.Add(mod))
            {
                return null;
            }

            issue = null;
            foreach (var dependency in mod.Manifest.Dependencies)
            {
                RempackMod found;
                if (!pass.Installed.TryGetValue(dependency.Id, out found))
                {
                    issue = Issue(RempackCodes.DependencyMissing, dependency.Id, dependency.Range.Text);
                }
                else if (!dependency.Range.Matches(found.Manifest.Version))
                {
                    issue = Issue(RempackCodes.DependencyVersion, dependency.Id, dependency.Range.Text, found.Manifest.Version.ToString());
                }
                else if (!found.Enabled || Decide(found, pass) != null)
                {
                    issue = Issue(RempackCodes.DependencyInactive, dependency.Id);
                }

                if (issue != null)
                {
                    break;
                }
            }

            if (issue == null)
            {
                foreach (var other in pass.Kept)
                {
                    if (Excludes(mod, other) || Excludes(other, mod))
                    {
                        issue = Issue(RempackCodes.ConflictPresent, other.Id);
                        break;
                    }
                }
            }

            pass.Visiting.Remove(mod);
            pass.Decided[mod] = issue;
            if (issue == null)
            {
                pass.Kept.Add(mod);
            }

            return issue;
        }

        private static RempackIssue Issue(string code, params string[] args)
        {
            return new RempackIssue(RempackSeverity.Error, code, string.Empty, args);
        }

        private static bool Excludes(RempackMod mod, RempackMod other)
        {
            foreach (var conflict in mod.Manifest.Conflicts)
            {
                if (string.Equals(conflict.Id, other.Id, StringComparison.Ordinal) && conflict.Range.Matches(other.Manifest.Version))
                {
                    return true;
                }
            }

            return false;
        }

        private static HashSet<string> Ids(List<RempackMod> mods)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var mod in mods)
            {
                ids.Add(mod.Id);
            }

            return ids;
        }
    }
}
