using System;
using BlitzLegacy.Core.Localization;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Ui;
using BlitzLegacy.Runtime.Assets;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// Port of account.tscn: one row in the account manager list.
    ///
    /// The Godot root is a ButtonFlat whose pressed signal is not connected to
    /// anything, so here it is only the background. The hidden "delete" button
    /// is skipped as well, because its handler in account.gd is an empty pass.
    /// </summary>
    public static class AccountCard
    {
        public const float Height = 80f;

        public static RectTransform Build(
            RectTransform parent,
            AccountData account,
            bool selected,
            LocalizationTable lang,
            AtlasRegionTable atlas,
            Font bold,
            Action<Button, Action> bind,
            Action onSelect)
        {
            var card = UiFactory.Node("account", parent);
            card.sizeDelta = new Vector2(360f, Height);

            var layout = card.gameObject.AddComponent<LayoutElement>();
            layout.minHeight = Height;
            layout.preferredHeight = Height;

            var background = card.gameObject.AddComponent<Image>();
            background.sprite = atlas.Get("button_flat");
            background.raycastTarget = false;

            var battles = UiFactory.Picture("battles", card, atlas.Get("icon_battles"));
            GodotRect.CenterTop(battles.rectTransform, -172f, 42f, -148f, 66f);

            var wins = UiFactory.Picture("battles2", card, atlas.Get("icon_wins"));
            GodotRect.CenterTop(wins.rectTransform, -50f, 40f, -26f, 64f);

            var clock = UiFactory.Picture("time", card, atlas.Get("icon_time"));
            GodotRect.CenterTop(clock.rectTransform, 157f, 6f, 175f, 24f);

            var name = UiFactory.Label(
                "name", card, bold, UiTheme.BoldLabelFontSize, UiTheme.BoldLabel,
                TextAnchor.MiddleLeft, account.Name);
            UiFactory.AddShadow(name, UiTheme.BoldLabelShadow);
            GodotRect.Centered(name.rectTransform, -172f, -37f, 67f, -1f);

            var timePlayed = UiFactory.Label(
                "time_played", card, bold, UiTheme.BoldLabelFontSize, UiTheme.BoldLabel,
                TextAnchor.MiddleRight, AccountCardText.TimePlayed(account.TimePlayed));
            UiFactory.AddShadow(timePlayed, UiTheme.BoldLabelShadow);
            GodotRect.Apply(
                timePlayed.rectTransform, 0.5f, 0.5f, 0.5f, 0.5f,
                71f, -34f, 191f, -9f, GodotRect.TopLeftPivot);
            timePlayed.rectTransform.localScale = new Vector3(0.69f, 0.69f, 1f);

            // LabelSettings_7merf: bold 18, no colour override, so plain white.
            var winRate = UiFactory.Label(
                "wr", card, bold, UiTheme.StatFontSize, UiTheme.Label,
                TextAnchor.MiddleLeft, AccountCardText.WinRate(account.WinRatePercent));
            GodotRect.Centered(winRate.rectTransform, -17f, -1f, 32f, 35f);

            var battlesAmount = UiFactory.Label(
                "battles_amount", card, bold, UiTheme.StatFontSize, UiTheme.Label,
                TextAnchor.MiddleLeft, AccountCardText.Battles(account.StatBattles));
            GodotRect.Centered(battlesAmount.rectTransform, -143f, -1f, -94f, 35f);

            var select = UiFactory.TextButton(
                "select", card, atlas.Get("button_green"), bold,
                UiTheme.ButtonFontSize, lang.Get("SELECT"));
            GodotRect.Apply(
                (RectTransform)select.transform, 1f, 1f, 1f, 1f, -107f, -37f, -8f, -8f);

            if (selected)
            {
                // account.gd hides the button on the account already in use.
                select.gameObject.SetActive(false);
            }
            else
            {
                bind(select, onSelect);
            }

            return card;
        }
    }
}
