using System;
using BlitzLegacy.Core.Maps;
using UnityEngine;

namespace BlitzLegacy.Runtime.Lighting
{
    /// <summary>
    /// Places a Unity transform from a source node's basis and origin.
    ///
    /// Both ported scenes need this: the map's props come from the object
    /// table and the hangar's from the bake manifest, but a source transform is
    /// a source transform, and the mirror it needs is subtle enough that one
    /// implementation is worth more than two that can drift.
    /// </summary>
    public static class SourcePlacement
    {
        /// <summary>
        /// Every authored basis in both scenes is square, unmirrored and free
        /// of shear, so one rotation plus the three column lengths reproduce it
        /// exactly. GodotSpace mirrors Z on the way in, which is what carries
        /// the source's right-handed space into Unity's left-handed one.
        /// </summary>
        public static void Place(Transform target, float[] origin, float[] basis)
        {
            if (target == null)
            {
                throw new ArgumentNullException("target");
            }

            var point = GodotSpace.Point(origin);
            target.localPosition = new Vector3(point.X, point.Y, point.Z);

            Vec3 right, up, forward;
            GodotSpace.Basis(basis, out right, out up, out forward);
            var sideways = new Vector3(right.X, right.Y, right.Z);
            var upward = new Vector3(up.X, up.Y, up.Z);
            var ahead = new Vector3(forward.X, forward.Y, forward.Z);

            target.localRotation = Quaternion.LookRotation(ahead, upward);
            target.localScale = new Vector3(
                sideways.magnitude, upward.magnitude, ahead.magnitude);
        }
    }
}
