using System.Collections.Generic;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Ui;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Ui;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// Port of the two damage panels of the Blitz 4.8.0 HUD,
    /// <c>DamageTankPartsContainer</c> and <c>DamageCrewContainer</c>: a plate
    /// with one icon per module and per crew role, each icon drawn grey while
    /// the part is intact, yellow while it is damaged and red once it is out.
    ///
    /// The client builds both rows in full and only swaps frames, so the port
    /// does the same: all seven modules and all five crew roles exist from the
    /// first frame even though only the tracks and the ammo rack carry damage
    /// logic in this slice.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BattleDamagePanel : MonoBehaviour
    {
        private sealed class Entry
        {
            public string Id;
            public Image Image;
            public Sprite Ok;
            public Sprite Damaged;
            public Sprite Destroyed;
            public TankModule.Status Status = TankModule.Status.Normal;
        }

        private BattleTank _tank;
        private RectTransform _root;
        private RectTransform _tankRow;
        private RectTransform _crewRow;
        private readonly List<Entry> _modules = new List<Entry>();
        private readonly List<Entry> _crew = new List<Entry>();

        // The slide state of both plates. A row is parked one full height
        // below its resting place while it is hidden, which is what lets it
        // drive out of the bottom edge instead of appearing on top of the
        // health plate.
        private float _tankBottom;
        private float _crewBottom;
        private float _tankHeight;
        private float _crewHeight;
        private float _tankSlide;
        private float _crewSlide;
        private bool _tankWanted;
        private bool _crewWanted;

        /// <summary>The module row, <c>DamageTankPartsContainer</c>.</summary>
        public RectTransform TankRow { get { return _tankRow; } }

        /// <summary>The crew row, <c>DamageCrewContainer</c>.</summary>
        public RectTransform CrewRow { get { return _crewRow; } }

        /// <summary>Icons of the module row, in the client's frame order.</summary>
        public int ModuleCount { get { return _modules.Count; } }

        /// <summary>Icons of the crew row, in the client's frame order.</summary>
        public int CrewCount { get { return _crew.Count; } }

        /// <summary>
        /// How much of the bottom edge the damage stack currently covers, in
        /// canvas pixels, while it slides in and out. Zero while both plates
        /// are parked, which is why the health plate sits at its authored
        /// place in an undamaged battle.
        /// </summary>
        public float StackHeight
        {
            get
            {
                var tank = _tankSlide <= 0f
                    ? 0f : (_tankBottom + _tankHeight) * _tankSlide;
                var crew = _crewSlide <= 0f
                    ? 0f : (_crewBottom + _crewHeight) * _crewSlide;
                var stack = Mathf.Max(tank, crew);
                return stack <= 0f ? 0f : stack + DamagePanelTuning.StackPadding;
            }
        }

        public static BattleDamagePanel Create(
            RectTransform parent, AtlasRegionTable atlas, BattleTank tank)
        {
            if (parent == null || atlas == null) { return null; }

            var root = UiFactory.Node("damage_panel", parent);
            var panel = root.gameObject.AddComponent<BattleDamagePanel>();
            panel._tank = tank;
            panel._root = root;
            panel.Build(atlas);
            return panel;
        }

        /// <summary>The status one icon currently draws.</summary>
        public TankModule.Status StatusOf(string id)
        {
            var entry = Find(id);
            return entry == null ? TankModule.Status.Normal : entry.Status;
        }

        /// <summary>The sprite one icon currently draws.</summary>
        public Sprite SpriteOf(string id)
        {
            var entry = Find(id);
            return entry == null || entry.Image == null ? null : entry.Image.sprite;
        }

        private void Build(AtlasRegionTable atlas)
        {
            // BottomHUD is anchored to the bottom centre of the whole HUD.
            GodotRect.FullRect(_root);

            var tankRowHeight = DamagePanelTuning.TankBgHeight
                * DamagePanelTuning.PanelScale;

            _tankHeight = tankRowHeight;
            _crewHeight = DamagePanelTuning.CrewBgHeight
                * DamagePanelTuning.PanelScale;
            _tankBottom = DamagePanelTuning.TankRowBottom;
            _crewBottom = DamagePanelTuning.TankRowBottom + tankRowHeight
                + DamagePanelTuning.CrewRowGap;

            _tankRow = Container(
                "damage_tank_parts",
                atlas.Get("damage_panel_tank_bg"),
                DamagePanelTuning.TankBgWidth,
                DamagePanelTuning.TankBgHeight,
                DamagePanelTuning.TankRowBottom);
            _crewRow = Container(
                "damage_crew",
                atlas.Get("damage_panel_crew_bg"),
                DamagePanelTuning.CrewBgWidth,
                DamagePanelTuning.CrewBgHeight,
                DamagePanelTuning.TankRowBottom + tankRowHeight
                    + DamagePanelTuning.CrewRowGap);

            BuildRow(
                atlas,
                _tankRow,
                TankModuleIds.All,
                "damage_module_",
                DamagePanelTuning.ModuleIconWidth,
                DamagePanelTuning.ModuleIconHeight,
                _modules);
            BuildRow(
                atlas,
                _crewRow,
                TankCrewRoles.All,
                "damage_crew_",
                DamagePanelTuning.CrewIconWidth,
                DamagePanelTuning.CrewIconHeight,
                _crew);

            // Both containers ship visible: false and the client only shows a
            // row once one of its parts is no longer intact.
            _tankRow.gameObject.SetActive(false);
            _crewRow.gameObject.SetActive(false);
            Refresh();
        }

        private RectTransform Container(
            string name, Sprite background, float width, float height, float bottom)
        {
            var container = UiFactory.Node(name, _root);
            container.anchorMin = new Vector2(0.5f, 0f);
            container.anchorMax = new Vector2(0.5f, 0f);
            container.pivot = new Vector2(0.5f, 0f);
            container.sizeDelta = new Vector2(width, height);
            container.anchoredPosition = new Vector2(0f, bottom);
            container.localScale = new Vector3(
                DamagePanelTuning.PanelScale, DamagePanelTuning.PanelScale, 1f);

            var plate = UiFactory.Picture("bg", container, background);
            plate.raycastTarget = false;
            var rect = plate.rectTransform;
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(width, height);
            return container;
        }

        private static void BuildRow(
            AtlasRegionTable atlas,
            RectTransform container,
            IReadOnlyList<string> ids,
            string regionPrefix,
            float iconWidth,
            float iconHeight,
            List<Entry> entries)
        {
            var row = UiFactory.Node("row", container);
            row.anchorMin = new Vector2(0.5f, 0.5f);
            row.anchorMax = new Vector2(0.5f, 0.5f);
            row.pivot = new Vector2(0.5f, 0.5f);
            var step = iconWidth + DamagePanelTuning.RowSpacing;
            row.sizeDelta = new Vector2(step * ids.Count, iconHeight);
            row.anchoredPosition = Vector2.zero;

            var left = -0.5f * (step * ids.Count) + 0.5f * step;
            for (var i = 0; i < ids.Count; i++)
            {
                var entry = new Entry
                {
                    Id = ids[i],
                    Ok = atlas.Get(regionPrefix + ids[i] + "_ok"),
                    Damaged = atlas.Get(regionPrefix + ids[i] + "_damaged"),
                    Destroyed = atlas.Get(regionPrefix + ids[i] + "_destroyed"),
                };

                var image = UiFactory.Picture(ids[i], row, entry.Ok);
                image.raycastTarget = false;
                var rect = image.rectTransform;
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(iconWidth, iconHeight);
                rect.anchoredPosition = new Vector2(left + i * step, 0f);

                entry.Image = image;
                entries.Add(entry);
            }
        }

        private void LateUpdate()
        {
            Refresh();
            Advance(Time.unscaledDeltaTime);
        }

        /// <summary>Pulls every icon back in sync with the hull's modules.</summary>
        public void Refresh()
        {
            _tankWanted = Apply(_modules, true);
            _crewWanted = Apply(_crew, false);
            ApplySlide();
        }

        /// <summary>
        /// Asks for a plate without a hull behind the panel. Only the tests and
        /// the render tools use this; a battle drives the rows from the modules.
        /// </summary>
        public void Show(bool tankRow, bool crewRow)
        {
            _tankWanted = tankRow;
            _crewWanted = crewRow;
            ApplySlide();
        }

        /// <summary>
        /// Drives the slide of both plates. Split from <see cref="Refresh"/> so
        /// a test can step the animation without a running battle.
        /// </summary>
        public void Advance(float deltaTime)
        {
            var step = DamagePanelTuning.SlideDuration <= 0f
                ? 1f
                : Mathf.Max(0f, deltaTime) / DamagePanelTuning.SlideDuration;
            _tankSlide = Mathf.MoveTowards(_tankSlide, _tankWanted ? 1f : 0f, step);
            _crewSlide = Mathf.MoveTowards(_crewSlide, _crewWanted ? 1f : 0f, step);
            ApplySlide();
        }

        /// <summary>
        /// Parks a hidden row one height below its resting place and puts a
        /// visible one back on it, so the plate travels out of the bottom edge
        /// rather than over the health bar.
        /// </summary>
        private void ApplySlide()
        {
            Place(_tankRow, _tankBottom, _tankHeight, _tankSlide, _tankWanted);
            Place(_crewRow, _crewBottom, _crewHeight, _crewSlide, _crewWanted);
        }

        private static void Place(
            RectTransform row, float bottom, float height, float slide, bool wanted)
        {
            if (row == null) { return; }

            var visible = wanted || slide > 0f;
            if (row.gameObject.activeSelf != visible)
            {
                row.gameObject.SetActive(visible);
            }

            if (!visible) { return; }

            var parked = -(bottom + height);
            row.anchoredPosition = new Vector2(
                0f, Mathf.Lerp(parked, bottom, Mathf.Clamp01(slide)));
        }

        private bool Apply(List<Entry> entries, bool modules)
        {
            var any = false;
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                // The crew has no health pool yet: the roles are drawn intact
                // so the panel matches the client one for one.
                var status = modules && _tank != null
                    ? _tank.ModuleStatus(entry.Id)
                    : TankModule.Status.Normal;
                entry.Status = status;
                if (entry.Image != null)
                {
                    entry.Image.sprite = SpriteFor(entry, status);
                }

                if (status != TankModule.Status.Normal) { any = true; }
            }

            return any;
        }

        private static Sprite SpriteFor(Entry entry, TankModule.Status status)
        {
            switch (status)
            {
                case TankModule.Status.Damaged: return entry.Damaged;
                case TankModule.Status.Destroyed: return entry.Destroyed;
                default: return entry.Ok;
            }
        }

        private Entry Find(string id)
        {
            for (var i = 0; i < _modules.Count; i++)
            {
                if (_modules[i].Id == id) { return _modules[i]; }
            }

            for (var i = 0; i < _crew.Count; i++)
            {
                if (_crew[i].Id == id) { return _crew[i]; }
            }

            return null;
        }
    }
}
