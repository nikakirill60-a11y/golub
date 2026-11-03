using BlitzLegacy.Core.Tanks;

namespace BlitzLegacy.Core.Mods
{
    /// <summary>Format-neutral on-disk assets; no Unity or source SDK types.</summary>
    public class ModVehicleAssets
    {
        public string Id = string.Empty;
        public string CardPath = string.Empty;
        public string ModelPath = string.Empty;
        public string IconPath = string.Empty;
        public string BodyTexturePath = string.Empty;
        public string TrackTexturePath = string.Empty;
        public string NodesPath = string.Empty;
        public string ArmorPath = string.Empty;
        public TankInfo PortableCard;
        public string DefaultName = string.Empty;
        public string RussianName = string.Empty;
        public bool HasModel { get { return !string.IsNullOrEmpty(ModelPath); } }
        public bool HasCard { get { return PortableCard != null || !string.IsNullOrEmpty(CardPath); } }
    }
}
