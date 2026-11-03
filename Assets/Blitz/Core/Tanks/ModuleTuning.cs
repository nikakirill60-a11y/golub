namespace BlitzLegacy.Core.Tanks
{
    /// <summary>
    /// The constants the module damage of <c>base_tank.gd</c> is authored
    /// with, plus the two rules the Blitz 4.8.0 client states in its own
    /// strings but keeps on the server.
    /// </summary>
    public static class ModuleTuning
    {
        /// <summary><c>get_repair_time()</c>: eight seconds.</summary>
        public const float RepairTime = 8f;

        /// <summary>The toolbox cuts a quarter off that.</summary>
        public const float ToolboxRepairFactor = 0.75f;

        /// <summary>Equipment id of the toolbox, <c>TanksDataBase.eq_toolbox</c>.</summary>
        public const string ToolboxId = "toolbox";

        /// <summary>
        /// <c>module_collide</c>: inside this share of half the track length
        /// the run is backed by the hull and takes a quarter of the damage.
        /// </summary>
        public const float TracksCentreShare = 0.75f;

        /// <summary>
        /// `ammo_bay_damaged`: "Loading speed decreased". The pack ships the
        /// message but not the coefficient, which the client keeps server
        /// side, so the port uses the series' own half-again reload.
        /// </summary>
        public const float AmmoBayDamagedReload = 1.5f;

        /// <summary><c>Audio.tracks_destroy</c>.</summary>
        public const string TracksDestroySound = "tracks_destroy";

        /// <summary><c>Audio.tracks_fix</c>.</summary>
        public const string TracksFixSound = "tracks_fix";
    }
}
