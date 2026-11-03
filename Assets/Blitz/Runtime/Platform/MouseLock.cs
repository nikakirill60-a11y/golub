using UnityEngine;

namespace BlitzLegacy.Runtime.Platform
{
    /// <summary>
    /// Port of Util.mouse_lock / Util.get_mouse_lock from scripts/_util.gd.
    ///
    /// The source captures the pointer on a desktop build and leaves it alone
    /// on a phone, where a capture would make no sense:
    ///
    ///   Input.mouse_mode = MOUSE_MODE_CAPTURED if lock &amp;&amp; !is_mobile_platform()
    ///                      else MOUSE_MODE_VISIBLE
    ///   get_mouse_lock() -&gt; is_mobile_platform() || mouse_mode == CAPTURED
    ///
    /// so every gate that reads get_mouse_lock() is always open on touch and
    /// closes on desktop exactly while a menu or a popup owns the pointer.
    ///
    /// The captured state is mirrored in a field instead of read back from
    /// Cursor.lockState: a batch-mode editor has no window to lock, and the
    /// gates still have to behave the way the source does.
    /// </summary>
    public static class MouseLock
    {
        private static bool _captured;

        /// <summary>SaveSettings.is_mobile_platform().</summary>
        public static bool IsMobile
        {
            get { return Application.isMobilePlatform; }
        }

        /// <summary>Util.get_mouse_lock().</summary>
        public static bool Locked
        {
            get { return IsMobile || _captured; }
        }

        /// <summary>The raw mouse_mode, without the mobile shortcut.</summary>
        public static bool Captured
        {
            get { return _captured; }
        }

        /// <summary>
        /// Frame until which the mouse delta is a warp artefact of a fresh
        /// grab rather than the player's own movement.
        /// </summary>
        public static int IgnoreDeltaUntilFrame { get; private set; }

        /// <summary>
        /// Keeps the desktop grab alive. On Linux (X11 and XWayland) and after
        /// an alt-tab the window manager can drop the pointer grab while Unity
        /// and this mirror still think it is held. The hidden cursor then
        /// drifts until it hits the top or bottom edge of the screen, where the
        /// vertical delta reads zero: the camera stops tilting while turning
        /// still works, seemingly at random. Re-applying the lock whenever it
        /// has slipped restores the grab.
        /// </summary>
        public static void Enforce()
        {
            if (!_captured) { return; }
            if (Cursor.lockState != CursorLockMode.Locked)
            {
                Relock();
                return;
            }

            if (Cursor.visible) { Cursor.visible = false; }
        }

        /// <summary>
        /// Drops and retakes the grab, which recentres the pointer. The
        /// recentring jump is ignored for one frame.
        /// </summary>
        public static void Relock()
        {
            if (!_captured) { return; }
            Cursor.lockState = CursorLockMode.None;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            IgnoreDeltaUntilFrame = Time.frameCount + 1;
        }

        /// <summary>Util.mouse_lock(lock).</summary>
        public static void Set(bool locked)
        {
            _captured = locked && !IsMobile;
            Cursor.lockState = _captured ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !_captured;
        }
    }
}
