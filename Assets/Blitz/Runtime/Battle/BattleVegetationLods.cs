using System.Collections.Generic;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// map_object_vegetation.gd's distance switch for a Godot render scene:
    /// every bush and tree crown shows its billboard MultiMesh near the
    /// camera and its Sprite3D "lods" once
    /// <c>distance_squared * fov &gt; 1000^2</c>, measured from the vegetation
    /// node (which rides a falling tree's trunk). One component evaluates all
    /// groups, so 264 crowns do not each pay for an Update.
    /// </summary>
    public sealed class BattleVegetationLods : MonoBehaviour
    {
        /// <summary>The source's <c>1000 ** 2</c>.</summary>
        public const float FarThreshold = 1000f * 1000f;

        /// <summary>A camera that renders without being Camera.main (captures).</summary>
        public static Camera ViewCamera;

        private sealed class Group
        {
            public Transform Origin;
            public Renderer[] Near;
            public Renderer[] Far;
            public bool IsFar;
        }

        private readonly List<Group> _groups = new List<Group>();

        public int GroupCount { get { return _groups.Count; } }

        /// <summary>Groups currently drawing their far sprites.</summary>
        public int FarCount { get; private set; }

        public void Add(Transform origin, Renderer[] near, Renderer[] far)
        {
            _groups.Add(new Group { Origin = origin, Near = near, Far = far });
            Show(near, true);
            Show(far, false);
        }

        /// <summary>The source's state test, kept separate for the tests.</summary>
        public static bool IsFar(Vector3 camera, Vector3 origin, float fov)
        {
            return (camera - origin).sqrMagnitude * fov > FarThreshold;
        }

        private void LateUpdate()
        {
            Refresh(ViewCamera != null ? ViewCamera : Camera.main);
        }

        public void Refresh(Camera camera)
        {
            if (camera == null)
            {
                return;
            }

            var position = camera.transform.position;
            var fov = camera.fieldOfView;
            var far = 0;
            for (var i = 0; i < _groups.Count; i++)
            {
                var group = _groups[i];
                if (group.Origin == null)
                {
                    continue;
                }

                var isFar = IsFar(position, group.Origin.position, fov);
                if (isFar != group.IsFar)
                {
                    group.IsFar = isFar;
                    Show(group.Near, !isFar);
                    Show(group.Far, isFar);
                }
                if (isFar) far++;
            }
            FarCount = far;
        }

        private static void Show(Renderer[] renderers, bool visible)
        {
            for (var i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null) renderers[i].enabled = visible;
            }
        }
    }
}
