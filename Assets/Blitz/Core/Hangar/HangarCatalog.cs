using System;
using System.Collections.Generic;

namespace BlitzLegacy.Core.Hangar
{
    /// <summary>One hangar the settings screen offers.</summary>
    public sealed class HangarInfo
    {
        public HangarInfo(
            HangarKind kind, string id, string nameKey, bool requiresPremium, bool ported,
            bool musicPlayerByDefault)
        {
            Kind = kind;
            Id = id;
            NameKey = nameKey;
            RequiresPremium = requiresPremium;
            Ported = ported;
            MusicPlayerByDefault = musicPlayerByDefault;
        }

        public HangarKind Kind { get; }

        /// <summary>The id the settings file stores: the map name of the source scene.</summary>
        public string Id { get; }

        /// <summary>lang.json key of the name the picker shows.</summary>
        public string NameKey { get; }

        /// <summary>Only an account with premium may pick it.</summary>
        public bool RequiresPremium { get; }

        /// <summary>
        /// Drawn by the ported 11.x scene runtime, which needs its own shaders: a device (or a headless
        /// player) without them keeps the 4.8 hangars.
        /// </summary>
        public bool Ported { get; }

        /// <summary>Whether the hangar music player is on until the player switches it.</summary>
        public bool MusicPlayerByDefault { get; }
    }

    /// <summary>Every hangar the client can show, in the order the picker lists them.</summary>
    public static class HangarCatalog
    {
        public const string RegularId = "hangar";
        public const string PremiumId = "hangar_prem_2018";
        public const string Halloween2026Id = "hangar_halloween_2026";
        public const string MikuId = "hangar_miku_void";

        private static readonly HangarInfo[] Entries =
        {
            new HangarInfo(HangarKind.Regular, RegularId, "ui:hangar/regular", false, false, false),
            new HangarInfo(HangarKind.Premium, PremiumId, "ui:hangar/premium", true, false, false),
            new HangarInfo(
                HangarKind.Halloween2026, Halloween2026Id, "ui:hangar/halloween_2026", false, true, false),
            new HangarInfo(HangarKind.Miku, MikuId, "ui:hangar/miku", false, true, true),
        };

        public static IReadOnlyList<HangarInfo> All
        {
            get { return Entries; }
        }

        public static HangarInfo Get(HangarKind kind)
        {
            for (var i = 0; i < Entries.Length; i++)
            {
                if (Entries[i].Kind == kind)
                {
                    return Entries[i];
                }
            }

            throw new ArgumentOutOfRangeException("kind", kind, "No catalog entry for this hangar");
        }

        /// <summary>The entry stored under <paramref name="id"/>, or null for an empty or unknown id.</summary>
        public static HangarInfo Find(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }

            for (var i = 0; i < Entries.Length; i++)
            {
                if (Entries[i].Id == id)
                {
                    return Entries[i];
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Which hangar is shown for the choice made in the settings. No choice (the default) keeps the
    /// schedule: the seasonal hangar while its window is open, the account hangar otherwise. A choice is
    /// honoured while the account may use it and the device can draw it; a premium hangar picked before
    /// the premium ran out, or a ported one on a device without its shaders, falls back to the schedule
    /// and comes back by itself once it is usable again.
    /// </summary>
    public static class HangarSelection
    {
        public static bool IsAvailable(HangarInfo info, bool premium, bool portedDrawable)
        {
            return info != null && (premium || !info.RequiresPremium) && (portedDrawable || !info.Ported);
        }

        public static HangarKind Resolve(string choice, DateTime localTime, bool premium, bool portedDrawable = true)
        {
            var info = HangarCatalog.Find(choice);
            return IsAvailable(info, premium, portedDrawable)
                ? info.Kind
                : HangarSchedule.Resolve(localTime, premium, portedDrawable);
        }

        /// <summary><see cref="Resolve"/> at a unix time on the device's local calendar.</summary>
        public static HangarKind ResolveLocal(
            string choice, double unixSeconds, bool premium, bool portedDrawable = true)
        {
            return Resolve(choice, HangarSchedule.LocalTime(unixSeconds, null), premium, portedDrawable);
        }
    }
}
