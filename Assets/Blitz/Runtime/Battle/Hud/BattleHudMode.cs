using System;
using System.Collections.Generic;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// What a battle mode adds to the standard battle HUD. Every mode shows the same HUD:
    /// header, plates, readouts, the shell rack with the three hangar consumable slots,
    /// minimap, chat, scoreboard and pause menu. A mode registers one of these on each of
    /// its crews (<see cref="BattleTank.HudMode"/>) and only answers for its own parts; a
    /// tank without one plays the standard battle, <see cref="Standard"/>.
    /// </summary>
    public class BattleHudMode
    {
        /// <summary>
        /// The standard battle: no mode slot, an empty status row, no return after a death
        /// and one header life per tank.
        /// </summary>
        public static readonly BattleHudMode Standard = new BattleHudMode();

        /// <summary>The mode registered for this crew, the standard battle without one.</summary>
        public static BattleHudMode Of(BattleTank tank)
        {
            return tank != null && tank.HudMode != null ? tank.HudMode : Standard;
        }

        /// <summary>
        /// The ability of the fourth consumable slot (cons_jump, Digit7); null leaves the slot
        /// unbuilt, as the source authors it hidden.
        /// </summary>
        public virtual IBattleModeAbility Ability { get { return null; } }

        /// <summary>
        /// Adds this frame's tiles of the ability status row above the health bar, left to
        /// right. The row only asks while the battle runs and the crew is alive.
        /// </summary>
        public virtual void CollectStatuses(List<BattleStatusTile> tiles) { }

        /// <summary>Face of the status row's countdowns; null keeps the HUD's bold font.</summary>
        public virtual Font StatusFont { get { return null; } }

        /// <summary>
        /// The crew comes back after a death, so the spectator strip stays hidden until
        /// <see cref="CanSpectate"/> allows it.
        /// </summary>
        public virtual bool Respawns { get { return false; } }

        /// <summary>
        /// A dead crew may watch its team now: always in the standard battle, only once no
        /// return is left in a mode that <see cref="Respawns"/>.
        /// </summary>
        public virtual bool CanSpectate { get { return true; } }

        /// <summary>
        /// A mode screen owns the pointer (the Uprising perk offer): the controls drive, look
        /// and fire nothing and only the pause menu stays available above it.
        /// </summary>
        public virtual bool ModalOpen { get { return false; } }

        /// <summary>
        /// Lives the header counts for this crew, its team score and when its icon blackens;
        /// -1 counts one per tank still alive, as the standard battle does.
        /// </summary>
        public virtual int HeaderLives { get { return -1; } }

        /// <summary>
        /// Calls <paramref name="changed"/> whenever <see cref="HeaderLives"/> rises again with
        /// no death to redraw the header (a respawn).
        /// </summary>
        public virtual void WatchHeader(Action changed) { }

        /// <summary>Stops calling <paramref name="changed"/> from <see cref="WatchHeader"/>.</summary>
        public virtual void UnwatchHeader(Action changed) { }

        /// <summary>
        /// Adds the mode's own widgets on top of the standard HUD once it is built, under
        /// <paramref name="hudRoot"/> so they hide with it; the standard battle adds none.
        /// Called once, for the player's crew, before the mode deals its roles, so the
        /// widgets read the mode state lazily.
        /// </summary>
        public virtual void CreateWidgets(BattleSession session, RectTransform hudRoot) { }
    }
}
