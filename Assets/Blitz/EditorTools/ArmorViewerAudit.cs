using System.Collections.Generic;
using System.Text;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using UnityEditor;
using UnityEngine;

namespace BlitzLegacy.EditorTools
{
    /// <summary>
    /// Walks every card the way <c>HangarArmorViewer.BuildPlates</c> does and
    /// reports the vehicles that would end up with no plate at all, which is
    /// what the hangar reports as ARMOR_VIEWER_UNAVAILABLE.
    /// </summary>
    public static class ArmorViewerAudit
    {
        public static void Run()
        {
            var cards = TanksDatabase.Parse(
                BlitzResources.LoadText(BlitzResources.TanksData));
            var report = new StringBuilder();
            var broken = new List<string>();
            for (var i = 0; i < cards.Tanks.Count; i++)
            {
                var card = cards.Tanks[i];
                var assets = TankModelAssets.For(card);
                var table = Resources.Load<TextAsset>("Data/" + assets.ArmorDataResource);
                var nodesAsset = Resources.Load<TextAsset>("Data/" + assets.NodeDataResource);
                var prefab = Resources.Load<GameObject>(assets.ArmorModelResource);
                var filter = prefab != null
                    ? prefab.GetComponentInChildren<MeshFilter>(true) : null;
                var mesh = filter != null ? filter.sharedMesh : null;
                if (table == null || nodesAsset == null || mesh == null)
                {
                    broken.Add(card.Name + ": assets table=" + (table != null)
                        + " nodes=" + (nodesAsset != null) + " mesh=" + (mesh != null));
                    continue;
                }

                var filters = prefab.GetComponentsInChildren<MeshFilter>(true);
                var parsed = TankArmorTable.Parse(table.text);
                var nodes = TankNodeTable.Parse(nodesAsset.text);
                string turretMesh;
                string gunMesh;
                TankLoadoutMeshes.Resolve(card, null, out turretMesh, out gunMesh);
                var turret = nodes.ResolveTurret(turretMesh);
                var gun = nodes.ResolveGun(turret, gunMesh);

                var mounted = 0;
                var drawn = 0;
                for (var p = 0; p < parsed.Count; p++)
                {
                    var plate = parsed.Plates[p];
                    if (!string.IsNullOrEmpty(plate.RequiredTurret) && plate.RequiredTurret != turret) { continue; }
                    if (plate.Mount != TankArmorTable.HullMount
                        && plate.Mount != turret && plate.Mount != gun) { continue; }
                    mounted++;
                    if (plate.Submesh >= 0 && plate.Submesh < mesh.subMeshCount) { drawn++; }
                }

                if (drawn == 0)
                {
                    broken.Add(card.Name + ": mounted=" + mounted + " drawn=0 submeshes="
                        + mesh.subMeshCount + " meshes=" + filters.Length
                        + " turret=" + turret + " gun=" + gun);
                }

                report.Append(card.Name).Append(' ').Append(drawn).Append('/')
                    .Append(parsed.Count).Append('\n');
            }

            Debug.Log("[Blitz] armor audit: cards=" + cards.Tanks.Count
                + " broken=" + broken.Count);
            for (var i = 0; i < broken.Count; i++)
            {
                Debug.Log("[Blitz] armor broken " + broken[i]);
            }

            EditorApplication.Exit(0);
        }
    }
}
