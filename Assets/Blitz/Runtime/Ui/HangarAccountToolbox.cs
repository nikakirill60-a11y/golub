using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Time;
using BlitzLegacy.Runtime.Audio;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// screens/main/menu/VBoxContainer/account_toolbox driven by
    /// assets/gfx/ui/hangar/ui_account_toolbox.gd: the debug tools the source
    /// hangar keeps at the bottom of the burger menu.
    ///
    /// Geometry, captions and colours come from
    /// Resources/Data/hangar_menu_source_manifest.json, which
    /// Tools/port_godot_hangar_menu.py dumps out of the live source scene. The
    /// source <c>_ready()</c> frees the node outside a debug build, so
    /// <see cref="Enabled"/> gates the whole panel the same way.
    /// </summary>
    public sealed class HangarAccountToolbox
    {
        /// <summary>account_toolbox: custom_minimum_size = (0, 246).</summary>
        public const float Height = 246f;

        /// <summary>
        /// The inner VBoxContainer is anchored with offset_bottom = 236, but its
        /// rows need 270 px, and Godot never shrinks a container below its
        /// minimum size. The last row therefore hangs 24 px past the tinted
        /// background, exactly as it does in the source build.
        /// </summary>
        public const float InnerHeight = 270f;

        public const float Width = HangarMenuPanel.Width;

        // Row rects, relative to the toolbox's own top-left corner.
        private static readonly Rect TitleRect = new Rect(0f, 9f, Width, 16f);
        private static readonly Rect DividerRect = new Rect(0f, 29f, Width, 1f);
        private static readonly Rect EraseRewardsRect = new Rect(0f, 34f, Width, 31f);
        private static readonly Rect EraseBattlesRect = new Rect(0f, 69f, Width, 31f);
        private const float FirstRowTop = 104f;
        private const float RowPitch = 34f;
        private const float RowHeight = 30f;
        private const float RowButtonWidth = 142f;
        private const float RowFieldLeft = 146f;
        private const float RowFieldWidth = 143f;
        private const int ButtonFontSize = 16;
        private const int FieldFontSize = 15;
        private const int TitleFontSize = 15;

        /// <summary>bg: Color(0, 0, 0, 0.41960785).</summary>
        private static readonly Color BackgroundColor = new Color(0f, 0f, 0f, 0.41960785f);

        /// <summary>TextEdit/styles/normal: StyleBoxFlat bg_color.</summary>
        private static readonly Color FieldBackground = new Color(0.1f, 0.1f, 0.1f, 0.6f);

        private readonly HangarUiKit _kit;
        private readonly AccountData _account;
        private readonly TanksDatabase _tanks;
        private readonly TankResearchService _research;
        private readonly IClock _clock;

        private InputField _credits;
        private InputField _gold;
        private InputField _freeXp;
        private InputField _premHours;

        public HangarAccountToolbox(
            HangarUiKit kit,
            AccountData account,
            TanksDatabase tanks,
            TankResearchService research,
            IClock clock)
        {
            if (kit == null)
            {
                throw new ArgumentNullException("kit");
            }

            if (account == null)
            {
                throw new ArgumentNullException("account");
            }

            _kit = kit;
            _account = account;
            _tanks = tanks;
            _research = research;
            _clock = clock ?? SystemClock.Instance;
        }

        /// <summary>
        /// Godot <c>OS.is_debug_build()</c>, which frees the node when false.
        /// The production-debug APK carries these tools without Unity's
        /// development player, so the question goes through BlitzBuildFlags
        /// instead of <c>Debug.isDebugBuild</c> directly.
        /// </summary>
        public static bool Enabled
        {
            get { return BlitzBuildFlags.DebugTools; }
        }

        /// <summary>Fallback label refresh for isolated toolbox callers.</summary>
        public Action CurrenciesChanged;

        /// <summary>
        /// Godot <c>Global.update_hangar(true)</c> plus <c>Global.save()</c>,
        /// which the two garage tools run once when they are done.
        /// </summary>
        public Action AccountChanged;

        public RectTransform Root { get; private set; }

        public InputField CreditsField
        {
            get { return _credits; }
        }

        public InputField GoldField
        {
            get { return _gold; }
        }

        public InputField FreeXpField
        {
            get { return _freeXp; }
        }

        public InputField PremHoursField
        {
            get { return _premHours; }
        }

        public RectTransform Build(RectTransform parent, float top)
        {
            Root = _kit.Node("account_toolbox", parent, 0f, top, Width, Height);

            var background = _kit.Picture("bg", Root, null, 0f, 0f, Width, Height);
            background.color = BackgroundColor;

            // The inner VBoxContainer's first child is a 5 px spacer, which is
            // why the title starts at y = 9 instead of at the top edge.
            var box = _kit.Node("VBoxContainer", Root, 0f, 0f, Width, InnerHeight);

            _kit.Label(
                "title",
                box,
                TitleFontSize,
                TextAnchor.MiddleCenter,
                _kit.Str("hangar:toolbox/title"),
                _kit.Regular,
                UiTheme.Label,
                TitleRect.x,
                TitleRect.y,
                TitleRect.width,
                TitleRect.height);

            var divider = _kit.Picture(
                "ColorRect", box, null,
                DividerRect.x, DividerRect.y, DividerRect.width, DividerRect.height);
            divider.color = Color.white;

            Row(box, "btn_erase_rewards", "hangar:toolbox/erase_rewards",
                EraseRewardsRect, EraseRewards);
            Row(box, "btn_erase_battles", "hangar:toolbox/erase_battles",
                EraseBattlesRect, EraseBattles);

            _credits = FieldRow(
                box, "set_credits", "hangar:toolbox/set_credits", 0, SetCredits);
            _gold = FieldRow(box, "set_gold", "hangar:toolbox/set_gold", 1, SetGold);
            _freeXp = FieldRow(
                box, "set_freexp", "hangar:toolbox/set_freexp", 2, SetFreeXp);
            _premHours = FieldRow(
                box, "set_prem", "hangar:toolbox/add_prem_hours", 3, AddPremHours);

            var unlockTop = FirstRowTop + (4 * RowPitch);
            var unlock = _kit.Node("unlock_all", box, 0f, unlockTop, Width, RowHeight);
            Row(unlock, "btn_unlock_tanks", "hangar:toolbox/unlock_all_tanks",
                new Rect(0f, 0f, RowButtonWidth, RowHeight), UnlockAllTanks);
            Row(unlock, "btn_full_upgrades", "hangar:toolbox/full_upgrades",
                new Rect(RowFieldLeft, 0f, RowFieldWidth, RowHeight),
                FullUpgradeAllTanks);

            return Root;
        }

        // ------------------------------------------------------------------
        // ui_account_toolbox.gd behaviour.
        // ------------------------------------------------------------------

        public void EraseRewards()
        {
            _account.Rewards.Clear();
            Rebuilt();
        }

        public void EraseBattles()
        {
            _account.BattleResults.Clear();
            Rebuilt();
            Debug.Log("[Blitz] account toolbox: erased current player's battles");
        }

        public void SetCredits()
        {
            SetCredits(ParseAmount(_credits == null ? null : _credits.text));
        }

        public void SetCredits(long amount)
        {
            _account.Credits = amount;
            Rebuilt();
        }

        public void SetGold()
        {
            SetGold(ParseAmount(_gold == null ? null : _gold.text));
        }

        public void SetGold(long amount)
        {
            _account.Gold = amount;
            Rebuilt();
        }

        public void SetFreeXp()
        {
            SetFreeXp(ParseAmount(_freeXp == null ? null : _freeXp.text));
        }

        public void SetFreeXp(long amount)
        {
            _account.FreeXp = amount;
            Rebuilt();
        }

        public void AddPremHours()
        {
            // Premium hours stay 32-bit; an entry past that range saturates.
            var hours = ParseAmount(_premHours == null ? null : _premHours.text);
            AddPremHours((int)System.Math.Max(int.MinValue, System.Math.Min(int.MaxValue, hours)));
        }

        public void AddPremHours(int hours)
        {
            _account.AddPremAccount(hours, 0d, _clock.NowUnixSeconds);
            Rebuilt();
        }

        /// <summary>
        /// Godot <c>unlock_all_tanks()</c>. <c>Account.add_tank</c> is
        /// deliberately not used: it rebuilds and notifies the hangar once per
        /// tank, so the whole database would mean hundreds of rebuilds. One
        /// save entry per card with the bought flag set is what it boils down
        /// to, followed by a single cleanup, refresh and save.
        /// </summary>
        public void UnlockAllTanks()
        {
            if (_tanks == null || _research == null)
            {
                Debug.LogError("[Blitz] account toolbox: no tank database to unlock");
                return;
            }

            var added = 0;
            var bought = 0;
            for (var i = 0; i < _tanks.Tanks.Count; i++)
            {
                var name = _tanks.Tanks[i].Name;
                // A hidden card (an event's own vehicle) never reaches the carousel.
                if (_tanks.Tanks[i].Hidden || string.Equals(name, TankSaveData.DefaultName, StringComparison.Ordinal))
                {
                    continue;
                }

                var save = _account.GetTank(name);
                if (save != null)
                {
                    if (save.Bought)
                    {
                        continue;
                    }

                    save.Bought = true;
                    bought++;
                    continue;
                }

                save = TankSaveData.WithName(name);
                save.Bought = true;
                _account.Tanks.Add(save);
                added++;
            }

            // Does what logging in does: drops cards the database no longer
            // knows about, clamps the module ids of the entries just created
            // and marks their stock modules as researched.
            _research.CleanupAccount();

            Rebuilt();
            Debug.Log(
                "[Blitz] account toolbox: added " + added +
                " tanks, bought " + bought +
                " researched ones, " + _account.TanksBoughtCount +
                " in the hangar now");
        }

        /// <summary>
        /// Godot <c>full_upgrade_all_tanks()</c>: top gun, engine and tracks on
        /// everything the account owns, with the whole module tree marked as
        /// researched so the hangar shows the modules as owned.
        /// </summary>
        public void FullUpgradeAllTanks()
        {
            if (_tanks == null || _research == null)
            {
                Debug.LogError("[Blitz] account toolbox: no tank database to upgrade");
                return;
            }

            var upgraded = 0;
            for (var i = 0; i < _account.Tanks.Count; i++)
            {
                var save = _account.Tanks[i];
                var info = _tanks.Get(save.Name);
                if (info == null ||
                    string.Equals(info.Name, TankSaveData.DefaultName, StringComparison.Ordinal))
                {
                    continue;
                }

                MarkResearched(info.Guns);
                MarkResearched(info.Engines);
                MarkResearched(info.Tracks);
                MarkResearched(info.Turrets);

                var engineId = TankResearchService.TopModuleId(info.Engines);
                var trackId = TankResearchService.TopModuleId(info.Tracks);
                var turretId = TankResearchService.TopModuleId(info.Turrets);
                var gunId = TankResearchService.TopModuleId(info.Guns);
                if (turretId >= 0)
                {
                    save.TurretId = turretId;
                    // The client keeps the gun list inside the turret, so the
                    // top gun is the top gun *of the mounted turret*.
                    gunId = info.GunIdFor(turretId, gunId);
                }

                if (engineId >= 0)
                {
                    save.EngineId = engineId;
                }

                if (trackId >= 0)
                {
                    save.TrackId = trackId;
                }

                if (gunId >= 0)
                {
                    var gunChanged = save.GunId != gunId;
                    save.GunId = gunId;

                    // Installing a gun clears the shell slots, so an upgraded
                    // tank would otherwise roll out with an empty ammo rack.
                    // The rack is only refilled when the gun actually changed or
                    // the rack no longer matches it, to leave a hand-picked
                    // loadout alone. Godot compares array sizes; the port's rack
                    // is a fixed three slots, so the same check is whether the
                    // rack is empty or carries rounds in a slot this gun has no
                    // shell for.
                    var gun = TankResearchService.UniqueModules(info.Guns)[gunId] as GunInfo;
                    if (info.Turrets.Count > 0)
                    {
                        // The rack size is authored per turret, so the fill has
                        // to use the barrel as this turret mounts it.
                        gun = info.GunFor(save.TurretId, gunId);
                    }
                    if (gunChanged || !RackMatchesGun(save, gun))
                    {
                        FillAmmoRack(save, gun);
                    }
                }

                upgraded++;
            }

            Rebuilt();
            Debug.Log("[Blitz] account toolbox: fully upgraded " + upgraded + " tanks");
        }

        /// <summary>
        /// Godot's <c>int(text)</c> cast: leading sign and digits, 0 for
        /// anything else, and the fractional part of "12.7" dropped.
        /// </summary>
        public static long ParseAmount(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            var index = 0;
            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            var negative = index < text.Length && (text[index] == '-' || text[index] == '+');
            var sign = index < text.Length && text[index] == '-' ? -1L : 1L;
            if (negative)
            {
                index++;
            }

            var digits = 0L;
            var any = false;
            while (index < text.Length && text[index] >= '0' && text[index] <= '9')
            {
                any = true;
                var digit = text[index] - '0';
                if (digits > (long.MaxValue - digit) / 10L)
                {
                    // Currencies are 64-bit like Godot's ints; an entry past
                    // the long range saturates instead of wrapping around
                    // into the opposite sign.
                    return sign < 0 ? long.MinValue : long.MaxValue;
                }

                digits = (digits * 10L) + digit;
                index++;
            }

            return any ? sign * digits : 0L;
        }

        /// <summary>
        /// Godot <c>_full_ammo_rack</c>: the gun's capacity spread across its
        /// shell types, remainder going to the primary shell.
        /// </summary>
        public static List<int> FullAmmoRack(GunInfo gun, int slots)
        {
            var rack = new List<int>();
            for (var i = 0; i < slots; i++)
            {
                rack.Add(0);
            }

            if (gun == null || gun.Shells.Count <= 0 || slots <= 0)
            {
                return rack;
            }

            var types = Math.Min(gun.Shells.Count, slots);
            var each = gun.MaxAmmo / types;
            for (var i = 0; i < types; i++)
            {
                rack[i] = each;
            }

            rack[0] += gun.MaxAmmo - (each * types);
            return rack;
        }

        // ------------------------------------------------------------------
        // Internals.
        // ------------------------------------------------------------------

        private void Refresh()
        {
            if (CurrenciesChanged != null)
            {
                CurrenciesChanged();
            }
        }

        private void Rebuilt()
        {
            if (AccountChanged != null)
            {
                AccountChanged();
                return;
            }

            // Unit callers and isolated tools still get a visible currency refresh.
            Refresh();
        }

        private void MarkResearched<T>(List<T> modules) where T : ModuleInfo
        {
            for (var i = 0; i < modules.Count; i++)
            {
                _research.MarkModuleResearched(modules[i].Name);
            }
        }

        private static bool RackMatchesGun(TankSaveData save, GunInfo gun)
        {
            if (gun == null)
            {
                return true;
            }

            var loaded = 0;
            for (var i = 0; i < save.ShellsAmounts.Count; i++)
            {
                if (save.ShellsAmounts[i] <= 0)
                {
                    continue;
                }

                if (i >= gun.Shells.Count)
                {
                    return false;
                }

                loaded += save.ShellsAmounts[i];
            }

            return loaded > 0;
        }

        private static void FillAmmoRack(TankSaveData save, GunInfo gun)
        {
            var rack = FullAmmoRack(gun, save.ShellsAmounts.Count);
            for (var i = 0; i < save.ShellsAmounts.Count; i++)
            {
                save.ShellsAmounts[i] = rack[i];
            }
        }

        private Button Row(
            RectTransform parent, string name, string key, Rect rect, Action action)
        {
            var button = _kit.StyleButton(
                name, parent, "button_generic", rect.x, rect.y, rect.width, rect.height);
            _kit.Label(
                "label",
                (RectTransform)button.transform,
                ButtonFontSize,
                TextAnchor.MiddleCenter,
                _kit.Str(key),
                _kit.Bold,
                UiTheme.ButtonFont,
                0f,
                0f,
                rect.width,
                rect.height);
            _kit.Bind(button, action, AudioService.SoundEnter);
            return button;
        }

        private InputField FieldRow(
            RectTransform parent, string name, string key, int index, Action action)
        {
            var top = FirstRowTop + (index * RowPitch);
            var row = _kit.Node(name, parent, 0f, top, Width, RowHeight);
            Row(row, "btn_" + name, key,
                new Rect(0f, 0f, RowButtonWidth, RowHeight), action);

            var field = UiFactory.Field(
                "tedit_" + name, row, _kit.Regular, "0", false, 18);
            HangarUiKit.Place(
                field.GetComponent<RectTransform>(),
                RowFieldLeft, 0f, RowFieldWidth, RowHeight);

            var background = field.GetComponent<Image>();
            if (background != null)
            {
                background.color = FieldBackground;
            }

            Restyle(field.placeholder as Text);
            Restyle(field.textComponent);
            field.text = "0";
            return field;
        }

        private static void Restyle(Text label)
        {
            if (label != null)
            {
                label.fontSize = FieldFontSize;
            }
        }
    }
}
