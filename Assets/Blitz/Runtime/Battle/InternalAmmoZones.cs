using System.Collections.Generic;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Runtime.Assets;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>Explicit reconstruction, NOT original Blitz server geometry.
    /// Public 4.8 client/Inspector collision files contain outer armour only.
    /// Small stowage volumes are measured in installed part-local space so
    /// turret rotation and different researched parts cannot detach them.</summary>
    public sealed class InternalAmmoZones
    {
        public struct Zone
        {
            public Transform Mount;
            public Bounds Bounds;
        }
        private readonly List<Zone> _zones = new List<Zone>();
        public IReadOnlyList<Zone> Zones => _zones;
        public float ProjectileChance { get; private set; }
        public float TraceDepthCalibres { get; private set; }
        public void Build(BattleTank tank)
        {
            _zones.Clear();
            var rules = JsonResources.Load("Data/legacy480_wreck_manifest");
            ProjectileChance = rules["moduleRules"]["ammoBay"]["projectile"].AsFloat();
            var layout = JsonResources.Load("Data/ammo_zone_reconstruction");
            TraceDepthCalibres = layout["traceDepthCalibres"].AsFloat();
            var profile = layout["profiles"][tank.Card.Name];
            if (profile.Kind != JsonKind.Array) profile = layout["profiles"][tank.IsTdNoFullRotation ? "casemate" : "default"];
            foreach (var entry in profile.Items)
            {
                var turret = entry["part"].AsString() == "turret";
                Transform mount = null;
                var bounds = new Bounds();
                var found = false;
                foreach (var plate in tank.Plates)
                {
                    if (plate.Screen || !string.IsNullOrEmpty(plate.Module) || plate.Shape == null ||
                        (turret ? !plate.Mount.StartsWith("turret") : plate.Mount != "hull")) continue;
                    var part = plate.FollowMount;
                    if (part == null) continue;
                    if (mount == null) mount = part;
                    if (part != mount) continue;
                    foreach (var vertex in plate.Shape.sharedMesh.vertices)
                    {
                        var local = mount.InverseTransformPoint(plate.transform.TransformPoint(vertex));
                        if (!found) { bounds = new Bounds(local, Vector3.zero); found = true; }
                        else bounds.Encapsulate(local);
                    }
                }
                if (!found || bounds.size.sqrMagnitude < .001f) continue;
                var centre = ReadVector(entry["centre"]);
                var size = ReadVector(entry["size"]);
                _zones.Add(new Zone { Mount = mount, Bounds = new Bounds(
                    bounds.center + Vector3.Scale(bounds.size, centre), Vector3.Scale(bounds.size, size)) });
            }
        }
        private static Vector3 ReadVector(JsonNode n) => new Vector3(n[0].AsFloat(), n[1].AsFloat(), n[2].AsFloat());

        /// <summary>Inspect the finite path behind the penetrated surface. A miss never consumes the damage-chance random roll.</summary>
        public bool Intersects(Vector3 entry, Vector3 direction, float distance)
        {
            if (direction.sqrMagnitude < .001f || distance <= 0f) return false;
            foreach (var zone in _zones)
            {
                var from = zone.Mount.InverseTransformPoint(entry);
                var to = zone.Mount.InverseTransformPoint(entry + direction.normalized * distance);
                var delta = to - from;
                if (zone.Bounds.Contains(from)) return true;
                if (delta.sqrMagnitude < .000001f) continue;
                if (zone.Bounds.IntersectRay(new Ray(from, delta.normalized), out var hit) && hit <= delta.magnitude)
                    return true;
            }
            return false;
        }
    }
}
