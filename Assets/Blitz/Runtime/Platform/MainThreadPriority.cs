using System.Runtime.InteropServices;
using UnityEngine;

namespace BlitzLegacy.Runtime.Platform
{
    /// <summary>
    /// Keeps the Unity main thread at Android's urgent-display priority.
    ///
    /// Unity keeps its main, render and job threads on the big cores at the
    /// default priority. Whenever another busy thread lands on the same core
    /// (a system service, a background sync) the scheduler splits that core
    /// evenly, so the game loop loses half its time and a 60 Hz battle drops
    /// to 20-30 fps with second-long stalls - measured on a Snapdragon 7s Gen
    /// 3 next to a spinning system thread. Android itself runs an app's UI
    /// and render threads at this priority; the game loop is that thread
    /// here. Only the scheduling share changes, never the work done.
    ///
    /// The player resets the priority on its own (focus and loading changes),
    /// so it is checked again once a second; the check is one system call.
    /// </summary>
    public sealed class MainThreadPriority : MonoBehaviour
    {
        /// <summary><c>Process.THREAD_PRIORITY_URGENT_DISPLAY</c>.</summary>
        public const int UrgentDisplay = -8;

#if UNITY_ANDROID && !UNITY_EDITOR
        private const float CheckSeconds = 1f;
        private const int PrioProcess = 0;

        private float _nextCheck;
        private bool _resetLogged;
        private int _tid;

        [DllImport("libc", EntryPoint = "gettid")]
        private static extern int GetTid();

        [DllImport("libc", EntryPoint = "setpriority", SetLastError = true)]
        private static extern int SetPriority(int which, int who, int priority);

        [DllImport("libc", EntryPoint = "getpriority")]
        private static extern int GetPriority(int which, int who);
#endif

        /// <summary>Adds the keeper on Android players; call from the main thread.</summary>
        public static void Attach(Transform parent)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            var holder = new GameObject("MainThreadPriority");
            holder.transform.SetParent(parent, false);
            holder.AddComponent<MainThreadPriority>();
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private void Awake()
        {
            try
            {
                _tid = GetTid();
                var result = SetPriority(PrioProcess, _tid, UrgentDisplay);
                Debug.Log("[Blitz] main thread priority: " + (result == 0
                    ? "nice " + GetPriority(PrioProcess, _tid)
                    : "unchanged, errno " + Marshal.GetLastWin32Error()));
                if (result != 0)
                {
                    enabled = false;
                }
            }
            catch (System.Exception error)
            {
                Debug.LogWarning("[Blitz] main thread priority: " + error.Message);
                enabled = false;
            }
        }

        private void Update()
        {
            var now = Time.unscaledTime;
            if (now < _nextCheck)
            {
                return;
            }

            _nextCheck = now + CheckSeconds;
            var nice = GetPriority(PrioProcess, _tid);
            if (nice <= UrgentDisplay)
            {
                return;
            }

            SetPriority(PrioProcess, _tid, UrgentDisplay);
            if (!_resetLogged)
            {
                _resetLogged = true;
                Debug.Log("[Blitz] main thread priority was reset to nice " + nice + "; raised again");
            }
        }
#endif
    }
}
