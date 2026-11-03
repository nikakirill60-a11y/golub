using System;
using UnityEngine;

namespace BlitzLegacy.Runtime.Platform
{
    /// <summary>Android Toast, used as the temporary stand-in for the hangar.</summary>
    public static class DeviceToast
    {
        public static void Show(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return;
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                activity.Call("runOnUiThread", new AndroidJavaRunnable(() =>
                {
                    var toastClass = new AndroidJavaClass("android.widget.Toast");
                    var toast = toastClass.CallStatic<AndroidJavaObject>(
                        "makeText", activity, message, 0);
                    toast.Call("show");
                }));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Blitz] toast failed: " + e.Message);
            }
#else
            Debug.Log("[Blitz] toast: " + message);
#endif
        }
    }
}
