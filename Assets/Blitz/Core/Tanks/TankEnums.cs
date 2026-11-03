namespace BlitzLegacy.Core.Tanks
{
    /// <summary>Port of Godot <c>TankInfoMain.Nation</c>.</summary>
    public enum Nation
    {
        None = 0,
        Ussr = 1,
        Germany = 2,
        Usa = 3,
        Gb = 4,
        Japan = 5,
        France = 6,
        China = 7,

        /// <summary>
        /// Blitz 4.8.0 files the crossover vehicles (Sega, Warhammer, Hellsing,
        /// Dracula and the loose one-offs) under an eighth nation. Godot stored
        /// it as <c>Nation.NONE</c> at index 0 of <c>nation_flags</c>; the live
        /// tank table keeps the client's own value, so the enum carries it as 8
        /// and the flag lookup maps it back to the same plate.
        /// </summary>
        Other = 8,
        /// <summary>Blitz 11.20 European tech tree (Poland, Sweden, etc.).</summary>
        European = 9,
    }

    /// <summary>Port of Godot <c>TankInfoMain.Class</c>.</summary>
    public enum TankClass
    {
        Light = 0,
        Medium = 1,
        Heavy = 2,
        TankDestroyer = 3,
    }

    /// <summary>Port of Godot <c>ShellInfo.Type</c>.</summary>
    public enum ShellType
    {
        Ap = 0,
        Apcr = 1,
        Heat = 2,
        He = 3,
        Hesh = 4,
    }

    /// <summary>Port of Godot <c>Global.ResearchStatus</c>.</summary>
    public enum ResearchStatus
    {
        NotAvailable = 0,
        Available = 1,
        Researched = 2,
        InstalledBought = 3,
    }

    /// <summary>
    /// Module row of the modules screen. Turret comes from the Blitz client,
    /// which researches a turret like any other module; the Godot cards had no
    /// turret at all.
    /// </summary>
    public enum ModuleKind
    {
        Gun = 0,
        Engine = 1,
        Tracks = 2,
        Turret = 3,
        Radio = 4,
    }
}
