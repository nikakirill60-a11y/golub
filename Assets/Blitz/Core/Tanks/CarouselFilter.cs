using System.Collections.Generic;

namespace BlitzLegacy.Core.Tanks
{
    /// <summary>
    /// The carousel filter: tiers, nations and classes are each an OR group
    /// (any ticked value matches) and the groups combine with AND. The status
    /// toggles -- tech tree, premium, collector and mod -- are one more OR
    /// group: every vehicle has exactly one status, the one its name is
    /// painted in, so a mod is never a premium or a collector, a collector is
    /// never a premium and a tech tree vehicle is none of the three. An empty
    /// filter matches every tank.
    /// </summary>
    public sealed class CarouselFilter
    {
        public const int MinLevel = 1;
        public const int MaxLevel = TanksDatabase.MaxLevel;

        private readonly HashSet<int> _levels = new HashSet<int>();
        private readonly HashSet<Nation> _nations = new HashSet<Nation>();
        private readonly HashSet<TankClass> _classes = new HashSet<TankClass>();

        /// <summary>
        /// The white vehicles: the tech tree's own, neither premium nor
        /// collector nor mod.
        /// </summary>
        public bool Regular { get; private set; }

        public bool Premium { get; private set; }
        public bool Collector { get; private set; }

        /// <summary>The light green vehicles: tanks installed from a mod package.</summary>
        public bool Mods { get; private set; }

        /// <summary>Number of ticked values, shown on the filter button.</summary>
        public int ActiveCount
        {
            get
            {
                return _levels.Count + _nations.Count + _classes.Count
                    + (Regular ? 1 : 0) + (Premium ? 1 : 0) + (Collector ? 1 : 0)
                    + (Mods ? 1 : 0);
            }
        }

        public bool IsActive
        {
            get { return ActiveCount > 0; }
        }

        public bool HasLevel(int level)
        {
            return _levels.Contains(level);
        }

        public bool HasNation(Nation nation)
        {
            return _nations.Contains(nation);
        }

        public bool HasClass(TankClass tankClass)
        {
            return _classes.Contains(tankClass);
        }

        public void ToggleLevel(int level)
        {
            if (level < MinLevel || level > MaxLevel)
            {
                return;
            }

            if (!_levels.Remove(level))
            {
                _levels.Add(level);
            }
        }

        public void ToggleNation(Nation nation)
        {
            if (!_nations.Remove(nation))
            {
                _nations.Add(nation);
            }
        }

        public void ToggleClass(TankClass tankClass)
        {
            if (!_classes.Remove(tankClass))
            {
                _classes.Add(tankClass);
            }
        }

        public void ToggleRegular()
        {
            Regular = !Regular;
        }

        public void TogglePremium()
        {
            Premium = !Premium;
        }

        public void ToggleCollector()
        {
            Collector = !Collector;
        }

        public void ToggleMods()
        {
            Mods = !Mods;
        }

        public void Clear()
        {
            _levels.Clear();
            _nations.Clear();
            _classes.Clear();
            Regular = false;
            Premium = false;
            Collector = false;
            Mods = false;
        }

        public bool Matches(TankInfo info)
        {
            if (info == null)
            {
                return false;
            }

            if (_levels.Count > 0 && !_levels.Contains(info.Level))
            {
                return false;
            }

            if (_nations.Count > 0 && !_nations.Contains(info.Nation))
            {
                return false;
            }

            if (_classes.Count > 0 && !_classes.Contains(info.Class))
            {
                return false;
            }

            if (!Regular && !Premium && !Collector && !Mods)
            {
                return true;
            }

            // HangarUiKit.TankNameColor's order: mod over collector over premium.
            if (info.Mod)
            {
                return Mods;
            }

            if (info.Collector)
            {
                return Collector;
            }

            if (info.Premium)
            {
                return Premium;
            }

            return Regular;
        }
    }
}
