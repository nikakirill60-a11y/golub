// The 11.x hangars the ported scene runtime (HalloweenHangarScene) builds:
// where each export lives under Resources and which client map it describes.
// The exports are written by the tools named here; the editor importer
// (BlitzLegacy.EditorTools.HalloweenHangarImport) applies the texture settings
// of every one of them.
using System.Collections.Generic;
using BlitzLegacy.Core.Hangar;

namespace BlitzLegacy.Runtime.Hangar.Halloween
{
    public sealed class PortedHangar
    {
        /// <summary>WoT Blitz 11.21 <c>hangar_halloween_2026</c>.</summary>
        public static readonly PortedHangar Halloween2026 = new PortedHangar(
            HangarKind.Halloween2026, "HalloweenHangar2026", HangarCatalog.Halloween2026Id, "Halloween",
            "Tools/port_halloween_hangar_2026.py");

        /// <summary>
        /// Tanks Blitz "Miku hangar in the void" (<c>hangar_miku_void</c>). It
        /// opens on the vehicle in front of the clock: from the 4.8 client's
        /// opening view the figure of Miku stands between the camera and the vehicle.
        /// </summary>
        public static readonly PortedHangar Miku = new PortedHangar(
            HangarKind.Miku, "MikuHangar", HangarCatalog.MikuId, "Miku", "Tools/port_miku_hangar.py", -45f);

        private static readonly PortedHangar[] Entries = { Halloween2026, Miku };

        private PortedHangar(HangarKind kind, string root, string mapName, string label, string exporter,
            float? openingYaw = null)
        {
            Kind = kind;
            Root = root;
            MapName = mapName;
            Label = label;
            Exporter = exporter;
            OpeningYaw = openingYaw;
        }

        public static IReadOnlyList<PortedHangar> All
        {
            get { return Entries; }
        }

        public HangarKind Kind { get; }

        /// <summary>Resources folder of the export.</summary>
        public string Root { get; }

        /// <summary>The client map the export describes: the "map" of its scene.json.</summary>
        public string MapName { get; }

        /// <summary>Name prefix of the scene root the build makes.</summary>
        public string Label { get; }

        /// <summary>The tool that writes the export, relative to the project.</summary>
        public string Exporter { get; }

        /// <summary>
        /// The yaw of the hangar's opening view relative to the vehicle, in
        /// degrees, when it does not open on the 4.8 client's view.
        /// </summary>
        public float? OpeningYaw { get; }

        /// <summary>The ported hangar shown for <paramref name="kind"/>, or null for a 4.8 hangar.</summary>
        public static PortedHangar For(HangarKind kind)
        {
            foreach (var entry in Entries)
            {
                if (entry.Kind == kind)
                {
                    return entry;
                }
            }

            return null;
        }
    }
}
