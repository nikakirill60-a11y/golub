using BlitzLegacy.Core.Tanks;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Guards the armour plates the hangar porter exports next to the tank
    /// mesh. Godot assembles them by walking the model at load time, so this
    /// table plus MS1_armor.obj is what the port carries instead of that walk.
    /// </summary>
    public sealed class TankArmorTableTests
    {
        private const string Sample =
            "{\"plates\":[{\"armor\":13,\"mount\":\"hull\",\"name\":\"_armor_13\"," +
            "\"screen\":false},{\"armor\":8,\"mount\":\"gun01\"," +
            "\"name\":\"gun_mask_8\",\"screen\":true}]}";

        [Test]
        public void Parse_ReadsTheThicknessMountAndScreenFlag()
        {
            var table = TankArmorTable.Parse(Sample);

            Assert.That(table.Count, Is.EqualTo(2));
            Assert.That(table.ArmorOf("_armor_13"), Is.EqualTo(13));
            Assert.That(
                table.MountOf("_armor_13"), Is.EqualTo(TankArmorTable.HullMount));
            Assert.That(table.IsScreen("_armor_13"), Is.False);
            Assert.That(table.MountOf("gun_mask_8"), Is.EqualTo(TankNodeTable.Gun));
            Assert.That(table.IsScreen("gun_mask_8"), Is.True);
            Assert.That(table.Find("gun_mask_8").Armor, Is.EqualTo(8));
        }

        [Test]
        public void Parse_ReadsTheMeasuredMountOffsets()
        {
            const string json =
                "{\"mountOffsets\":{\"turret_01\":[0.0,-0.46,0.08]}," +
                "\"plates\":[{\"armor\":13,\"mount\":\"turret_01\"," +
                "\"name\":\"armor_turret_01_front_13\",\"screen\":false}]}";

            var table = TankArmorTable.Parse(json);

            Assert.That(table.OffsetOf("turret_01").Y, Is.EqualTo(-0.46f).Within(1e-4f));
            Assert.That(table.OffsetOf("turret_01").Z, Is.EqualTo(0.08f).Within(1e-4f));
            // Battle space mirrors the source across Z, so the correction has
            // to mirror with it or the shell moves to the wrong end.
            Assert.That(
                table.MirroredOffsetOf("turret_01").Z, Is.EqualTo(-0.08f).Within(1e-4f));
            Assert.That(table.OffsetOf("hull").Y, Is.EqualTo(0f));
            Assert.That(table.OffsetOf(null).X, Is.EqualTo(0f));
        }

        [Test]
        public void Parse_SurvivesMissingPlatesAndEmptyInput()
        {
            var table = TankArmorTable.Parse(Sample);

            Assert.That(table.Has("nope"), Is.False);
            Assert.That(table.ArmorOf("nope"), Is.EqualTo(0));
            Assert.That(table.MountOf("nope"), Is.Null);
            Assert.That(table.IsScreen("nope"), Is.False);
            Assert.That(table.Find("nope").Name, Is.Null);
            Assert.That(TankArmorTable.Parse(null).Count, Is.EqualTo(0));
            Assert.That(TankArmorTable.Parse(null).MinArmor, Is.EqualTo(0));
        }

        [Test]
        public void ShippedTable_CarriesTheClientArmourOfEveryMS1Module()
        {
            var asset = Resources.Load<TextAsset>("Data/" + TankArmorTable.Resource);
            Assert.That(asset, Is.Not.Null, "the porter must export ms1_armor.json");

            var table = TankArmorTable.Parse(asset.text);

            // The 4.8.0 collision mesh authors the thickness per face (uv0.y
            // in mm, uv0.x = 0 for a screen), so the port ships one plate per
            // (thickness, screen) pair of every part instead of guessing
            // front/side/rear/roof zones from the face normals. Each plate
            // rides the part that carries it so only the installed modules
            // become colliders. The track run rides the hull as well, as the
            // damageable module body the source calls module_tracks.
            Assert.That(table.Count, Is.EqualTo(32));
            Assert.That(table.OnMount(TankArmorTable.HullMount).Count, Is.EqualTo(6));
            Assert.That(table.OnMount("turret_01").Count, Is.EqualTo(4));
            Assert.That(table.OnMount("turret_02").Count, Is.EqualTo(5));

            // MS-1 hull: the authored 18, 16 and 8 mm plates, a 10 mm screen
            // and 0 mm faces the client authors as unarmoured (a direct hit goes through, splash ignores them).
            Assert.That(table.ArmorOf("armor_hull_plate18_18"), Is.EqualTo(18));
            Assert.That(table.ArmorOf("armor_hull_plate16_16"), Is.EqualTo(16));
            Assert.That(table.ArmorOf("armor_hull_plate8_8"), Is.EqualTo(8));
            Assert.That(table.ArmorOf("armor_hull_plate0_0"), Is.EqualTo(0));
            Assert.That(table.IsScreen("armor_hull_plate10s_10"), Is.True);
            Assert.That(
                table.MountOf("armor_turret_02_plate18_18"), Is.EqualTo("turret_02"));

            // A mantlet face keeps its authored screen flag and rides the gun
            // that carries it.
            Assert.That(
                table.MountOf("gun_09_mask_turret_02_plate18s_18"), Is.EqualTo("gun_09"));
            Assert.That(table.IsScreen("gun_09_mask_turret_02_plate18s_18"), Is.True);
            Assert.That(table.IsScreen("armor_hull_plate18_18"), Is.False);

            // module_tracks is the track module body, not armour: it is a
            // screen that charges its nominal thickness and it stays out of
            // the min/max armour the AI reads.
            Assert.That(table.MountOf("module_tracks_20"), Is.EqualTo("hull"));
            Assert.That(table.IsScreen("module_tracks_20"), Is.True);
            Assert.That(table.ArmorOf("module_tracks_20"), Is.EqualTo(20));

            Assert.That(table.MinArmor, Is.EqualTo(0));
            Assert.That(table.MaxArmor, Is.EqualTo(18));
        }

        /// <summary>
        /// The OBJ importer merges the object records into a single mesh and
        /// splits it into one submesh per material, the same contract the hangar
        /// (three submeshes) and the tank body (two) are read under. So the
        /// porter gives every plate its own material and records the submesh it
        /// landed in: that index is what stays of a plate after import, and the
        /// collider build reads it to tell a 13 mm hull plate from an 8 mm
        /// screen.
        /// </summary>
        [Test]
        public void ShippedModel_KeepsOneSubMeshPerPlate()
        {
            var model = Resources.Load<GameObject>(TankArmorTable.ModelResource);
            Assert.That(model, Is.Not.Null, "the porter must export MS1_armor.obj");

            var meshes = model.GetComponentsInChildren<MeshFilter>(true);
            Assert.That(meshes.Length, Is.EqualTo(1), "the importer merges the records");
            var mesh = meshes[0].sharedMesh;
            Assert.That(mesh, Is.Not.Null);

            var asset = Resources.Load<TextAsset>("Data/" + TankArmorTable.Resource);
            var table = TankArmorTable.Parse(asset.text);
            Assert.That(mesh.subMeshCount, Is.EqualTo(table.Count));

            // Pin the mapping rather than the count: each plate must own the
            // triangles the porter exported for it, in the order the table
            // lists them.
            var triangles = 0;
            for (var i = 0; i < table.Count; i++)
            {
                var plate = table.Plates[i];
                Assert.That(plate.Submesh, Is.EqualTo(i));
                Assert.That(plate.Triangles, Is.GreaterThan(0));
                Assert.That((int)mesh.GetIndexCount(i) / 3,
                    Is.EqualTo(plate.Triangles),
                    "submesh " + i + " must hold " + plate.Name);
                triangles += plate.Triangles;
            }

            Assert.That(triangles, Is.EqualTo(1246));
        }
    }
}
