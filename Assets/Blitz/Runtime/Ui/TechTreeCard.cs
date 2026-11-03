using System;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Audio;
using BlitzLegacy.Runtime.Mods;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// ui_tech_tree_tank.tscn. Every child is anchored to the card centre in
    /// Godot, so the rects below are expressed relative to that centre and
    /// resolve for any root size: the tech tree instantiates the card at
    /// 400x170 with scale 0.65, the modules screen at 347x136 with scale 0.5.
    /// The only child not ported is "elite", whose TankTypesBig art is not in
    /// the atlas yet.
    /// </summary>
    public sealed class TechTreeCard
    {
        private readonly Image _selected;

        private TechTreeCard(RectTransform root, Image selected, TankInfo info)
        {
            Root = root;
            _selected = selected;
            Info = info;
        }

        public RectTransform Root { get; private set; }

        public TankInfo Info { get; private set; }

        public void Select()
        {
            if (_selected != null)
            {
                _selected.gameObject.SetActive(true);
            }
        }

        public void Deselect()
        {
            if (_selected != null)
            {
                _selected.gameObject.SetActive(false);
            }
        }

        public static TechTreeCard Build(
            HangarUiKit kit,
            RectTransform parent,
            float x,
            float y,
            float width,
            float height,
            float scale,
            TankInfo info,
            ResearchStatus status,
            TankSaveData save,
            Action<string, ResearchStatus> pressed)
        {
            var cx = width * 0.5f;
            var cy = height * 0.5f;
            var premium = info.Premium;

            var card = kit.Node("UiTechTreeTank", parent, x, y, width, height);
            card.localScale = new Vector3(scale, scale, 1f);

            kit.Picture(
                "background",
                card,
                HangarUiKit.TreeCardRegion(status, premium),
                (width * 0.497f) - 175.95f,
                (height * 0.493f) - 70.020004f,
                352f,
                140f);

            // icon: 316x200 node scaled 0.62 around pivot (137, 100), drawn
            // with Godot's STRETCH_KEEP_CENTERED.
            var iconRegion = HangarUiKit.TankIconRegion(info.Name);
            var iconImage = kit.Centered(
                "icon",
                card,
                iconRegion,
                cx - 100.44f,
                cy - 58f,
                195.92f,
                124f,
                0.62f);
            if (info.Mod && iconImage != null && kit.Art(iconRegion) == null)
            {
                // A mod's own icon, or a "?" instead of the white quad.
                var modIcon = ModRuntime.Icon(info.Name);
                if (modIcon != null)
                {
                    iconImage.sprite = modIcon;
                    iconImage.preserveAspect = true;
                }
                else
                {
                    iconImage.gameObject.SetActive(false);
                    kit.Label(
                        "icon_unknown", card, 64, TextAnchor.MiddleCenter, "?",
                        kit.Bold, HangarUiKit.TankGlyphTint(info),
                        cx - 60f, cy - 40f, 120f, 80f);
                }
            }
            var levelGlyph = kit.Centered(
                "lvl", card, HangarUiKit.TankLevelRegion(info), cx - 122f, cy - 61f, 76f, 57f);
            levelGlyph.color = HangarUiKit.TankGlyphTint(info);
            var classGlyph = kit.Centered(
                "class", card, HangarUiKit.TankClassRegion(info), cx - 167f, cy - 60f, 52f, 58f);
            classGlyph.color = HangarUiKit.TankGlyphTint(info);

            var shortName = kit.Lang == null
                ? (string.IsNullOrEmpty(info.DisplayNameShort) ? info.DisplayName : info.DisplayNameShort)
                : kit.Lang.GetTankName(info, true);
            kit.Label(
                "name",
                card,
                34,
                TextAnchor.UpperLeft,
                shortName,
                kit.Bold,
                HangarUiKit.TankNameColor(info),
                cx - 159f,
                cy + 22f,
                124f,
                36f);

            var researched = status == ResearchStatus.Researched
                || status == ResearchStatus.InstalledBought;
            var gated = status == ResearchStatus.NotAvailable
                || status == ResearchStatus.Available;

            var expIcon = kit.Centered(
                "exp_icon", card, "hangar_exp", cx + 120f, cy - 65f, 52f, 58f);
            expIcon.gameObject.SetActive(researched);
            var exp = kit.Label(
                "exp",
                card,
                35,
                TextAnchor.UpperRight,
                HangarUiKit.FormatNumberToK(save == null ? 0 : save.Exp),
                kit.Bold,
                UiTheme.Label,
                cx - 165f,
                cy - 52f,
                286.5f,
                75f);
            exp.gameObject.SetActive(researched);

            var cost = kit.Label(
                "cost",
                card,
                34,
                TextAnchor.UpperRight,
                HangarUiKit.FormatNumberToK(
                    gated ? info.CostXp : TankResearchService.PurchasePrice(info)),
                kit.Regular,
                UiTheme.Label,
                cx + 54f,
                cy + 24f,
                64f,
                36f);
            cost.gameObject.SetActive(status != ResearchStatus.InstalledBought);

            var currency = gated
                ? "hangar_exp"
                : (premium ? "hangar_gold" : "hangar_credits");
            kit.Centered("currency_icon", card, currency, cx + 119f, cy + 11f, 52f, 58f);

            var bought = kit.Picture(
                "bought", card, "module_installed", cx + 121f, cy + 12f, 64f, 64f);
            bought.gameObject.SetActive(status == ResearchStatus.InstalledBought);

            var selected = kit.Picture(
                "selected", card, "tree_selector", cx - 174f, cy - 69f, 351f, 139f);
            selected.type = Image.Type.Sliced;
            selected.gameObject.SetActive(false);

            var button = kit.StyleButton(
                "ButtonCustom", card, null, cx - 170f, cy - 62f, 340f, 125f);
            var tank = info.Name;
            kit.Bind(
                button,
                () =>
                {
                    if (pressed != null)
                    {
                        pressed(tank, status);
                    }
                },
                AudioService.SoundSelect);

            return new TechTreeCard(card, selected, info);
        }
    }
}
