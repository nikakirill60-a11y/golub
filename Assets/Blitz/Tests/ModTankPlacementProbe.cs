using System.Text;
using BlitzLegacy.Runtime.Mods;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    public sealed class ModTankPlacementProbe
    {
        [Test]
        public void InstalledMods_BuildAHullSizedModel()
        {
            ModRuntime.Forget();
            var report = new StringBuilder();
            var bad = 0;
            var mods = ModRuntime.Library.Mods;
            for (var m = 0; m < mods.Count; m++)
            {
                for (var t = 0; t < mods[m].Tanks.Count; t++)
                {
                    var tank = mods[m].Tanks[t];
                    var root = ModTankModel.Visible(tank.Id);
                    if (root == null)
                    {
                        report.AppendLine(tank.Id + ": NO MODEL has=" + tank.HasModel + " path=" + tank.ModelPath);
                        bad++;
                        continue;
                    }

                    var filters = root.GetComponentsInChildren<MeshFilter>(true);
                    var bounds = new Bounds();
                    var first = true;
                    for (var f = 0; f < filters.Length; f++)
                    {
                        var box = filters[f].sharedMesh.bounds;
                        if (first) { bounds = box; first = false; } else { bounds.Encapsulate(box); }
                    }

                    var n = filters.Length > 0 ? filters[0].sharedMesh.normals : new Vector3[0];
                    report.AppendLine(tank.Id + ": parts=" + filters.Length + " size=" + bounds.size.ToString("F2") +
                        " center=" + bounds.center.ToString("F2") + " n0=" + (n.Length > 0 ? n[0].magnitude.ToString("F2") : "-"));
                    if (bounds.size.x < 1.5f || bounds.size.x > 8f || bounds.size.z < 3f || bounds.size.z > 14f) { bad++; }
                }
            }

            Assert.That(bad, Is.EqualTo(0), report.ToString());
        }
    }
}
