using System;
using System.Text;

namespace BlitzLegacy.Core.Tanks
{
    /// <summary>
    /// Resource names emitted by Tools/port_godot_tank_models.py for one card.
    /// Keeping the naming here makes the hangar, battle and tests resolve the
    /// same payload instead of growing another MS-1 special case.
    /// </summary>
    public sealed class TankModelAssets
    {
        public const string DefaultTankName = "ms-1";

        public string TankName { get; private set; }
        public string Id { get; private set; }
        public string DataId { get; private set; }
        public string ModelResource { get; private set; }
        public string PhysicsResource { get; private set; }
        public string ArmorModelResource { get; private set; }
        public string BodyTextureResource { get; private set; }
        public string TrackTextureResource { get; private set; }
        public string NodeDataResource { get; private set; }
        public string ArmorDataResource { get; private set; }

        public static TankModelAssets For(TankInfo card)
        {
            return For(card != null ? card.Name : DefaultTankName);
        }

        public static TankModelAssets For(string tankName)
        {
            var name = string.IsNullOrEmpty(tankName) ? DefaultTankName : tankName;
            var id = ResourceId(name);
            var root = "Models/Tanks/" + id + "/";
            return new TankModelAssets
            {
                TankName = name,
                Id = id,
                DataId = id.ToLowerInvariant(),
                ModelResource = root + id,
                PhysicsResource = root + id + "_phys",
                ArmorModelResource = root + id + "_armor",
                BodyTextureResource = root + "Textures/" + id,
                TrackTextureResource = root + "Textures/" + id + "_tracks",
                NodeDataResource = id.ToLowerInvariant() + "_nodes",
                ArmorDataResource = id.ToLowerInvariant() + "_armor",
            };
        }

        public string AlbedoTextureResource(string albedoId)
        {
            if (string.IsNullOrEmpty(albedoId))
            {
                return BodyTextureResource;
            }

            return "Models/Tanks/" + Id + "/Textures/" + albedoId;
        }

        public static string ResourceId(string tankName)
        {
            var source = string.IsNullOrEmpty(tankName) ? DefaultTankName : tankName;
            var result = new StringBuilder(source.Length);
            for (var i = 0; i < source.Length; i++)
            {
                if (char.IsLetterOrDigit(source[i]))
                {
                    result.Append(char.ToUpperInvariant(source[i]));
                }
            }

            return result.Length > 0 ? result.ToString() : "MS1";
        }
    }
}
