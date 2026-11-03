using System;
using BlitzLegacy.Core.Maps;
using BlitzLegacy.Runtime.Battle;
using UnityEditor;
using UnityEngine;

namespace BlitzLegacy.EditorTools
{
    /// <summary>
    /// Answers the two questions a bot asks before it drives anywhere: can the
    /// navmesh project this marker, and does a path from the spawn reach it.
    /// A map whose markers all fail both is a map where every bot stands still,
    /// which is exactly what Karieri did.
    /// </summary>
    public static class MapRouteAudit
    {
        public static void Run()
        {
            var mapId = Environment.GetEnvironmentVariable("BLITZ_MAP");
            if (string.IsNullOrEmpty(mapId)) { mapId = "karieri"; }

            var map = BattleWorld.LoadDefinition(mapId);
            var root = new GameObject("RouteAudit");
            var navigation = BattleNavigation.Create(root.transform, mapId);
            Debug.Log("[Blitz] route audit " + mapId + ": ready=" + navigation.Ready
                + " polygons=" + navigation.PolygonCount);

            var spawnA = Point(map.TeamA[0].Origin);
            var spawnB = Point(map.TeamB[0].Origin);
            Vector3 fromA;
            Vector3 fromB;
            var okA = navigation.ClosestPoint(spawnA, out fromA);
            var okB = navigation.ClosestPoint(spawnB, out fromB);
            Debug.Log("[Blitz] route audit spawnA=" + spawnA + " projected=" + okA
                + " " + fromA + " drift=" + (okA ? Vector3.Distance(spawnA, fromA) : -1f));
            Debug.Log("[Blitz] route audit spawnB=" + spawnB + " projected=" + okB
                + " " + fromB + " drift=" + (okB ? Vector3.Distance(spawnB, fromB) : -1f));

            // Every slot, not just the first: a slot parked on a navmesh
            // island is a bot that never leaves the spawn.
            var teams = new[] { map.TeamA, map.TeamB };
            for (var t = 0; t < teams.Length; t++)
            {
                for (var s = 0; s < teams[t].Count; s++)
                {
                    var slot = Point(teams[t][s].Origin);
                    Vector3 projectedSlot;
                    var slotOk = navigation.ClosestPoint(slot, out projectedSlot);
                    var reachesCentre = 0;
                    for (var m = 0; m < map.AiMarkers.Count; m++)
                    {
                        Vector3 goal;
                        if (slotOk && navigation.ClosestPoint(Point(map.AiMarkers[m].Origin), out goal)
                            && navigation.PathReaches(projectedSlot, goal))
                        {
                            reachesCentre++;
                        }
                    }

                    Debug.Log("[Blitz] route audit slot team=" + t + " #" + s + " at=" + slot
                        + " projected=" + slotOk + " " + projectedSlot
                        + " drift=" + (slotOk ? Vector3.Distance(slot, projectedSlot) : -1f)
                        + " reachableMarkers=" + reachesCentre + "/" + map.AiMarkers.Count);
                }
            }

            for (var i = 0; i < map.AiMarkers.Count; i++)
            {
                var marker = map.AiMarkers[i];
                var point = Point(marker.Origin);
                Vector3 projected;
                var projectedOk = navigation.ClosestPoint(point, out projected);
                var reachA = projectedOk && okA && navigation.PathReaches(fromA, projected);
                var reachB = projectedOk && okB && navigation.PathReaches(fromB, projected);
                Debug.Log("[Blitz] route audit marker " + marker.Name
                    + " type=" + marker.Type + " at=" + point
                    + " projected=" + projectedOk + " " + projected
                    + " drift=" + (projectedOk ? Vector3.Distance(point, projected) : -1f)
                    + " reachA=" + reachA + " reachB=" + reachB);
            }

            // A coarse sweep of the play area, so authored routes can be put
            // where the bake actually is walkable from both spawns.
            for (var x = -200f; x <= 200f; x += 50f)
            {
                for (var z = -200f; z <= 200f; z += 50f)
                {
                    var probe = new Vector3(x, 40f, z);
                    Vector3 projected;
                    if (!navigation.ClosestPoint(probe, out projected)) { continue; }
                    if (Vector3.Distance(
                            new Vector3(projected.x, 0f, projected.z),
                            new Vector3(x, 0f, z)) > 8f)
                    {
                        continue;
                    }

                    var reachA = okA && navigation.PathReaches(fromA, projected);
                    var reachB = okB && navigation.PathReaches(fromB, projected);
                    Debug.Log("[Blitz] route audit cell " + x + "," + z
                        + " surface=" + projected
                        + " reachA=" + reachA + " reachB=" + reachB);
                }
            }

            EditorApplication.Exit(0);
        }

        private static Vector3 Point(float[] origin)
        {
            var point = GodotSpace.Point(origin);
            return new Vector3(point.X, point.Y, point.Z);
        }
    }
}
