using UnityEngine;

namespace BlitzLegacy.Runtime
{
    /// <summary>
    /// What the source asks <c>OS.is_debug_build()</c> for.
    ///
    /// In Godot a debug export template carries the debug tools and nothing
    /// else: there is no engine-drawn console on top of the game. Unity ties
    /// both to one flag, <c>BuildOptions.Development</c>, so a build with the
    /// debug tools also gets the development player's on-screen log console
    /// and watermark - which is not what the alpha testers should be looking
    /// at. The production-debug APK is therefore built without that option and
    /// with <c>BLITZ_DEBUG_TOOLS</c> defined instead, and this is the flag the
    /// game asks rather than <c>Debug.isDebugBuild</c> directly.
    /// </summary>
    public static class BlitzBuildFlags
    {
        public static bool DebugTools
        {
            get
            {
#if BLITZ_DEBUG_TOOLS
                return true;
#else
                return Debug.isDebugBuild;
#endif
            }
        }

        /// <summary>
        /// True only in a player Unity itself marks as a development build, so
        /// the engine console and watermark are on. Kept separate from
        /// <see cref="DebugTools"/> so the two questions cannot be confused.
        /// </summary>
        public static bool DevelopmentPlayer
        {
            get { return Debug.isDebugBuild; }
        }
    }
}
