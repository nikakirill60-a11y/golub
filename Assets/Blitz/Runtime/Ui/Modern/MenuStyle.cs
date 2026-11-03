using System;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Ui;
using BlitzLegacy.Runtime.Assets;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// Runtime switch between the Classic and the Modern menus. The choice is
    /// the saved <see cref="SettingsData.UiStyle"/>, overridable with the
    /// BLITZ_UI_STYLE environment variable (smokes) or <see cref="ToolOverride"/>
    /// (editor render tools). The in-battle HUD never asks.
    /// </summary>
    public static class MenuStyle
    {
        /// <summary>Forced style for editor tools, or null to follow the settings.</summary>
        public static int? ToolOverride { get; set; }

        public static bool IsModern
        {
            get
            {
                if (ToolOverride.HasValue)
                {
                    return ToolOverride.Value == SettingsData.UiStyleModern;
                }

                var bootstrap = BlitzBootstrap.Current;
                return MenuStyleRules.IsModern(
                    bootstrap == null ? null : bootstrap.Settings,
                    Environment.GetEnvironmentVariable(MenuStyleRules.OverrideVariable));
            }
        }

        /// <summary>
        /// Modern, and drawn from the menus rather than from a running battle:
        /// screens shared with the battle (settings from the pause menu) keep
        /// their classic look there.
        /// </summary>
        public static bool InMenus
        {
            get
            {
                var bootstrap = BlitzBootstrap.Current;
                return IsModern && (bootstrap == null || bootstrap.Battle == null);
            }
        }

        /// <summary>Screen aspect forced by render tools, or null for the real screen.</summary>
        public static float? AspectOverride { get; set; }

        /// <summary>Width over height of the screen the menus are laid out on.</summary>
        public static float ScreenAspect
        {
            get
            {
                if (AspectOverride.HasValue)
                {
                    return AspectOverride.Value;
                }

                return UnityEngine.Screen.height > 0
                    ? UnityEngine.Screen.width / (float)UnityEngine.Screen.height
                    : 0f;
            }
        }

        /// <summary>Whether the first-run style picker is due.</summary>
        public static bool NeedsPicker
        {
            get
            {
                if (ToolOverride.HasValue)
                {
                    return false;
                }

                if (MenuStyleRules.ParseOverride(Environment.GetEnvironmentVariable(
                    MenuStyleRules.OverrideVariable)) != SettingsData.UiStyleUnset)
                {
                    return false;
                }

                var bootstrap = BlitzBootstrap.Current;
                return bootstrap != null && MenuStyleRules.NeedsPicker(bootstrap.Settings);
            }
        }

        /// <summary>Puts the Modern chrome on <paramref name="atlas"/>, or takes it off.</summary>
        public static AtlasRegionTable Apply(AtlasRegionTable atlas, bool modern)
        {
            if (atlas != null)
            {
                atlas.Skin = modern ? ModernSkin.Instance : null;
            }

            return atlas;
        }

        /// <summary><see cref="Apply(AtlasRegionTable,bool)"/> for a menu-only screen.</summary>
        public static AtlasRegionTable ApplyForMenus(AtlasRegionTable atlas)
        {
            return Apply(atlas, InMenus);
        }
    }
}
