using BlitzLegacy.Core.Tanks;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// Shows the installed turret and gun of a card and hides the rest.
    ///
    /// The Blitz client ships one mesh per researchable module inside the same
    /// vehicle scene (<c>turret_01</c>, <c>gun_05</c> ...) and mounts the one
    /// the player installed. <c>Tools/port_blitz_branch_models.py</c> keeps that
    /// layout: the exported OBJ carries every slot as its own object, so the
    /// hangar and the battle only have to switch which ones are active.
    /// </summary>
    public static class TankModuleView
    {
        /// <summary>
        /// Deactivates every turret and gun object of the import except the two
        /// the loadout installs. Returns how many objects it hid.
        /// </summary>
        public static int ShowInstalled(
            GameObject model, TankNodeTable nodes, string turretNode, string gunNode)
        {
            if (model == null || nodes == null)
            {
                return 0;
            }

            var hidden = 0;
            var filters = model.GetComponentsInChildren<MeshFilter>(true);
            for (var i = 0; i < filters.Length; i++)
            {
                var name = TrimCloneSuffix(filters[i].gameObject.name);
                // A gun's moving parts (`gun_01_rotor`, `gun_01_anim_*`) and the
                // extra-sheet objects of a module (`gun_04_sheet1`) follow
                // their own slot.
                name = nodes.PartOwner(name) ?? name;

                if (!nodes.IsModuleNode(name))
                {
                    continue;
                }

                var installed = name == turretNode || name == gunNode;
                if (filters[i].gameObject.activeSelf != installed)
                {
                    filters[i].gameObject.SetActive(installed);
                }

                if (!installed)
                {
                    hidden++;
                }
            }

            return hidden;
        }

        /// <summary>Unity appends the suffix to every instantiated object.</summary>
        public static string TrimCloneSuffix(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var index = value.IndexOf("(Clone)", System.StringComparison.Ordinal);
            return index >= 0 ? value.Substring(0, index).Trim() : value;
        }
    }
}
