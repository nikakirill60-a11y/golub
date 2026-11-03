using UnityEngine;

namespace BlitzLegacy.Runtime.Lighting
{
    /// <summary>
    /// Marks a light the source draws without shadows at any setting, such as
    /// the fill light a hangar aims at the vehicle. The graphics tab's shadow
    /// row leaves it shadowless.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShadowlessLight : MonoBehaviour
    {
    }
}
