using System.Text;
using BlitzLegacy.Core.Localization;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Ui;
using BlitzLegacy.Runtime.Assets;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>Shared uGUI construction rules for the ported battle-flow screens.</summary>
    public static class BattleFlowUi
    {
        public const float AuthoredWidth = UiLayout.GodotWindowWidth;
        public const float AuthoredHeight = UiLayout.GodotWindowHeight;
        public static readonly Color White = new Color(0.94f, 0.96f, 0.98f, 1f);
        public static readonly Color Muted = new Color(0.62f, 0.69f, 0.75f, 1f);
        public static readonly Color Yellow = new Color(1f, 0.74f, 0.08f, 1f);
        public static readonly Color Ally = new Color(0.55f, 0.82f, 0.19f, 1f);
        public static readonly Color Enemy = new Color(0.91f, 0.25f, 0.19f, 1f);
        public static readonly Color Panel = new Color(0.035f, 0.06f, 0.075f, 0.9f);
        public static readonly Color PanelLight = new Color(0.08f, 0.12f, 0.145f, 0.94f);

        public static AtlasRegionTable LoadAtlas()
        {
            return AtlasRegionTable.Parse(
                BlitzResources.LoadText(BlitzResources.AtlasRegionsData));
        }

        public static LocalizationTable LoadLanguage()
        {
            var table = LocalizationTable.Parse(
                BlitzResources.LoadText(BlitzResources.LanguageData));
            if (BlitzBootstrap.Current != null && BlitzBootstrap.Current.Settings != null)
            {
                table.Language = BlitzBootstrap.Current.Settings.Language;
            }
            return table;
        }

        public static RectTransform CreateCanvas(
            Transform parent, bool battleScale, bool mobile, int sortingOrder)
        {
            var go = new GameObject("Canvas", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.pixelPerfect = false;
            canvas.sortingOrder = sortingOrder;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = battleScale
                ? new Vector2(UiLayout.BattleReferenceWidth(mobile),
                    UiLayout.BattleReferenceHeight(mobile))
                : new Vector2(UiLayout.ReferenceWidth, UiLayout.ReferenceHeight);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
            scaler.referencePixelsPerUnit = 100f;

            // The menu scale row owns every flow screen that is not laid out
            // against the battle window.
            if (!battleScale)
            {
                MenuUiScale.Register(scaler, scaler.referenceResolution);
            }

            go.AddComponent<GraphicRaycaster>();
            return (RectTransform)go.transform;
        }

        public static RectTransform AuthoredRoot(Transform parent)
        {
            var root = UiFactory.Node("authored_1040x550", parent);
            root.anchorMin = root.anchorMax = new Vector2(0.5f, 0.5f);
            root.pivot = new Vector2(0.5f, 0.5f);
            root.sizeDelta = new Vector2(AuthoredWidth, AuthoredHeight);
            root.anchoredPosition = Vector2.zero;
            return root;
        }

        public static Image Solid(string name, Transform parent, Color color)
        {
            var image = UiFactory.Picture(name, parent, null);
            image.color = color;
            return image;
        }

        public static Image Art(
            string name, Transform parent, AtlasRegionTable atlas, string region,
            float x, float y, float width, float height)
        {
            var image = UiFactory.Picture(name, parent, atlas.Get(region));
            Place(image.rectTransform, x, y, width, height);
            return image;
        }

        public static Text Label(
            string name, Transform parent, Font font, int size, Color color,
            TextAnchor anchor, string text, float x, float y, float width, float height)
        {
            var label = UiFactory.Label(name, parent, font, size, color, anchor, text);
            Place(label.rectTransform, x, y, width, height);
            UiFactory.AddShadow(label, new Color(0f, 0f, 0f, 0.75f));
            return label;
        }

        public static void Place(
            RectTransform rt, float x, float y, float width, float height)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(width, height);
        }

        public static void Center(
            RectTransform rt, float x, float y, float width, float height)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(width, height);
        }

        public static Button Button(
            string name, Transform parent, AtlasRegionTable atlas, string background,
            Font font, int size, string text, float x, float y, float width, float height)
        {
            var sprite = string.IsNullOrEmpty(background) ? null : atlas.Get(background);
            var button = UiFactory.TextButton(name, parent, sprite, font, size, text);
            var image = button.GetComponent<Image>();
            image.color = sprite == null ? new Color(0.035f, 0.06f, 0.075f, 0.94f) : Color.white;
            image.type = sprite != null && sprite.border.sqrMagnitude > 0f
                ? Image.Type.Sliced : Image.Type.Simple;
            Place((RectTransform)button.transform, x, y, width, height);
            return button;
        }

        /// <summary>ui_team_intro: Color("4f5356") for every class but the TD.</summary>
        public static readonly Color TankIconColor =
            new Color(0x4f / 255f, 0x53 / 255f, 0x56 / 255f, 1f);

        /// <summary>ui_team_intro: Color("505484ff") for tank destroyers.</summary>
        public static readonly Color TankDestroyerIconColor =
            new Color(0x50 / 255f, 0x54 / 255f, 0x84 / 255f, 1f);

        /// <summary>
        /// tank_info.get_small_icon(): an exact per-tank SmallTankIcons crop,
        /// sourced from Blitz 4.8 first and Blitz 11.20 for additive vehicles.
        /// </summary>
        public static string SmallTankIconRegion(TankInfo card)
        {
            if (card == null || string.IsNullOrEmpty(card.Name))
            {
                return "flow_tank_ms1_small";
            }

            // Resolve through the model assets so a card borrowing a donor
            // model (model_path naming the donor tank) also borrows its icon.
            var id = TankModelAssets.For(card).DataId.ToLowerInvariant();
            return "flow_tank_" + id + "_small";
        }

        /// <summary>
        /// ui_mat_color_replace.tres, the shader every small tank icon is
        /// drawn through. Returns null when the shader is stripped out of a
        /// build, which simply leaves the icon in its authored colour.
        /// </summary>
        public static Material ColorReplaceMaterial(Color replacement)
        {
            var shader = Shader.Find("Blitz/UiColorReplace");
            if (shader == null)
            {
                return null;
            }

            var material = new Material(shader);
            material.SetColor("_PrevColor", new Color(0f, 0f, 0f, 0.846f));
            material.SetColor("_NewColor", replacement);
            material.SetFloat("_Tolerance", 0.999999977648f);
            return material;
        }

        public static string RomanTier(int index)
        {
            switch (index)
            {
                case 0: return "XI";
                case 1: return "X";
                case 2: return "IX";
                case 3: return "VIII";
                case 4: return "VII";
                case 5: return "VI";
                case 6: return "V";
                case 7: return "IV";
                case 8: return "III";
                case 9: return "II";
                default: return "I";
            }
        }
    }
}
