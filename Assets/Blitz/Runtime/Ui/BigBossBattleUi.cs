using System;
using System.Collections.Generic;
using System.Globalization;
using BlitzLegacy.Core.Localization;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Ui;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Platform;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>Source RoleProgressBar/BossmodeAbility/RespawnTimer geometry, 11.20 art and
    /// WarHelios fonts. Native-only screen placement is an explicit adapter, not source data.
    /// The role's button ability (the cons_jump slot), self-activating abilities and timed
    /// effects (the status row above the health bar) are the standard HUD's, fed by
    /// BigBossHudMode.</summary>
    public sealed class BigBossBattleUi : MonoBehaviour
    {
        public const float RoleWidth = 205, RoleHeight = 96, ProgressWidth = 136;
        public const float CardWidth = 320, AbilityIconSize = 128;
        /// <summary>The plate numeral rises this far so the role marker fits beneath it.</summary>
        public const float PlateLevelLift = 14;
        /// <summary>Respawn text, timer, role icon and role name above the ability cards.</summary>
        public const float RespawnHeaderHeight = 248, RespawnMinWidth = 500;
        private BigBossTank _owner;
        private LocalizationTable _lang;
        private Font _bold, _regular;
        private RectTransform _role, _details, _cards, _respawnContent, _respawnCards;
        private Text _name, _level, _timer, _warning, _respawnText, _nextName;
        private Image _icon, _fill, _wings, _fillTip, _nextIcon;
        private GameObject _levelBackground, _progressTriangle, _progressBackground;
        private BattleRespawnFrame _frame;
        private readonly Image[] _lives = new Image[3];
        private readonly GameObject[] _lifeBackgrounds = new GameObject[3];
        private readonly Dictionary<string, Sprite> _sprites = new Dictionary<string, Sprite>();
        private readonly List<Image> _roleMarkers = new List<Image>();
        private readonly List<int> _markerRoles = new List<int>();
        private readonly List<CanvasGroup> _healMarkers = new List<CanvasGroup>();
        private readonly Dictionary<BattleTank, Image> _bossDots = new Dictionary<BattleTank, Image>();
        private int _roleId = -1, _currentLevel, _respawnRole = -1, _respawnLevel, _shownLevel = -1, _shownTimer = -1;
        private bool _captured, _warnAlly, _warnEnemy;
        private float _warningUntil;
        private Vector2 _respawnSize = new Vector2(RespawnMinWidth, RespawnHeaderHeight);
        /// <summary>Fade of the shared respawn presentation.</summary>
        public float RespawnAlpha { get { return _frame != null ? _frame.Alpha : 0; } }
        public static BigBossBattleUi Create(Transform parent, BigBossTank owner)
        {
            var root = UiFactory.Node("BigBossBattleUi", parent); GodotRect.FullRect(root);
            var c = root.gameObject.AddComponent<BigBossBattleUi>(); c._owner = owner;
            c._lang = LocalizationTable.Parse(BlitzResources.LoadText("lang")); c._lang.Language = BlitzBootstrap.Current?.Settings.Language ?? "en";
            c._bold = Resources.Load<Font>("Fonts/WarHeliosCondCBold"); c._regular = Resources.Load<Font>("Fonts/WarHeliosCondC");
            // Source specifies leftAnchor=25, not a vertical screen anchor. Keep the
            // native-only vertical choice isolated here; no guessed YAML value is exported.
            c.Modal(root, 3);
            c._role = UiFactory.Node("RoleProgressBar", root); GodotRect.Apply(c._role, 0, .5f, 0, .5f, 25, -48, 230, 48);
            c.Box("RoleNameBackground", c._role, 48, 42, 142, 30, new Color(.117647f, .133333f, .137255f, .701961f));
            c.Picture("RoleNameTriangle", c._role, "role-bg", 190, 42, 15, 30);
            c._progressBackground = c.Box("ProgressBackground", c._role, 54, 72, 136, 4, new Color(.117647f, .133333f, .137255f)).gameObject;
            c._fill = c.Box("ProgressBarFill", c._role, 54, 72, 136, 4, Color.white);
            var triangle = c.Picture("ProgressTriangle", c._role, "bg_triangle", 190, 72, 2, 4); triangle.color = new Color(.117647f,.133333f,.137255f); c._progressTriangle = triangle.gameObject;
            c._fillTip = c.Picture("ProgressFillTriangle", c._role, "bg_triangle", 190,72,2,4);
            c._name = c.Label("Name", c._role, 16, 86, 47, 104, 20, "");
            var roleButton = UiFactory.TextButton("RoleIcon", c._role, null, null, 0, null); GodotRect.TopLeft((RectTransform)roleButton.transform, 0, 0, 96, 96); roleButton.GetComponent<Image>().color = Color.clear;
            c._icon = c.Picture("Icon", roleButton.transform, null, 0, 0, 96, 96); roleButton.onClick.AddListener(c.ToggleDetails);
            c._levelBackground = c.Picture("LevelBackground", c._role, "bg_level", 39.5f, 60, 21, 28).gameObject;
            c._wings = c.Picture("Wings", c._role, "icon_wings", 37, 62, 26, 24);
            c._level = c.Label("Level", c._role, 19, 34, 58, 32, 32, "");
            for (var i = 0; i < 3; i++) { c._lifeBackgrounds[i] = c.Picture("LifeBG_" + i, c._role, "icon_live-bg", 86 + 24 * i, 72, 24, 24).gameObject; c._lives[i] = c.Picture("Life_" + i, c._role, "icon_live", 86 + 24 * i, 72, 24, 24); }
            c._details = UiFactory.Node("BossmodeAbilitiesPanel", root); GodotRect.FullRect(c._details); c.Modal(c._details, 12);
            var close = UiFactory.TextButton("Dismiss", c._details, null, null, 0, null); GodotRect.FullRect((RectTransform)close.transform); close.GetComponent<Image>().color = new Color(0, 0, 0, .45f); close.onClick.AddListener(c.ToggleDetails);
            c._cards = UiFactory.Node("PanelContainer", c._details);
            c._details.gameObject.SetActive(false);
            // The shared respawn presentation: dimmed, faded in after a beat, scaled to fit.
            c._frame = BattleRespawnFrame.Create(root, "RespawnTimer");
            c._respawnContent = c._frame.Content;
            c._respawnText = c.Label("RespawnText", c._respawnContent, 19, 0, 0, RespawnMinWidth, 28, c.Get("#respawn/awaitReturnToBattleText"));
            c._timer = c.Label("Time", c._respawnContent, 56, 0, 28, RespawnMinWidth, 70, "");
            c._nextIcon = c.Picture("RoleIcon", c._respawnContent, null, 0, 106, 96, 96);
            c._nextName = c.Label("RoleName", c._respawnContent, 24, 0, 206, RespawnMinWidth, 30, "");
            c._respawnCards = UiFactory.Node("Abilities", c._respawnContent);
            c._warning = c.Label("CloseVictory", root, 19, 0, 0, 600, 42, ""); GodotRect.Apply(c._warning.rectTransform, .5f, 0, .5f, 0, -300, 110, 300, 152);
            var markerX = TankPlateTuning.LevelLeft + TankPlateTuning.LevelScale * (TankPlateTuning.LevelRight - TankPlateTuning.LevelLeft) / 2;
            for (var i = 0; i < owner.Session.Hud.TankPlateCount; i++)
            {
                var plate = owner.Session.Hud.GetTankPlate(i);
                // The role marker sits under the Roman numeral, which moves up to make room.
                if (plate.LevelIcon != null) plate.LevelIcon.rectTransform.anchoredPosition += Vector2.up * PlateLevelLift;
                var image = c.Picture("BigBossRole", plate.Root, null, markerX - 12, -144, 24, 22); c._roleMarkers.Add(image); c._markerRoles.Add(-1);
                var heal = UiFactory.Node("HealerIconContainer",plate.Root); GodotRect.TopLeft(heal,-16,-52,16,-10);
                c.Picture("HealPlus",heal,"icon_plus",0,21,32,21);c.Picture("HealUp",heal,"icon_up",0,0,32,21);
                var opacity=heal.gameObject.AddComponent<CanvasGroup>();opacity.alpha=0;c._healMarkers.Add(opacity);
            }
            c.RefreshBossDots();
            owner.Effect += c.Effect;
            owner.Respawned += c.RestoreEquipment;
            c.Refresh(); return c;
        }
        private void Modal(RectTransform node, int offset)
        {
            var canvas = node.gameObject.AddComponent<Canvas>(); canvas.overrideSorting = true; canvas.sortingOrder = BattleHud.SortingOrder + offset; node.gameObject.AddComponent<GraphicRaycaster>();
        }
        private string Get(string key) { return _lang.Has(key) ? _lang.Get(key) : ""; }
        private Sprite Sprite(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var resource = name.Contains("/") ? name : "BigBoss/Textures/" + name;
            if (_sprites.TryGetValue(resource, out var s)) return s;
            var t = Resources.Load<Texture2D>(resource); if (t == null) return null;
            var border = resource.EndsWith("bg_border_1px_r4", StringComparison.Ordinal) ? new Vector4(8, 8, 8, 8) : resource.EndsWith("bg_r1", StringComparison.Ordinal) ? new Vector4(2, 2, 2, 2) : Vector4.zero;
            s = UnityEngine.Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(.5f, .5f), 200, 0, SpriteMeshType.FullRect, border); _sprites.Add(resource, s); return s;
        }
        private Image Picture(string n, Transform p, string s, float x, float y, float w, float h)
        { var image = UiFactory.Picture(n, p, Sprite(s)); GodotRect.TopLeft(image.rectTransform, x, y, x + w, y + h); return image; }
        private Image Box(string n, Transform p, float x, float y, float w, float h, Color color)
        { var image = Picture(n, p, null, x, y, w, h); image.color = color; return image; }
        private Text Label(string n, Transform p, int size, float x, float y, float w, float h, string text, bool regular = false)
        { var t = UiFactory.Label(n, p, regular ? _regular : _bold, size, new Color(.960784f, .960784f, .960784f), TextAnchor.MiddleCenter, text); GodotRect.TopLeft(t.rectTransform, x, y, x + w, y + h); t.horizontalOverflow = HorizontalWrapMode.Wrap; return t; }
        private static Color RoleColor(JsonNode role)
        {
            var color = role["color"]; return new Color(color[0].AsFloat(), color[1].AsFloat(), color[2].AsFloat(), color[3].AsFloat());
        }
        private static void ClearChildren(Transform root)
        {
            for (var i = root.childCount - 1; i >= 0; i--)
            {
                var child = root.GetChild(i); child.gameObject.SetActive(false);
                if (Application.isPlaying) Destroy(child.gameObject); else DestroyImmediate(child.gameObject);
            }
        }
        private void Rebuild()
        {
            ClearChildren(_cards);
            var state = _owner.State; var rules = state.Rules; var ids = rules.Abilities(state.RoleId, state.Level);
            _roleId = state.RoleId; _currentLevel = state.Level;
            var role = rules.Role(state.RoleId);
            _icon.sprite = Sprite(role["icon"].AsString()); _name.text = Get(role["nameKey"].AsString()); _fill.color = RoleColor(role);
            var height = 0f;
            for (var index = 0; index < ids.Length; index++) height = Mathf.Max(height, BuildCard(rules.Ability(ids[index]), index, _cards));
            var width = ids.Length * CardWidth; GodotRect.Centered(_cards, -width / 2, -height / 2, width / 2, height / 2);
            // Fit the complete source-sized panel only on viewports smaller than its content.
            var available = ((RectTransform)transform).rect; _cards.localScale = Vector3.one * Mathf.Min(1, Mathf.Min((available.width - 32) / width, (available.height - 24) / Mathf.Max(1, height)));
        }
        /// <summary>The role the tank returns with and its abilities at the shared level.</summary>
        private void BuildRespawn(int roleId, int level)
        {
            _respawnRole = roleId; _respawnLevel = level; ClearChildren(_respawnCards);
            var rules = _owner.State.Rules; var role = rules.Role(roleId); var ids = rules.Abilities(roleId, level);
            var height = 0f;
            for (var index = 0; index < ids.Length; index++) height = Mathf.Max(height, BuildCard(rules.Ability(ids[index]), index, _respawnCards));
            var cards = ids.Length * CardWidth; var width = Mathf.Max(RespawnMinWidth, cards);
            GodotRect.TopLeft(_respawnCards, (width - cards) / 2, RespawnHeaderHeight, (width + cards) / 2, RespawnHeaderHeight + height);
            GodotRect.TopLeft(_respawnText.rectTransform, 0, 0, width, 28); GodotRect.TopLeft(_timer.rectTransform, 0, 28, width, 98);
            GodotRect.TopLeft(_nextIcon.rectTransform, width / 2 - 48, 106, width / 2 + 48, 202); _nextIcon.sprite = Sprite(role["icon"].AsString());
            GodotRect.TopLeft(_nextName.rectTransform, 0, 206, width, 236); _nextName.text = Get(role["nameKey"].AsString()); _nextName.color = RoleColor(role);
            _respawnSize = new Vector2(width, RespawnHeaderHeight + height);
            GodotRect.Centered(_respawnContent, -width / 2, -_respawnSize.y / 2, width / 2, _respawnSize.y / 2);
        }
        private float BuildCard(JsonNode ability, int index, RectTransform parent)
        {
            var card = UiFactory.Node("Ability_" + ability["id"].AsString(), parent); GodotRect.TopLeft(card, index * 320, 0, (index + 1) * 320, 500);
            var fade = Picture("Fade", card, "bg_loader-circle_shadow", 64, 0, 192, 192);
            Picture("icon", card, ability["iconBig"].AsString(), 96, 32, 128, 128);
            var kind = Get(ability["kind"].AsString() == "active" ? "#bossmode:ability/active" : "#bossmode:ability/passive");
            var textWidth = Mathf.Max(24, _bold.GetCharacterInfo('A', out var info, 12) ? kind.Length * info.advance : kind.Length * 7);
            var border = Picture("TypeBorder", card, "bg_border_1px_r4", 160 - (textWidth + 8) / 2, 176, textWidth + 8, 20); border.type = Image.Type.Sliced;
            Label("Type", border.transform, 12, 4, 2, textWidth, 16, kind);
            var name = Get(ability["nameKey"].AsString());
            var title = Label("AbilityName", card, 21, 32, 200, 256, 48, name); title.gameObject.SetActive(name.Length > 0);
            var y = name.Length == 0 ? 224f : 248f;
            var desc = Get(ability["descriptionKey"].AsString());
            if (desc.Length > 0)
            {
                var label = Label("AbilityDescription", card, 14, 32, y + 24, 256, 84, desc, true); label.alignment = TextAnchor.UpperCenter;
                var h = Mathf.Min(84, label.preferredHeight); GodotRect.TopLeft(label.rectTransform, 32, y + 24, 288, y + 24 + h); y += 24 + h;
            }
            y += 32;
            foreach (var param in ability["descrParams"].Items)
            {
                var key = param["nameKey"].AsString(); var text = Get(key); var value = param["value"].AsString();
                // Profile: the boss pool multiple is a compatibility value, so show the one in play.
                if (key == "Boss_hpboost_description") value = _owner.State.Rules.P("bossMaxHealthFactor").ToString("0.##", CultureInfo.InvariantCulture);
                var label = Label("Descr", card, 14, 32, y + 4, 196, 42, text, true); label.alignment = TextAnchor.MiddleLeft; label.color = new Color(1, 1, 1, .5f);
                var h = Mathf.Clamp(label.preferredHeight, 17, 42); GodotRect.TopLeft(label.rectTransform, 32, y + 4, 228, y + 4 + h);
                Label("Value", card, 19, 248, y + 4, 40, h, value); y += h + 8;
            }
            var bg = Picture("Background", card, "bg_r1", 0, 192, 320, Mathf.Max(0, y + 32 - 192)); bg.type = Image.Type.Sliced; bg.color = new Color(.117647f, .133333f, .137255f, .701961f); bg.transform.SetAsFirstSibling();
            if (index > 0) Box("Paginator", card, 0, 160, 1, Mathf.Max(0, y - 160), new Color(1, 1, 1, .15f));
            return y + 32;
        }
        private void RestoreEquipment() { _owner.Session.Controls?.Consumables?.Refresh(); }
        public void ToggleDetails()
        {
            var opening = !_details.gameObject.activeSelf;
            if (opening) { _captured = MouseLock.Captured; MouseLock.Set(false); }
            else if (_captured && _owner.Session.Started && _owner.Session.SettingsUi == null && !_owner.Session.PauseMenu.Visible) MouseLock.Set(true);
            _owner.Session.SetInputActive(!opening && _owner.Session.Started && _owner.Session.SettingsUi == null && !_owner.Session.PauseMenu.Visible);
            _details.gameObject.SetActive(opening);
        }
        private void Effect(string id, Vector3 point)
        {
            // The world scene and UI scene are deliberately distinct source assets.
            if (id == "levelUp")
            {
                BigBossLevelUpFx.Play(_role);
            }
        }
        private void Refresh()
        {
            if (_owner == null || _owner.State == null) return;
            var s = _owner.State; var ongoing = _owner.Session.Started;
            if (_roleId != s.RoleId || _currentLevel != s.Level) Rebuild();
            _role.gameObject.SetActive(ongoing && !_owner.Tank.Dead);
            _levelBackground.SetActive(!s.Boss); _progressTriangle.SetActive(!s.Boss);
            var progress = s.Rules.Progress(s.Points);
            _fillTip.gameObject.SetActive(!s.Boss); _fillTip.color=_fill.color;
            GodotRect.TopLeft(_fillTip.rectTransform,54+ProgressWidth*progress,72,56+ProgressWidth*progress,76);
            _level.gameObject.SetActive(!s.Boss); _wings.gameObject.SetActive(!s.Boss && s.Level == 5); _progressBackground.SetActive(!s.Boss);
            _fill.gameObject.SetActive(!s.Boss); _fill.rectTransform.sizeDelta = new Vector2(ProgressWidth * progress, 4);
            if (_shownLevel != s.Level) { _shownLevel = s.Level; _level.text = s.Level.ToString(CultureInfo.InvariantCulture); }
            for (var i = 0; i < 3; i++) { _lives[i].gameObject.SetActive(s.Boss && i < s.Lives); _lifeBackgrounds[i].SetActive(s.Boss); }
            RefreshRespawn(ongoing);
            RefreshBossDots();
            for (var i = 0; i < _roleMarkers.Count; i++)
            {
                var target = _owner.Session.Hud.GetTankPlate(i).Tank;
                if (target?.BigBoss?.State == null) continue;
                var roleId = target.BigBoss.State.RoleId;
                if (_markerRoles[i] != roleId) { _markerRoles[i] = roleId; _roleMarkers[i].sprite = Sprite(s.Rules.Role(roleId)["marker"].AsString()); }
                _healMarkers[i].alpha=HealerPulse(Time.time - target.BigBoss.HealedAt);
            }
            var match = _owner.Session.BigBoss?.Match;
            if (ongoing && match != null)
            {
                var a = _owner.Tank.Team == 0 ? match.TeamA : match.TeamB; var b = _owner.Tank.Team == 0 ? match.TeamB : match.TeamA;
                if (!_warnAlly && a <= 5) { _warnAlly = true; Warn("#bossmode:Status/CloseVictory/Allies", a); }
                if (!_warnEnemy && b <= 5) { _warnEnemy = true; Warn("#bossmode:Status/CloseVictory/Enemies", b); }
            }
            _warning.gameObject.SetActive(ongoing && Time.time < _warningUntil);
            if (!ongoing && _details.gameObject.activeSelf) ToggleDetails();
        }
        /// <summary>The boss marker replaces the boss's minimap dot. Online the roles arrive
        /// from the host after the HUD is built, so the swap follows the mirrored state.</summary>
        private void RefreshBossDots()
        {
            var roster = _owner.Session.Roster; var minimap = _owner.Session.Minimap;
            if (roster == null || minimap == null) return;
            for (var side = 0; side < 2; side++)
            {
                var tanks = side == 0 ? roster.TanksA : roster.TanksB;
                for (var i = 0; i < tanks.Count; i++)
                {
                    var tank = tanks[i];
                    if (tank == null || tank.BigBoss == null || tank.BigBoss.State == null) continue;
                    var boss = tank.BigBoss.State.Boss;
                    _bossDots.TryGetValue(tank, out var dot);
                    if (dot == null && !boss) continue;
                    var marker = minimap.MarkerOf(tank); if (marker == null) continue;
                    if (dot == null)
                    {
                        dot = Picture("BigBossRole", marker, _owner.State.Rules.Role(1)["marker"].AsString(), 0, 0, 24, 22);
                        GodotRect.FullRect(dot.rectTransform); _bossDots[tank] = dot;
                    }
                    if (dot.gameObject.activeSelf != boss) dot.gameObject.SetActive(boss);
                    var source = marker.GetComponent<Image>(); if (source != null && source.enabled == boss) source.enabled = !boss;
                }
            }
        }
        private void RefreshRespawn(bool ongoing)
        {
            var s = _owner.State; var showing = ongoing && s.Respawning;
            if (showing)
            {
                var next = s.NextRoleId >= 0 ? s.NextRoleId : s.RoleId; var level = s.Rules.IsBoss(next) ? 1 : s.Rules.Level(s.Points);
                if (next != _respawnRole || level != _respawnLevel) BuildRespawn(next, level);
            }
            _frame.Present(showing, Time.time - (s.RespawnAt - s.Rules.P("respawnSeconds")), _respawnSize, false);
            if (!showing) return;
            var seconds = Mathf.Max(1, Mathf.CeilToInt((float)(s.RespawnAt - Time.time)));
            if (seconds != _shownTimer) { _shownTimer = seconds; _timer.text = seconds.ToString(CultureInfo.InvariantCulture); }
        }
        public static float HealerPulse(float age)
        {
            if(age<0 || age>=5)return 0;
            var phase=age%1;var up=phase<.5f;var t=Mathf.SmoothStep(0,1,up?phase*2:(phase-.5f)*2);
            if(age>=4)return up?Mathf.Lerp(.5f,1,t):1-t;
            return up?Mathf.Lerp(age<1?0:.5f,1,t):Mathf.Lerp(1,.5f,t);
        }
        private void Warn(string key, int points) { _warning.text = Get(key).Replace("%(points)", points.ToString(CultureInfo.InvariantCulture)); _warningUntil = Time.time + 4; }
        private void LateUpdate() { Refresh(); if (_details.gameObject.activeSelf && Keyboard.current?.escapeKey.wasPressedThisFrame == true) ToggleDetails(); }
        private void OnDestroy()
        {
            if (_owner != null) { _owner.Effect -= Effect; _owner.Respawned -= RestoreEquipment; }
            foreach (var sprite in _sprites.Values) if (sprite != null) { if (Application.isPlaying) Destroy(sprite); else DestroyImmediate(sprite); }
        }
    }
}
