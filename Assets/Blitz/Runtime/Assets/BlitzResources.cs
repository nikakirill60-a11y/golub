using UnityEngine;

namespace BlitzLegacy.Runtime.Assets
{
    /// <summary>
    /// Every asset the port needs is loaded by name from Resources, which is
    /// what replaces dragging references into the inspector.
    /// </summary>
    public static class BlitzResources
    {
        public const string BoldFontName = "WarHeliosCondCBold";
        public const string RegularFontName = "WarHeliosCondC";

        public const string AtlasRegionsData = "atlas_regions";
        public const string LanguageData = "lang";
        public const string TanksData = "tanks";
        public const string SettingsManifestData = "settings_source_manifest";

        public static Texture2D LoadTexture(string name)
        {
            return Resources.Load<Texture2D>("Textures/" + name);
        }

        public static AudioClip LoadAudio(string name)
        {
            return Resources.Load<AudioClip>("Audio/" + name);
        }

        public static Font LoadFont(string name)
        {
            return Resources.Load<Font>("Fonts/" + name);
        }

        public static string LoadText(string name)
        {
            var asset = Resources.Load<TextAsset>("Data/" + name);
            return asset == null ? null : asset.text;
        }
    }
}
