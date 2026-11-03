using System.Collections.Generic;
using BlitzLegacy.Core.Maps;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    public sealed class BattleNavigationTests
    {
        private const float Tolerance = 0.0001f;

        private GameObject _root;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("NavigationTests");
        }

        [TearDown]
        public void TearDown()
        {
            if (_root != null)
            {
                Object.DestroyImmediate(_root);
                _root = null;
            }
        }

        /// <summary>
        /// The bake the source actually ships is <c>maps/rudniki/navmesh.res</c>,
        /// not the editor scene's settings, so the ported blob is checked
        /// against the resource's own header.
        /// </summary>
        [Test]
        public void PortedTable_MatchesTheSourceBake()
        {
            var table = LoadTable();

            Assert.AreEqual(1, table.RegionCount);
            var region = table.RegionAt(0);
            Assert.AreEqual(1850, region.VertexCount);
            Assert.AreEqual(2502, region.PolygonCount);

            Assert.AreEqual(1.5f, region.CellSize, Tolerance);
            Assert.AreEqual(0.25f, region.CellHeight, Tolerance);
            Assert.AreEqual(3f, region.AgentRadius, Tolerance);
            Assert.AreEqual(1.5f, region.AgentHeight, Tolerance);
            Assert.AreEqual(0.5f, region.AgentMaxClimb, Tolerance);
            Assert.AreEqual(25f, region.AgentMaxSlope, Tolerance);

            for (var p = 0; p < region.PolygonCount; p++)
            {
                var polygon = region.PolygonAt(p);
                Assert.GreaterOrEqual(polygon.Length, 3,
                    "every navmesh polygon is at least a triangle");
                for (var i = 0; i < polygon.Length; i++)
                {
                    Assert.Less(polygon[i], region.VertexCount);
                }
            }
        }

        /// <summary>
        /// The two questions the source's bots ask the navigation map: project
        /// a goal onto the walkable surface, and say whether a path reaches it.
        /// The spawn-to-spawn route is the one that has to work - a bot that
        /// cannot reach the enemy spawn has nowhere to drive.
        /// </summary>
        [Test]
        public void Navigation_AnswersTheSourceQueries()
        {
            var navigation = BattleNavigation.Create(
                _root.transform, BattleWorld.DefaultMapId);

            Assert.IsTrue(navigation.Ready, "the ported navmesh loads and bakes");
            Assert.AreEqual(2502, navigation.PolygonCount);
            Assert.AreEqual(2502, navigation.TriangleCount,
                "the source bake is already triangulated");

            var region = LoadTable().RegionAt(0);
            var first = region.VertexAt(0);
            var onMesh = new Vector3(first.X, first.Y, first.Z);

            Vector3 projected;
            Assert.IsTrue(
                navigation.ClosestPoint(onMesh + Vector3.up * 50f, out projected),
                "a point above the surface projects back onto it");
            Assert.Less(
                Vector2.Distance(
                    new Vector2(projected.x, projected.z),
                    new Vector2(onMesh.x, onMesh.z)),
                2f,
                "and it projects straight down, not across the map");
            Assert.IsTrue(navigation.PathReaches(projected, projected),
                "a goal under the tank is trivially reachable");

            var map = BattleWorld.LoadDefinition(BattleWorld.DefaultMapId);
            Assert.Greater(map.TeamA.Count, 0);
            Assert.Greater(map.TeamB.Count, 0);

            Vector3 fromBot;
            Vector3 toPlayer;
            Assert.IsTrue(
                navigation.ClosestPoint(Spawn(map.TeamB[0]), out fromBot),
                "the bot spawn sits on the walkable surface");
            Assert.IsTrue(
                navigation.ClosestPoint(Spawn(map.TeamA[0]), out toPlayer),
                "so does the player spawn");
            Assert.IsTrue(navigation.PathReaches(fromBot, toPlayer),
                "and the bake connects the two spawns");

            var route = new List<Vector3>();
            Assert.IsTrue(navigation.CalculateRoute(fromBot, toPlayer, route));
            Assert.GreaterOrEqual(route.Count, 2,
                "a cross-map route has corners to follow");
        }

        private static Vector3 Spawn(MapSpawnPoint spawn)
        {
            var point = GodotSpace.Point(spawn.Origin);
            return new Vector3(point.X, point.Y, point.Z);
        }

        private static NavigationMeshTable LoadTable()
        {
            var asset = Resources.Load<TextAsset>(
                BattleNavigation.ResourcePrefix + BattleWorld.DefaultMapId);
            Assert.IsNotNull(asset, "the ported navmesh ships in Resources");
            return NavigationMeshTable.Parse(asset.bytes);
        }
    }
}
