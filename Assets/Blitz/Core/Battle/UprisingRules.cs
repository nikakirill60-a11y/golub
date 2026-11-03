using System;
using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Core.Battle
{
    /// <summary>
    /// Uprising economy and upgrade strength. <see cref="Standard"/> is the shipped
    /// rule set (Godot Global.UPRISING_LIVES / ELIXIR_* constants); the Sandbox builds
    /// its own instance from the active preset. Mirrors Global.uprising_*() and
    /// UprisingAbilities.num()/nums()/cost() in the Godot project.
    /// </summary>
    public sealed class UprisingRules
    {
        public const int ElixirKind = 1;

        public static readonly UprisingRules Standard = new UprisingRules(3, 1.0, 2, 100, 5, 100, 1f, null);

        private static readonly string[] MultiplierKeys =
        {
            "dmg_out_mul", "reload_mul", "engine_mul", "ram_out_mul", "ram_in_mul", "dispersion_mul",
        };
        private static readonly string[] AdditiveKeys = { "speed_add", "radius", "health_factor" };

        private readonly int[] _levelCosts;

        public readonly int Lives;
        public readonly double ElixirPerSecond;
        public readonly int ElixirPerDamagingShot;
        public readonly int ElixirDamageStep;
        public readonly int ElixirPerDamageStep;
        public readonly int ElixirPerDeath;
        public readonly float UpgradePower;

        /// <param name="levelCosts">Elixir price per rarity 1..3, or null for catalogue prices.</param>
        public UprisingRules(int lives, double elixirPerSecond, int elixirPerDamagingShot, int elixirDamageStep,
            int elixirPerDamageStep, int elixirPerDeath, float upgradePower, int[] levelCosts)
        {
            Lives = Clamp(lives, 1, 10);
            ElixirPerSecond = double.IsNaN(elixirPerSecond) || double.IsInfinity(elixirPerSecond)
                ? 1.0 : Math.Max(0.0, Math.Min(100.0, elixirPerSecond));
            ElixirPerDamagingShot = Clamp(elixirPerDamagingShot, 0, 1000);
            ElixirDamageStep = Clamp(elixirDamageStep, 1, 10000);
            ElixirPerDamageStep = Clamp(elixirPerDamageStep, 0, 1000);
            ElixirPerDeath = Clamp(elixirPerDeath, 0, 10000);
            UpgradePower = float.IsNaN(upgradePower) || float.IsInfinity(upgradePower)
                ? 1f : Math.Max(0f, Math.Min(10f, upgradePower));
            if (levelCosts != null)
            {
                if (levelCosts.Length != 3) throw new ArgumentException("Three rarity prices expected.", nameof(levelCosts));
                _levelCosts = new int[3];
                for (var i = 0; i < 3; i++) _levelCosts[i] = Clamp(levelCosts[i], 0, 5000);
            }
        }

        /// <summary>Respawns granted at battle start: lives minus the first one.</summary>
        public int Respawns { get { return Math.Max(0, Lives - 1); } }

        public bool OverridesCosts { get { return _levelCosts != null; } }

        /// <summary>Price of an ability. Free (zero-price) catalogue entries stay free.</summary>
        public int Cost(UprisingCatalog catalog, string id)
        {
            if (catalog == null || string.IsNullOrEmpty(id)) return 0;
            var original = catalog.Cost(id);
            if (original <= 0) return 0;
            if (_levelCosts == null) return original;
            return _levelCosts[Clamp(catalog.Rarity(id), 1, 3) - 1];
        }

        /// <summary>Elixir-shop upgrade figure scaled by <see cref="UpgradePower"/>.</summary>
        public float Scale(JsonNode ability, string key, float value)
        {
            if (ability == null || ability.Kind == JsonKind.Null || ability["kind"].AsInt() != ElixirKind) return value;
            if (Array.IndexOf(MultiplierKeys, key) >= 0) return Math.Max(0.05f, 1f + (value - 1f) * UpgradePower);
            if (Array.IndexOf(AdditiveKeys, key) >= 0) return value * UpgradePower;
            return value;
        }

        /// <summary>Array figure (damage_factors) scaled element-wise by <see cref="UpgradePower"/>.</summary>
        public float[] ScaleAll(JsonNode ability, float[] values)
        {
            var scaled = (float[])values.Clone();
            if (ability == null || ability.Kind == JsonKind.Null || ability["kind"].AsInt() != ElixirKind) return scaled;
            for (var i = 0; i < scaled.Length; i++) scaled[i] *= UpgradePower;
            return scaled;
        }

        private static int Clamp(int value, int min, int max) { return value < min ? min : value > max ? max : value; }
    }
}
