using BlitzLegacy.Core.Maps;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// One source map object in the <c>navmesh_obstacle</c> group. Godot's AI
    /// uses its root position and mesh AABB when it chooses the nearest object
    /// large enough to hide a tank; this immutable runtime record exposes the
    /// same data without making every controller rescan scene components.
    /// </summary>
    public sealed class BattleAiCover
    {
        private readonly GameObject _root;
        private readonly Vector3 _size;

        private BattleAiCover(
            GameObject root, string sourceName, Vector3 size, bool aiNoHide)
        {
            _root = root;
            SourceName = sourceName;
            _size = size;
            AiNoHide = aiNoHide;
        }

        public string SourceName { get; private set; }

        public bool AiNoHide { get; private set; }

        public Vector3 Position
        {
            get { return _root != null ? _root.transform.position : Vector3.zero; }
        }

        public Vector3 Size { get { return _size; } }

        public float Volume
        {
            get { return Mathf.Abs(_size.x * _size.y * _size.z); }
        }

        public bool IsValid { get { return _root != null && _root.activeInHierarchy; } }

        public static BattleAiCover Create(GameObject root, MapObjectInstance source)
        {
            if (root == null || source == null || !source.NavigationObstacle)
            {
                return null;
            }

            var size = Vector3.zero;
            var filter = root.GetComponentInChildren<MeshFilter>(true);
            if (filter != null && filter.sharedMesh != null)
            {
                var scale = filter.transform.lossyScale;
                scale = new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
                size = Vector3.Scale(filter.sharedMesh.bounds.size, scale);
            }
            else
            {
                var collider = root.GetComponentInChildren<Collider>(true);
                if (collider != null)
                {
                    size = collider.bounds.size;
                }
            }

            if (size.sqrMagnitude <= 0.0001f)
            {
                return null;
            }

            return new BattleAiCover(root, source.Name, size, source.AiNoHide);
        }
    }
}
