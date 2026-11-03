using UnityEngine;

namespace BlitzLegacy.Runtime.Lighting
{
    /// <summary>
    /// Marks a sun whose shadows are already in the map's lightmap (a Godot
    /// render scene with shadow_enabled off). The graphics tab's shadow row
    /// leaves such a light without realtime shadows, as the source draws it.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BakedSun : MonoBehaviour
    {
    }
}
