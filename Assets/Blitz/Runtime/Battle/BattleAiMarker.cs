using BlitzLegacy.Core.Maps;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// maps/components/ai_marker.gd as it behaves in a running battle. The flag
    /// mesh, the radius cylinder and the gizmo extents the script builds live
    /// behind Engine.is_editor_hint(), and _ready() clears the children again
    /// before pushing the marker into Battle.ai_markers, so the port keeps the
    /// data and the occupier bookkeeping and nothing else.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BattleAiMarker : MonoBehaviour
    {
        public MapAiMarkerType Type { get; private set; }

        /// <summary>The radius a bot roams inside, in metres.</summary>
        public float Radius { get; private set; }

        /// <summary>The 0..5 weight the tactical scoring adds to a marker.</summary>
        public int Priority { get; private set; }

        /// <summary>The tank that called dibs, as ai_controller.gd does.</summary>
        public Component Occupier { get; private set; }

        public static BattleAiMarker Create(Transform parent, MapAiMarker definition)
        {
            var host = new GameObject(definition.Name);
            host.transform.SetParent(parent, false);

            var point = GodotSpace.Point(definition.Origin);
            host.transform.localPosition = new Vector3(point.X, point.Y, point.Z);

            var marker = host.AddComponent<BattleAiMarker>();
            marker.Type = definition.Type;
            marker.Radius = definition.Radius;
            marker.Priority = definition.Priority;
            return marker;
        }

        public bool IsSniper()
        {
            return Type == MapAiMarkerType.Snipe;
        }

        public bool IsNormal()
        {
            return Type == MapAiMarkerType.PriorityPrimary
                || Type == MapAiMarkerType.PrioritySecondary
                || Type == MapAiMarkerType.PriorityTertiary;
        }

        public void Occupy(Component owner)
        {
            Occupier = owner;
        }

        /// <summary>
        /// The source compares against null, and a destroyed Unity object also
        /// compares equal to null, so a marker frees itself when its occupier
        /// leaves the battle.
        /// </summary>
        public bool CanOccupy()
        {
            return Occupier == null;
        }
    }
}
