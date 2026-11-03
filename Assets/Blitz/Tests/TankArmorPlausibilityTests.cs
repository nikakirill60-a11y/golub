using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Per-tank guards for the shipped armour. The 4.8.0 port once bucketed
    /// every collision triangle by its normal into front/side/rear/roof and
    /// gave it the XML zone thickness, so a down-and-forward lower glacis
    /// became a 20 mm "roof" on a 180 mm AMX 50 Foch (155) nose. These tests
    /// pin the authored naming of every plate and scan each tank the way the
    /// armour viewer sees it, so a thin island on a heavy face either is a
    /// reviewed, authored part of the client data or fails the run.
    /// </summary>
    public sealed class TankArmorPlausibilityTests
    {
        private const string ReviewedIslands = "Blitz/Tests/Data/armor_thin_islands_reviewed.json";

        private static readonly Regex ZoneName =
            new Regex(@"^(armor_hull|armor_turret_\d+)_(front|side|rear|roof)_\d+$");

        private static readonly Regex WholeMantlet = new Regex(@"^gun_\d+_mask_\d+$");

        private static readonly Regex PlateName = new Regex(
            @"^(armor_hull|armor_turret_(?<turret>\d+)|gun_\d+_mask_turret_(?<gunturret>\d+))_plate(?<label>\w+?)_(?<mm>\d+)$");

        // 4.8.0 plates are named after their authored thickness and an `s` for
        // a screen face; 11.20 plates are named after the XML plate index.
        private static readonly Regex Plate480Label = new Regex(@"^(?<mm>\d+)(?<screen>s?)$");

        private GameObject _root;
        private static TanksDatabase _database;

        private static JsonNode Manifest()
        {
            return JsonNode.Parse(
                Resources.Load<TextAsset>("Data/tank_models_source_manifest").text)["tanks"];
        }

        public static IEnumerable<string> Tanks()
        {
            return Manifest().Fields.Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        }

        /// <summary>Tanks the 4.8.0 porter exported: no 11.20 source pack.</summary>
        private static bool From480(string id)
        {
            return string.IsNullOrEmpty(Manifest()[id]["original_pack"].AsString());
        }

        private static TanksDatabase Database
        {
            get
            {
                if (_database == null)
                {
                    _database = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
                }

                return _database;
            }
        }

        [SetUp]
        public void Prepare()
        {
            _root = new GameObject("armor plausibility");
        }

        [TearDown]
        public void Cleanup()
        {
            Object.DestroyImmediate(_root);
        }

        [TestCaseSource(nameof(Tanks))]
        public void ArmorTable_CarriesAuthoredPlates(string id)
        {
            var card = Database.Get(id);
            Assert.IsNotNull(card, id);
            var assets = TankModelAssets.For(card);
            var text = Resources.Load<TextAsset>("Data/" + assets.ArmorDataResource);
            Assert.IsNotNull(text, assets.ArmorDataResource);
            var table = TankArmorTable.Parse(text.text);
            Assert.Greater(table.Count, 0, id);

            var from480 = From480(id);
            var mainOnMount = new Dictionary<string, int>();
            foreach (var plate in table.Plates)
            {
                var where = id + "/" + plate.Name;
                Assert.IsFalse(ZoneName.IsMatch(plate.Name),
                    where + ": a normal-zone guess, not an authored collision plate");
                var sourceAuthorizedGunMask = id == "gb86_mark_i_male"
                    && plate.Name == "gun_01_mask_10";
                Assert.IsFalse(WholeMantlet.IsMatch(plate.Name) && !sourceAuthorizedGunMask,
                    where + ": one thickness for a whole mantlet, not its authored faces");
                if (sourceAuthorizedGunMask)
                {
                    Assert.AreEqual(10, plate.Armor, where + ": source XML gun armour");
                    Assert.IsTrue(plate.Screen, where + ": authored gun mask is a screen plate");
                    Assert.AreEqual(TankModuleIds.Gun, plate.Module, where + ": mounted gun module");
                }
                Assert.That(plate.Armor, Is.InRange(0, 450), where);
                Assert.Greater(plate.Triangles, 0, where);
                if (plate.IsModule) { continue; }

                var match = PlateName.Match(plate.Name);
                Assert.IsTrue(match.Success, where + ": unexpected plate name");
                Assert.AreEqual(plate.Armor, int.Parse(match.Groups["mm"].Value),
                    where + ": the name and the table disagree on the thickness");
                if (from480)
                {
                    var label = Plate480Label.Match(match.Groups["label"].Value);
                    Assert.IsTrue(label.Success, where + ": a 4.8.0 panel is named after its thickness");
                    Assert.AreEqual(plate.Armor.ToString(), label.Groups["mm"].Value, where);
                    Assert.AreEqual(label.Groups["screen"].Value.Length > 0, plate.Screen,
                        where + ": the authored screen flag must survive the export");
                }

                if (!plate.Screen)
                {
                    int count;
                    mainOnMount.TryGetValue(plate.Mount, out count);
                    mainOnMount[plate.Mount] = count + 1;
                }
            }

            Assert.IsTrue(mainOnMount.ContainsKey(TankArmorTable.HullMount),
                id + ": the hull carries no main armour");

            // Every plate has to stay a collider PhysX can cook: a plate made
            // only of degenerate slivers fails to cook and leaves a hole.
            var prefab = Resources.Load<GameObject>(assets.ArmorModelResource);
            Assert.IsNotNull(prefab, assets.ArmorModelResource);
            var filter = prefab.GetComponentInChildren<MeshFilter>(true);
            Assert.IsNotNull(filter, assets.ArmorModelResource + ": no mesh");
            var mesh = filter.sharedMesh;
            Assert.IsNotNull(mesh, assets.ArmorModelResource + ": no mesh");
            Assert.AreEqual(table.Count, mesh.subMeshCount,
                id + ": one submesh per armour plate");
            var vertices = mesh.vertices;
            foreach (var plate in table.Plates)
            {
                var where = id + "/" + plate.Name;
                Assert.That(plate.Submesh, Is.InRange(0, mesh.subMeshCount - 1), where);
                var triangles = mesh.GetTriangles(plate.Submesh);
                // The OBJ importer drops a triangle whose rounded corners
                // repeat a position, so the model may hold fewer triangles
                // than the porter wrote, never more and never none.
                Assert.That(triangles.Length / 3, Is.InRange(1, plate.Triangles),
                    where + ": the table and the model disagree on the triangle count");
                var area = 0f;
                for (var i = 0; i + 2 < triangles.Length; i += 3)
                {
                    var a = vertices[triangles[i]];
                    var b = vertices[triangles[i + 1]];
                    var c = vertices[triangles[i + 2]];
                    area += Vector3.Cross(b - a, c - a).magnitude * 0.5f;
                }

                Assert.Greater(area, 1e-6f, where + ": no area to hit");
            }
        }

        [TestCaseSource(nameof(Tanks))]
        public void Armor_HasNoUnreviewedThinIslands(string id)
        {
            var card = Database.Get(id);
            Assert.IsNotNull(card, id);
            var reviewed = Reviewed();
            List<Island> allowed;
            reviewed.TryGetValue(id, out allowed);
            allowed = allowed ?? new List<Island>();

            var found = new List<Island>();
            foreach (var turret in Configurations(card))
            {
                var go = new GameObject(id + " turret " + turret, typeof(Rigidbody));
                go.transform.SetParent(_root.transform, false);
                var tank = go.AddComponent<BattleTank>();
                tank.Initialize(card, turretIndex: turret);
                Physics.SyncTransforms();
                found.AddRange(ArmorIslandScan.Scan(tank, "turret" + turret));
                Object.DestroyImmediate(go);
            }

            var problems = new StringBuilder();
            foreach (var island in found)
            {
                if (!allowed.Any(a => a.Covers(island)))
                {
                    problems.AppendLine("unreviewed " + island);
                }
            }

            foreach (var entry in allowed)
            {
                if (!found.Any(entry.Covers))
                {
                    problems.AppendLine("stale review entry " + entry);
                }
            }

            if (problems.Length > 0)
            {
                Debug.Log("[ArmorIslands] " + id + " " + string.Join(" ", found.Select(f => f.ToJson())));
            }

            Assert.IsEmpty(problems.ToString(), id);
        }

        /// <summary>
        /// The scan has to see the bug it guards against: a 20 mm triangle
        /// facing forward on a 180 mm nose is an island, angled thin plates and
        /// a mantlet over a 0 mm opening are not.
        /// </summary>
        [Test]
        public void Scan_FlagsAThinPlateOnAHeavyFaceOnly()
        {
            var plates = new List<ArmorIslandScan.Sample>
            {
                Box("front_180", new Vector3(0f, 1f, 2f), new Vector3(3f, 1f, 0.04f), 180, false),
                Box("glacis_20", new Vector3(0f, 0.35f, 2f), new Vector3(1f, 0.3f, 0.04f), 20, false),
                Box("mantlet_150s", new Vector3(0f, 1.8f, 2.2f), new Vector3(0.6f, 0.4f, 0.04f), 150, true),
                Box("opening_0", new Vector3(0f, 1.8f, 2f), new Vector3(0.5f, 0.3f, 0.04f), 0, false),
            };
            Physics.SyncTransforms();
            var islands = ArmorIslandScan.Scan(plates, _root.transform, "synthetic");
            Assert.AreEqual(1, islands.Count, string.Join(" ", islands.Select(i => i.ToString())));
            Assert.AreEqual("front", islands[0].Direction);
            CollectionAssert.AreEqual(new[] { "glacis_20" }, islands[0].Plates);
            Assert.AreEqual(0.3f, islands[0].Area, 0.05f);
        }

        private ArmorIslandScan.Sample Box(string name, Vector3 centre, Vector3 size, int armor, bool screen)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_root.transform, false);
            go.transform.localPosition = centre;
            var box = go.AddComponent<BoxCollider>();
            box.size = size;
            return new ArmorIslandScan.Sample
            {
                Collider = box, Name = name, Armor = armor, Screen = screen, Mount = "hull",
            };
        }

        private static IEnumerable<int> Configurations(TankInfo card)
        {
            var count = card.Turrets.Count;
            yield return 0;
            if (count > 1) { yield return count - 1; }
        }

        private static Dictionary<string, List<Island>> _reviewed;

        private static Dictionary<string, List<Island>> Reviewed()
        {
            if (_reviewed != null) { return _reviewed; }

            var path = Path.Combine(Application.dataPath, ReviewedIslands);
            Assert.IsTrue(File.Exists(path), path);
            var root = JsonNode.Parse(File.ReadAllText(path))["tanks"];
            _reviewed = new Dictionary<string, List<Island>>();
            foreach (var tank in root.Fields)
            {
                var list = new List<Island>();
                foreach (var entry in tank.Value.Items)
                {
                    list.Add(new Island
                    {
                        Configuration = entry["config"].AsString(),
                        Direction = entry["dir"].AsString(),
                        Plates = entry["plates"].Items.Select(p => p.AsString()).ToArray(),
                        Area = entry["area"].AsFloat(),
                    });
                }

                _reviewed[tank.Key] = list;
            }

            return _reviewed;
        }

        /// <summary>A connected patch of thin effective armour on one face.</summary>
        public sealed class Island
        {
            public string Configuration;
            public string Direction;
            public string[] Plates;
            public float Area;
            public int MinEffective;
            public int Reference;

            /// <summary>
            /// A review entry covers an island on the same face whose plates it
            /// names and whose area did not grow; the grid is fixed, so only a
            /// changed export moves the numbers.
            /// </summary>
            public bool Covers(Island other)
            {
                return Configuration == other.Configuration && Direction == other.Direction
                    && other.Plates.All(p => Plates.Contains(p))
                    && other.Area <= Area * 1.1f + 0.011f && Area <= other.Area * 1.1f + 0.011f;
            }

            public string ToJson()
            {
                return "{\"config\":\"" + Configuration + "\",\"dir\":\"" + Direction + "\",\"plates\":["
                    + string.Join(",", Plates.Select(p => "\"" + p + "\"")) + "],\"area\":"
                    + Area.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)
                    + ",\"min\":" + MinEffective + ",\"ref\":" + Reference + "}";
            }

            public override string ToString()
            {
                return ToJson();
            }
        }
    }

    /// <summary>
    /// Casts a fixed grid of rays at the front and both sides of a tank, the
    /// way the armour viewer picks plates: screens (and the track module) in
    /// front of a cell add their thickness, the first main plate ends the stack.
    /// A cell is thin when that sum is below 35 % of the 90th percentile
    /// nominal its own part (hull, turret, gun) shows on that face; connected
    /// thin cells of one part form an island.
    /// </summary>
    public static class ArmorIslandScan
    {
        public const float Step = 0.05f;
        public const float ThinFraction = 0.35f;
        public const float MinArea = 0.02f;
        public const int MinReference = 60;
        public const int MinCells = 20;

        public struct Sample
        {
            public Collider Collider;
            public string Name;
            public int Armor;
            public bool Screen;
            public string Mount;

            /// <summary>The track module charges its nominal thickness at any angle.</summary>
            public bool Nominal;
        }

        public static List<TankArmorPlausibilityTests.Island> Scan(BattleTank tank, string configuration)
        {
            var samples = tank.Plates
                .Where(p => p != null && p.Shape != null)
                .Select(p => new Sample
                {
                    Collider = p.Shape, Name = p.PlateName, Armor = p.Armor, Screen = p.Screen, Mount = p.Mount,
                    Nominal = p.UseNominalArmor,
                })
                .ToList();
            return Scan(samples, tank.transform, configuration);
        }

        public static List<TankArmorPlausibilityTests.Island> Scan(
            IList<Sample> samples, Transform frame, string configuration)
        {
            var result = new List<TankArmorPlausibilityTests.Island>();
            if (samples.Count == 0) { return result; }

            var byCollider = new Dictionary<Collider, Sample>();
            var bounds = samples[0].Collider.bounds;
            foreach (var sample in samples)
            {
                byCollider[sample.Collider] = sample;
                bounds.Encapsulate(sample.Collider.bounds);
            }

            var faces = new[]
            {
                new KeyValuePair<string, Vector3>("front", frame.forward),
                new KeyValuePair<string, Vector3>("left", -frame.right),
                new KeyValuePair<string, Vector3>("right", frame.right),
            };
            foreach (var face in faces)
            {
                ScanFace(face.Key, face.Value, frame, bounds, byCollider, configuration, result);
            }

            return result;
        }

        private static float Extent(Bounds bounds, Vector3 axis)
        {
            var e = bounds.extents;
            return Mathf.Abs(axis.x) * e.x + Mathf.Abs(axis.y) * e.y + Mathf.Abs(axis.z) * e.z;
        }

        private static void ScanFace(
            string name, Vector3 outward, Transform frame, Bounds bounds,
            Dictionary<Collider, Sample> plates, string configuration,
            List<TankArmorPlausibilityTests.Island> result)
        {
            var direction = -outward;
            var up = frame.up;
            // Preserve the reviewed grid's physical sample points after the
            // tank handedness fix: reflecting X also reverses the horizontal
            // grid axis. The review manifest swaps left/right labels only;
            // plate identities, areas and acceptance thresholds stay intact.
            var across = Vector3.Cross(direction, up).normalized;
            var radius = bounds.extents.magnitude;
            var halfAcross = Extent(bounds, across);
            var halfUp = Extent(bounds, up);
            var columns = Mathf.CeilToInt(2f * halfAcross / Step);
            var rows = Mathf.CeilToInt(2f * halfUp / Step);
            var effective = new float[rows, columns];
            var owner = new string[rows, columns];
            var mountOf = new string[rows, columns];
            var values = new Dictionary<string, List<float>>();
            for (var y = 0; y < rows; y++)
            {
                for (var x = 0; x < columns; x++)
                {
                    effective[y, x] = float.NaN;
                    var origin = bounds.center - direction * (radius + 1f)
                        + across * (-halfAcross + (x + 0.5f) * Step) + up * (-halfUp + (y + 0.5f) * Step);
                    var hits = Physics.RaycastAll(origin, direction, 2f * radius + 2f, ~0, QueryTriggerInteraction.Ignore);
                    if (hits.Length == 0) { continue; }

                    Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                    var sum = 0f;
                    for (var i = 0; i < hits.Length; i++)
                    {
                        Sample plate;
                        if (!plates.TryGetValue(hits[i].collider, out plate)) { continue; }

                        var cos = Mathf.Max(Mathf.Abs(Vector3.Dot(hits[i].normal, direction)), 0.05f);
                        sum += plate.Nominal ? plate.Armor : plate.Armor / cos;
                        if (plate.Screen) { continue; }

                        effective[y, x] = sum;
                        owner[y, x] = plate.Name;
                        var mount = plate.Mount ?? string.Empty;
                        mountOf[y, x] = mount;
                        List<float> list;
                        if (!values.TryGetValue(mount, out list)) { values[mount] = list = new List<float>(); }
                        list.Add(plate.Armor);
                        break;
                    }
                }
            }

            // Each part is judged against its own face: a 30 mm hull side
            // under a 90 mm turret is the design, a 20 mm patch in a 180 mm
            // nose is not.
            var references = new Dictionary<string, float>();
            foreach (var pair in values)
            {
                if (pair.Value.Count < MinCells) { continue; }

                pair.Value.Sort();
                var at = Mathf.Clamp(Mathf.CeilToInt(pair.Value.Count * 0.9f) - 1, 0, pair.Value.Count - 1);
                if (pair.Value[at] >= MinReference) { references[pair.Key] = pair.Value[at]; }
            }

            Func<int, int, bool> thin = (cx, cy) =>
            {
                float reference;
                return mountOf[cy, cx] != null && references.TryGetValue(mountOf[cy, cx], out reference)
                    && effective[cy, cx] < ThinFraction * reference;
            };
            var seen = new bool[rows, columns];
            for (var y = 0; y < rows; y++)
            {
                for (var x = 0; x < columns; x++)
                {
                    if (seen[y, x] || !thin(x, y)) { continue; }

                    var stack = new Stack<Vector2Int>();
                    stack.Push(new Vector2Int(x, y));
                    seen[y, x] = true;
                    var count = 0;
                    var min = float.MaxValue;
                    var names = new SortedSet<string>(StringComparer.Ordinal);
                    while (stack.Count > 0)
                    {
                        var c = stack.Pop();
                        count++;
                        min = Mathf.Min(min, effective[c.y, c.x]);
                        names.Add(owner[c.y, c.x]);
                        foreach (var n in new[] { new Vector2Int(c.x + 1, c.y), new Vector2Int(c.x - 1, c.y),
                                     new Vector2Int(c.x, c.y + 1), new Vector2Int(c.x, c.y - 1) })
                        {
                            if (n.x < 0 || n.y < 0 || n.x >= columns || n.y >= rows || seen[n.y, n.x]) { continue; }
                            if (mountOf[n.y, n.x] != mountOf[y, x] || !thin(n.x, n.y)) { continue; }

                            seen[n.y, n.x] = true;
                            stack.Push(n);
                        }
                    }

                    var area = count * Step * Step;
                    if (area < MinArea) { continue; }

                    result.Add(new TankArmorPlausibilityTests.Island
                    {
                        Configuration = configuration,
                        Direction = name,
                        Plates = names.ToArray(),
                        Area = Mathf.Round(area * 1000f) / 1000f,
                        MinEffective = Mathf.RoundToInt(min),
                        Reference = Mathf.RoundToInt(references[mountOf[y, x]]),
                    });
                }
            }
        }
    }
}
