using System;
using BlitzLegacy.Core.Save;
using UnityEngine;

namespace BlitzLegacy.Runtime.Save
{
    /// <summary>
    /// Wraps the save data key with an AES-256-GCM key held by the Android
    /// Keystore (<c>Assets/Plugins/Android/SaveKeystore.java</c>). That key
    /// cannot be exported, is not part of any backup and is deleted with the
    /// app, so the wrapped save key only ever opens on this install.
    /// </summary>
    public sealed class AndroidKeystoreProtector : ISaveKeyProtector
    {
        private const string JavaClass = "ru.blitzlegacy.save.SaveKeystore";

        public string Protect(byte[] key)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            using (var keystore = new AndroidJavaClass(JavaClass))
            {
                var wrapped = keystore.CallStatic<string>("wrap", Convert.ToBase64String(key));
                return string.IsNullOrEmpty(wrapped) ? null : wrapped;
            }
#else
            return null;
#endif
        }

        public bool TryUnprotect(string wrapped, out byte[] key)
        {
            key = null;
#if UNITY_ANDROID && !UNITY_EDITOR
            using (var keystore = new AndroidJavaClass(JavaClass))
            {
                var plain = keystore.CallStatic<string>("unwrap", wrapped ?? string.Empty);
                if (string.IsNullOrEmpty(plain))
                {
                    return false;
                }

                key = Convert.FromBase64String(plain);
                return true;
            }
#else
            return false;
#endif
        }
    }
}
