using System.Collections;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Runtime.Ui;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    public sealed partial class BattleSession
    {
        /// <summary>
        /// Builds the one battle HUD every mode shows: controls, header and plates, the shell
        /// rack with the three hangar consumable slots, scoreboard, minimap, pause menu, chat
        /// and the sixth-sense lamp. The player's battle mode only adds its own widgets on top
        /// (<see cref="BattleHudMode.CreateWidgets"/>), here and nowhere else.
        /// </summary>
        private IEnumerator BuildHud(bool mobile, SettingsData settings, bool autoStart)
        {
            Controls = BattleControls.Create(Player, View, mobile);
            Controls.transform.SetParent(transform, false);
            if (Dedicated)
            {
                // Kept for the HUD wiring below, but it must never drive the
                // anchor tank: its BattleRemoteDriver does.
                Controls.SetInputActive(false);
                Controls.enabled = false;
            }
            Hud = BattleHud.Create(Player, View, mobile, MatchRoster.TeamSize);
            if (!autoStart)
            {
                // The build spans frames now, so the HUD would otherwise be
                // on screen for a moment while the map is still loading,
                // before the intro curtain goes up.
                Hud.SetVisible(false);
            }
            Hud.SetTeamRoster(_allies, _enemies);
            Hud.AttachShellSelector(Controls.Shells);
            for (var i = 0; i < _allies.Count; i++)
            {
                Hud.AddTankPlate(_allies[i]);
            }
            for (var i = 0; i < _enemies.Count; i++)
            {
                Hud.AddTankPlate(_enemies[i]);
            }

            // Both scoreboard lists live in the HUD tree, and setup calls
            // toggle_scoreboard() once before the battle starts.
            yield return null;
            Scoreboard = BattleScoreboard.Create(Hud.Root);
            Scoreboard.SetRoster(_participants, Player != null ? Player.Team : 0);
            Scoreboard.Toggle();
            Controls.ScoreboardRequested += Scoreboard.Toggle;

            // minimap_handle rides the same slide: _finish_setup_map_widgets
            // runs init_minimap() once the tanks are on the map.
            Minimap = BattleMinimap.Create(Hud.Root, World.Map, World.SidesSwapped);
            if (Minimap != null)
            {
                var board = Scoreboard;
                var view = View;
                Minimap.Playhead = () => board != null ? board.Playhead : 1f;
                Minimap.ViewForward = () => view != null && view.Active != null
                    ? view.Active.transform.forward
                    : (Player != null ? Player.transform.forward : Vector3.forward);
                Minimap.Tanks = BattleTanks;
                Minimap.Bind(Player, Roster != null ? Roster.PlayerTeam : 0, BattleTanks());
            }

            // menu_bg lives in the same HUD tree in the source, and
            // player_controller's "menu" action is what toggles it.
            PauseMenu = BattlePauseMenu.Create(transform, mobile);
            Controls.MenuRequested += ToggleMenu;

            // Battle chat lives in the HUD tree, so it hides with the HUD.
            Chat = BattleChatView.Create(Hud.Root, this, Player, mobile);
            if (Chat != null) { Controls.ChatRequested += Chat.Toggle; }
            // btn_settings opens ui_settings.tscn over the battle. There is no
            // hangar UI to hide here, so only the save at the end of the
            // screen's btn_menu chain is left.
            PauseMenu.SettingsPressed = () => OpenSettings(mobile, settings);

            // cycle_spectator walks Battle.get_player_team().
            Controls.PlayerTeam = PlayerTeamRoster;

            // Uprising: lives, respawn timer and perk offer; Big Boss: the role panel.
            BattleHudMode.Of(Player).CreateWidgets(this, Hud.Root);

            Lamp = BattleSpottedLamp.Create(Hud.Root);
            // _on_player_spotted and _show_spotted_lamp both give up on a
            // dead player tank.
            Lamp.CanShow = () => Player != null && Player.Health != null && !Player.Health.Dead;
            Lamp.SetCombatVisible(Hud.CombatVisible);
            if (Player != null)
            {
                Player.SpottedChanged += OnPlayerSpotted;
            }
        }
    }
}
