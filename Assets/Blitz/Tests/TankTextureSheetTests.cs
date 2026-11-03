using System.Collections.Generic;
using System.IO;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Guards the pairing between an exported mesh and the sheet it samples.
    /// The meshes come from the client pack while the sheets were once taken
    /// from a Godot build of a different pack, and the two atlases disagree:
    /// the MS-1 and the BT-2 hull then sampled the empty half of their sheet
    /// and rendered as black and white rectangles. Nothing in the importer
    /// notices that, because both halves decode fine on their own, so the
    /// pairing has to be measured through the UVs.
    /// </summary>
    public sealed class TankTextureSheetTests
    {
        private static readonly string[] Branch =
        {
            "ms-1", "bt-2", "bt-7", "a-20", "t-34",
            "t-34-85", "t-43", "t-44", "t-54", "t62a",
        };

        /// <summary>
        /// A hull keeps a few triangles on the seam of a painted island, so a
        /// handful of centroids may legitimately land on a cleared texel. A
        /// mismatched atlas misses by 84% instead, so the gap is unambiguous.
        /// </summary>
        private const float MaxClearedShare = 0.02f;

        /// <summary>
        /// <c>_Cutoff</c> of the shipped track material, as a byte. Everything
        /// below it is clipped away and therefore only reaches the screen
        /// through filtering.
        /// </summary>
        private const byte TrackCutoff = 26;

        /// <summary>
        /// Some client sheets also contain near-white texels with enough alpha
        /// to survive the clip. They are the opaque part of the visible white
        /// teeth, so the exporter must paint over them too.
        /// </summary>
        private const byte TrackWhite = 230;

        [Test]
        public void ShippedBranch_HullUvsLandOnPaintedTexels()
        {
            var database = TanksDatabase.Parse(
                BlitzResources.LoadText(BlitzResources.TanksData));

            foreach (var name in Branch)
            {
                var assets = TankModelAssets.For(database.Get(name));
                var model = Resources.Load<GameObject>(assets.ModelResource);
                Assert.That(model, Is.Not.Null, name + " visible model");

                var body = ReadSheet(assets.BodyTextureResource);
                Assert.That(body, Is.Not.Null, name + " body sheet");

                var hull = 0;
                var cleared = 0;
                Measure(model, body, ref hull, ref cleared);
                Object.DestroyImmediate(body);

                Assert.That(hull, Is.GreaterThan(0), name + " hull triangles");
                Assert.That((float)cleared / hull, Is.LessThanOrEqualTo(MaxClearedShare),
                    name + " hull triangles sampling cleared texels");
            }
        }

        /// <summary>
        /// The clip only decides whether a texel is drawn; the filter and the
        /// mip chain still average the clipped ones into their neighbours. The
        /// client leaves white under the gaps between the track links, which
        /// bled a white fringe over the links - the white teeth reported on
        /// MS-1, BT-2, BT-7, A-20, T-54 and T-62A, while T-34..T-44 happened
        /// to ship black there and looked clean.
        ///
        /// The exporter dilates the paint outwards, so every clipped texel
        /// that touches a drawn one carries the average of those neighbours.
        /// Checking that ring is exact: a sheet that skipped the dilation
        /// keeps its own colour there instead.
        /// </summary>
        [Test]
        public void ShippedBranch_TrackSheetsBleedTheirPaintUnderTheClip()
        {
            var database = TanksDatabase.Parse(
                BlitzResources.LoadText(BlitzResources.TanksData));

            foreach (var name in Branch)
            {
                var assets = TankModelAssets.For(database.Get(name));
                var sheet = ReadSheet(assets.TrackTextureResource);
                Assert.That(sheet, Is.Not.Null, name + " track sheet");

                var width = sheet.width;
                var height = sheet.height;
                var pixels = sheet.GetPixels32();
                Object.DestroyImmediate(sheet);

                var ring = 0;
                var wrong = 0;
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var index = y * width + x;
                        if (pixels[index].a >= TrackCutoff) { continue; }

                        int r = 0, g = 0, b = 0, drawn = 0;
                        for (var side = 0; side < 4; side++)
                        {
                            var nx = x + (side == 0 ? 1 : side == 1 ? -1 : 0);
                            var ny = y + (side == 2 ? 1 : side == 3 ? -1 : 0);
                            nx = (nx + width) % width;
                            ny = (ny + height) % height;
                            var neighbour = pixels[ny * width + nx];
                            if (neighbour.a < TrackCutoff ||
                                (neighbour.r >= TrackWhite &&
                                 neighbour.g >= TrackWhite &&
                                 neighbour.b >= TrackWhite))
                            {
                                continue;
                            }

                            r += neighbour.r;
                            g += neighbour.g;
                            b += neighbour.b;
                            drawn++;
                        }

                        if (drawn == 0) { continue; }

                        ring++;
                        var pixel = pixels[index];
                        if (Mathf.Abs(pixel.r - r / drawn) > 1 ||
                            Mathf.Abs(pixel.g - g / drawn) > 1 ||
                            Mathf.Abs(pixel.b - b / drawn) > 1)
                        {
                            wrong++;
                        }
                    }
                }

                Assert.That(ring, Is.GreaterThan(0), name + " clipped track texels");
                Assert.That(wrong, Is.Zero,
                    name + " track texels not bled from their drawn neighbours");
            }
        }

        /// <summary>
        /// A-20, BT-2 and BT-7 carry near-white texels with enough alpha to
        /// survive the clip, right at the edge of the track islands. The alpha
        /// test cannot remove them and mip filtering carries them into the
        /// links, which is the half of the white teeth the first bleed pass
        /// left behind. The exporter now paints over the whole near-white
        /// region, so no shipped sheet may contain white paint at all.
        /// </summary>
        [Test]
        public void ShippedBranch_TrackSheetsContainNoWhitePaint()
        {
            var database = TanksDatabase.Parse(
                BlitzResources.LoadText(BlitzResources.TanksData));

            foreach (var name in Branch)
            {
                var assets = TankModelAssets.For(database.Get(name));
                var sheet = ReadSheet(assets.TrackTextureResource);
                Assert.That(sheet, Is.Not.Null, name + " track sheet");

                var pixels = sheet.GetPixels32();
                Object.DestroyImmediate(sheet);

                var white = 0;
                for (var index = 0; index < pixels.Length; index++)
                {
                    var pixel = pixels[index];
                    if (pixel.r >= TrackWhite && pixel.g >= TrackWhite &&
                        pixel.b >= TrackWhite)
                    {
                        white++;
                    }
                }

                Assert.That(white, Is.Zero,
                    name + " track sheet still contains white paint");
            }
        }

        /// <summary>
        /// Walks the triangle centroids of every hull submesh. The track sheet
        /// is deliberately skipped: its cleared texels are the gaps between the
        /// links, which the alpha clip removes instead of painting.
        /// </summary>
        private static void Measure(
            GameObject model, Texture2D body, ref int hull, ref int cleared)
        {
            var renderers = model.GetComponentsInChildren<MeshRenderer>(true);
            for (var r = 0; r < renderers.Length; r++)
            {
                var filter = renderers[r].GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) { continue; }

                // The OBJ importer drops the .mtl bindings and gives every
                // record the same default material, so the split between the
                // two sheets is read off the object names, exactly like the
                // runtime does when it assigns the track materials.
                var name = renderers[r].gameObject.name;
                if (name == "trackL" || name == "trackR" ||
                    name == "tracks_crash")
                {
                    // The broken tracks share the track sheet too.
                    continue;
                }

                var mesh = filter.sharedMesh;
                var uv = mesh.uv;
                if (uv.Length == 0) { continue; }

                for (var s = 0; s < mesh.subMeshCount; s++)
                {
                    var triangles = mesh.GetTriangles(s);
                    for (var t = 0; t + 2 < triangles.Length; t += 3)
                    {
                        var point =
                            (uv[triangles[t]] + uv[triangles[t + 1]] + uv[triangles[t + 2]])
                            / 3f;
                        hull++;
                        if (Sample(body, point).a <= 0f) { cleared++; }
                    }
                }
            }
        }

        private static Color Sample(Texture2D sheet, Vector2 uv)
        {
            var x = Mathf.Clamp(
                Mathf.FloorToInt(Mathf.Repeat(uv.x, 1f) * sheet.width), 0, sheet.width - 1);
            var y = Mathf.Clamp(
                Mathf.FloorToInt(Mathf.Repeat(uv.y, 1f) * sheet.height), 0, sheet.height - 1);
            return sheet.GetPixel(x, y);
        }

        /// <summary>
        /// The shipped sheets are imported without CPU access, so the test
        /// decodes the PNG on the side rather than making the player pay for a
        /// readable copy of every atlas.
        /// </summary>
        private static Texture2D ReadSheet(string resource)
        {
            var loaded = Resources.Load<Texture2D>(resource);
            if (loaded == null) { return null; }

            var path = AssetDatabase.GetAssetPath(loaded);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) { return null; }

            var sheet = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            return sheet.LoadImage(File.ReadAllBytes(path), false) ? sheet : null;
        }
    }
}
