using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Hangar;
using BlitzLegacy.Runtime.Audio;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// The list the hangar row of the settings screen opens: one entry per catalog hangar in the grey
    /// panel of the client's popups, the hangar on screen ticked. A hangar the account may not use (the
    /// premium one without premium) stays listed, greyed out with the reason, and cannot be picked.
    /// Cancel or a click beside the panel close the list without a change; the settings screen maps
    /// Escape to <see cref="Close"/> while the list is up, so the key does not close both.
    /// </summary>
    public sealed class HangarPickerPopup : MonoBehaviour
    {
        public const float PanelWidth = 640f;
        public const float HeaderHeight = 70f;
        public const float EntryHeight = 72f;
        public const float EntryGap = 4f;
        public const float Padding = 16f;
        public const float CancelWidth = 260f;
        public const float CancelHeight = 60f;
        public const int TitleFontSize = 26;
        public const int NameFontSize = 25;
        public const int NoteFontSize = 17;
        private const float CheckSize = 36f;
        private const float BadgeHeight = 34f;

        private static readonly Color Blocker = new Color(0f, 0f, 0f, 0.45f);
        private static readonly Color LockedText = new Color(0.55f, 0.58f, 0.62f, 1f);
        private static readonly Color NoteText = new Color(0.8f, 0.68f, 0.45f, 1f);

        public sealed class Entry
        {
            public HangarInfo Info;
            public Button Button;
            public Text Name;
            public Image Check;
            public Image Badge;
            public Text Note;
            public bool Available;
        }

        private readonly List<Entry> _entries = new List<Entry>();
        private HangarUiKit _kit;
        private bool _closed;

        /// <summary>Raised with the entry the player picked, before the list closes.</summary>
        public Action<HangarInfo> Picked;

        /// <summary>Raised once the list is gone, picked or not.</summary>
        public Action Dismissed;

        public RectTransform Root { get; private set; }

        public RectTransform Panel { get; private set; }

        public Text Title { get; private set; }

        public Button Cancel { get; private set; }

        /// <summary>The dimmed area around the panel; a click on it closes the list.</summary>
        public Button Outside { get; private set; }

        public bool IsClosed
        {
            get { return _closed; }
        }

        public IList<Entry> Entries
        {
            get { return _entries; }
        }

        public static HangarPickerPopup Create(
            RectTransform parent,
            HangarUiKit kit,
            HangarKind current,
            Func<HangarInfo, bool> available,
            Color normal,
            Color selected)
        {
            if (parent == null) { throw new ArgumentNullException("parent"); }
            if (kit == null) { throw new ArgumentNullException("kit"); }
            if (available == null) { throw new ArgumentNullException("available"); }

            var root = UiFactory.Node("hangar_picker", parent);
            GodotRect.FullRect(root);
            var popup = root.gameObject.AddComponent<HangarPickerPopup>();
            popup.Root = root;
            popup._kit = kit;
            popup.Build(current, available, normal, selected);
            return popup;
        }

        private void Build(HangarKind current, Func<HangarInfo, bool> available, Color normal, Color selected)
        {
            // A click beside the panel dismisses the list, like a dropdown. The
            // dimmer is the panel's sibling rather than its parent, so a click on
            // the panel itself does not bubble up to it.
            var blocker = UiFactory.Picture("dim", Root, null);
            GodotRect.FullRect(blocker.rectTransform);
            blocker.color = Blocker;
            blocker.raycastTarget = true;
            Outside = blocker.gameObject.AddComponent<Button>();
            Outside.targetGraphic = blocker;
            Outside.transition = Selectable.Transition.None;
            Outside.onClick.AddListener(Close);

            var catalog = HangarCatalog.All;
            var listHeight = (catalog.Count * (EntryHeight + EntryGap)) - EntryGap;
            var height = HeaderHeight + Padding + listHeight + Padding + CancelHeight + Padding;

            var body = _kit.Picture("panel", Root, "panel_body", 0f, 0f, PanelWidth, height);
            Panel = body.rectTransform;
            GodotRect.Centered(Panel, -PanelWidth * 0.5f, -height * 0.5f, PanelWidth * 0.5f, height * 0.5f);
            // The panel swallows its own clicks so only the dimmed area closes the list.
            body.raycastTarget = true;

            _kit.Picture("header", Panel, "panel_header", 0f, 0f, PanelWidth, HeaderHeight).raycastTarget = false;
            Title = _kit.Label(
                "title", Panel, TitleFontSize, TextAnchor.MiddleCenter, _kit.Str("ui:hangar/picker_title"),
                _kit.Bold, UiTheme.BoldLabel, 0f, 0f, PanelWidth, HeaderHeight);

            var y = HeaderHeight + Padding;
            for (var i = 0; i < catalog.Count; i++)
            {
                _entries.Add(BuildEntry(catalog[i], current, available(catalog[i]), normal, selected, y));
                y += EntryHeight + EntryGap;
            }

            Cancel = _kit.StyleButton(
                "btn_cancel", Panel, "button_generic",
                (PanelWidth - CancelWidth) * 0.5f, height - Padding - CancelHeight, CancelWidth, CancelHeight);
            var cancel = UiFactory.Label(
                "label", (RectTransform)Cancel.transform, _kit.Bold, NameFontSize - 5, UiTheme.ButtonFont,
                TextAnchor.MiddleCenter, _kit.Str("CANCEL"));
            GodotRect.FullRect(cancel.rectTransform);
            _kit.Bind(Cancel, Close, AudioService.SoundEnter);
        }

        private Entry BuildEntry(
            HangarInfo info, HangarKind current, bool available, Color normal, Color selected, float y)
        {
            var entry = new Entry { Info = info, Available = available };
            var width = PanelWidth - (Padding * 2f);
            entry.Button = _kit.StyleButton("hangar_" + info.Id, Panel, null, Padding, y, width, EntryHeight);
            var button = (RectTransform)entry.Button.transform;
            var isCurrent = info.Kind == current;
            var fill = isCurrent ? selected : normal;
            if (!available)
            {
                fill.a *= 0.55f;
            }

            entry.Button.GetComponent<Image>().color = fill;
            entry.Button.interactable = available;

            entry.Name = _kit.Label(
                "name", button, NameFontSize, TextAnchor.MiddleLeft, _kit.Str(info.NameKey), _kit.Regular,
                available ? UiTheme.ButtonFont : LockedText, 24f, 0f, width - 48f, EntryHeight);
            entry.Name.horizontalOverflow = HorizontalWrapMode.Overflow;

            var nameRight = 24f + Mathf.Ceil(entry.Name.preferredWidth);
            if (info.RequiresPremium)
            {
                var art = _kit.Art(HangarUiKit.PremiumBadgeRegion);
                var badgeWidth = art == null ? BadgeHeight : BadgeHeight * art.rect.width / art.rect.height;
                entry.Badge = _kit.Picture(
                    "premium", button, HangarUiKit.PremiumBadgeRegion,
                    nameRight + 10f, (EntryHeight - BadgeHeight) * 0.5f, badgeWidth, BadgeHeight);
                entry.Badge.preserveAspect = true;
                entry.Badge.raycastTarget = false;
                if (!available)
                {
                    entry.Badge.color = new Color(1f, 1f, 1f, 0.5f);
                }
            }

            if (!available)
            {
                var noteKey = info.RequiresPremium ? "ui:hangar/needs_premium" : "ui:hangar/unsupported";
                entry.Note = _kit.Label(
                    "note", button, NoteFontSize, TextAnchor.MiddleRight, _kit.Str(noteKey), _kit.Regular,
                    NoteText, width * 0.45f, 0f, (width * 0.55f) - 20f, EntryHeight);
            }

            if (isCurrent)
            {
                entry.Check = _kit.Picture(
                    "check", button, "camo_check_mark",
                    width - 20f - CheckSize, (EntryHeight - CheckSize) * 0.5f, CheckSize, CheckSize);
                entry.Check.raycastTarget = false;
            }

            _kit.Bind(entry.Button, () => Pick(entry), AudioService.SoundEnter);
            return entry;
        }

        /// <summary>Picks <paramref name="entry"/> and closes the list; refused for an unavailable hangar.</summary>
        public void Pick(Entry entry)
        {
            if (_closed || entry == null || !entry.Available)
            {
                return;
            }

            var picked = Picked;
            Picked = null;
            if (picked != null)
            {
                picked(entry.Info);
            }

            Close();
        }

        public void Close()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            gameObject.SetActive(false);
            var dismissed = Dismissed;
            Dismissed = null;
            if (dismissed != null)
            {
                dismissed();
            }

            if (Application.isPlaying)
            {
                Destroy(gameObject);
            }
            else
            {
                DestroyImmediate(gameObject);
            }
        }
    }
}
