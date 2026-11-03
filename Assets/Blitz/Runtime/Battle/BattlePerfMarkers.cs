using Unity.Profiling;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// Profiler markers around the battle's physics queries and AI phases.
    /// They cost nothing in a release player and give the profiler (and the
    /// development-build <c>[BlitzPerf]</c> report) a name for the time each
    /// one spends inside the fixed step.
    /// </summary>
    internal static class BattlePerfMarkers
    {
        internal static readonly ProfilerMarker GunTrace = new ProfilerMarker("Blitz.GunTrace");
        internal static readonly ProfilerMarker ArmorPlan = new ProfilerMarker("Blitz.AiArmorPlan");
        internal static readonly ProfilerMarker AiSight = new ProfilerMarker("Blitz.AiSight");
        internal static readonly ProfilerMarker AiObstacle = new ProfilerMarker("Blitz.AiObstacle");
        internal static readonly ProfilerMarker AiState = new ProfilerMarker("Blitz.AiState");
        internal static readonly ProfilerMarker AiNavigation = new ProfilerMarker("Blitz.AiNavigation");
        internal static readonly ProfilerMarker AiTactics = new ProfilerMarker("Blitz.AiTactics");
        internal static readonly ProfilerMarker WheelCast = new ProfilerMarker("Blitz.WheelCast");
        internal static readonly ProfilerMarker WheelCastOwn = new ProfilerMarker("Blitz.WheelCastOwn");
        internal static readonly ProfilerMarker WheelForces = new ProfilerMarker("Blitz.WheelForces");
        internal static readonly ProfilerMarker SuspensionDrive = new ProfilerMarker("Blitz.SuspensionDrive");
        internal static readonly ProfilerMarker TrackMarkCast = new ProfilerMarker("Blitz.TrackMarkCast");
        internal static readonly ProfilerMarker SpotBush = new ProfilerMarker("Blitz.SpotBush");
        internal static readonly ProfilerMarker SpotSight = new ProfilerMarker("Blitz.SpotSight");
        internal static readonly ProfilerMarker CamTrace = new ProfilerMarker("Blitz.CamTrace");
        internal static readonly ProfilerMarker TankTurret = new ProfilerMarker("Blitz.TankTurret");
        internal static readonly ProfilerMarker TankTracks = new ProfilerMarker("Blitz.TankTracks");
        internal static readonly ProfilerMarker TankUpkeep = new ProfilerMarker("Blitz.TankUpkeep");
        internal static readonly ProfilerMarker UprisingActive = new ProfilerMarker("Blitz.UprisingActive");
    }
}
