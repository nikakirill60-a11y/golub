using UnityEditor;
using UnityEngine;

namespace BlitzLegacy.EditorTools
{
    /// <summary>Batch-mode entry points used by Tools/unity.sh.</summary>
    public static class Ci
    {
        /// <summary>
        /// Reaching this method at all proves every assembly compiled, because
        /// Unity refuses to invoke -executeMethod when scripts fail to build.
        /// </summary>
        public static void CompileOnly()
        {
            Debug.Log("[Blitz] compile ok: " + Application.unityVersion);
            EditorApplication.Exit(0);
        }
    }
}
