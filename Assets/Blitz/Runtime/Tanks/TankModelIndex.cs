using System;
using System.Collections.Generic;
using BlitzLegacy.Runtime.Assets;

namespace BlitzLegacy.Runtime.Tanks
{
    /// <summary>
    /// The vehicle models the build ships under Resources/Models/Tanks, by
    /// resource id (MS1, T62A, ...).
    ///
    /// Resources cannot tell whether an asset exists short of loading it, and
    /// a tank model loads its meshes, kept readable for the armour cut. Asked
    /// that way, a garage of every vehicle pulled the whole roster into
    /// memory just to learn which cards to show. The editor keeps this list
    /// in step with the folder (TankModelIndexWriter).
    /// </summary>
    public static class TankModelIndex
    {
        /// <summary>The list under Resources/Data: one id per line.</summary>
        public const string DataName = "tank_models";

        private static HashSet<string> _ids;
        private static bool _read;

        /// <summary>
        /// Sets <paramref name="shipped"/> to whether the build has a model
        /// for <paramref name="id"/>. Returns false when there is no list to
        /// ask; the caller has to load the model then.
        /// </summary>
        public static bool TryContains(string id, out bool shipped)
        {
            if (!_read)
            {
                _read = true;
                var text = BlitzResources.LoadText(DataName);
                _ids = text == null ? null : Parse(text);
            }

            shipped = _ids != null && !string.IsNullOrEmpty(id) && _ids.Contains(id);
            return _ids != null;
        }

        /// <summary>Reads the list: one id per line, blank lines skipped.</summary>
        public static HashSet<string> Parse(string text)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(text))
            {
                return ids;
            }

            foreach (var line in text.Split('\n'))
            {
                var id = line.Trim();
                if (id.Length > 0)
                {
                    ids.Add(id);
                }
            }

            return ids;
        }
    }
}
