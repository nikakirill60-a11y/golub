namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// The collision layers the source names in its project settings. Godot
    /// layers 1..9 land on Unity user layers 8..16, which is the order
    /// <c>ProjectBootstrap.ApplyLayers</c> writes into the tag manager.
    /// </summary>
    public static class BattleLayers
    {
        /// <summary>Godot layer 1: the map and everything nailed to it.</summary>
        public const int Static = 8;

        /// <summary>Godot layer 2: the hull shell a tank drives around on.</summary>
        public const int Physics = 9;

        /// <summary>Godot layer 3.</summary>
        public const int Wheel = 10;

        /// <summary>Godot layer 4: armour plates.</summary>
        public const int Armor = 11;

        /// <summary>Godot layer 5: spaced armour.</summary>
        public const int Screen = 12;

        /// <summary>Godot layer 6.</summary>
        public const int Foliage = 13;

        /// <summary>Godot layer 7.</summary>
        public const int Visibility = 14;

        /// <summary>Godot layer 8, which a destroyed tank also joins.</summary>
        public const int Modules = 15;

        /// <summary>Godot layer 9: the map objects a shell can break.</summary>
        public const int Destructibles = 16;

        /// <summary>
        /// <c>projectile.gd</c> traces with mask <c>1 + 8 + 16 + 2 ** 8</c>:
        /// the map, armour, screens and destructibles, but never the physics
        /// shell of a tank, which sits inside its own armour. Everything the
        /// map ships still rides the default layer in this port, so the rule is
        /// spelled as every layer except that shell.
        /// </summary>
        public static int AimMask
        {
            get { return (1 << Static) | (1 << Armor) | (1 << Screen); }
        }

        /// <summary>
        /// <c>suspension_component.gd</c> builds every wheel ray with
        /// <c>collision_mask = 1 + 2</c>: the map and the physics shell of a
        /// tank. Armour plates, spaced armour, foliage, modules and
        /// destructibles are all outside it, so a hull never rides on its own
        /// plates.
        /// </summary>
        public static int WheelMask
        {
            get { return (1 << Static) | (1 << Physics); }
        }

        public static int ProjectileMask
        {
            get { return AimMask | (1 << Destructibles); }
        }

        /// <summary>
        /// <c>Projectile.explode</c> fans its rays with mask <c>8 + 2 ** 8</c>:
        /// armour plates and destructibles only. Terrain, static props and
        /// spaced armour are all outside it, so splash is never stopped by
        /// cover and a screen never shields the hull behind it from a burst.
        /// </summary>
        public static int ExplosionMask
        {
            get { return (1 << Armor) | (1 << Destructibles); }
        }
    }
}
