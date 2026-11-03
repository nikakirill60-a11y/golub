using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Runtime.Ui;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// What Uprising adds to the standard battle HUD: its active in the mode slot, the owned
    /// abilities, the cloak and received marks in the status row, the perk offer that owns
    /// the pointer while the crew waits to return, and the lives in the header.
    /// </summary>
    public sealed class UprisingHudMode : BattleHudMode, IBattleModeAbility
    {
        public const string StatusFontResource = "Uprising/arialmb";

        /// <summary>Marks another crew puts on this tank.</summary>
        private static readonly string[] Received = { "huntersMark", "curse" };

        private readonly UprisingTank _owner;
        private readonly List<string> _statusIds = new List<string>();
        private readonly Dictionary<string, string> _icons = new Dictionary<string, string>(StringComparer.Ordinal);
        private string _activeId, _icon;
        private Font _font;

        public UprisingHudMode(UprisingTank owner)
        {
            _owner = owner;
        }

        public UprisingTank Owner { get { return _owner; } }

        public override IBattleModeAbility Ability { get { return this; } }

        public override bool Respawns { get { return true; } }

        /// <summary>Only the final death watches the team.</summary>
        public override bool CanSpectate
        {
            get { var state = _owner.State; return state != null && !state.Respawning && state.RespawnsLeft == 0; }
        }

        /// <summary>The perk offer of a pending return.</summary>
        public override bool ModalOpen
        {
            get { var state = _owner.State; return state != null && state.Respawning; }
        }

        /// <summary>
        /// uprising_component.gd remaining_lives(): the respawns still banked plus the life
        /// the tank is driving right now, if it is on the field.
        /// </summary>
        public override int HeaderLives
        {
            get
            {
                var state = _owner.State;
                if (state == null) { return -1; }

                var active = !_owner.Tank.Dead && !state.Respawning;
                return Mathf.Max(state.RespawnsLeft, 0) + (active ? 1 : 0);
            }
        }

        public override void WatchHeader(Action changed)
        {
            _owner.Respawned += changed;
        }

        public override void UnwatchHeader(Action changed)
        {
            _owner.Respawned -= changed;
        }

        /// <summary>The lives, the respawn timer and the perk offer.</summary>
        public override void CreateWidgets(BattleSession session, RectTransform hudRoot)
        {
            UprisingBattleUi.Create(hudRoot, _owner);
        }

        public BattleModeAbilityState Read(float now)
        {
            var state = _owner.State;
            if (state == null) { return default(BattleModeAbilityState); }

            if (state.ActiveId != _activeId)
            {
                _activeId = state.ActiveId;
                _icon = Icon(_activeId);
            }
            var running = state.Running(now);
            var left = (float)(running ? state.ActiveUntil - now : state.ReadyAt - now);
            return new BattleModeAbilityState
            {
                Phase = running ? BattleModeAbilityPhase.Active
                    : left > 0f ? BattleModeAbilityPhase.Cooling
                    : TargetMissing(state) ? BattleModeAbilityPhase.NoTarget
                    : BattleModeAbilityPhase.Ready,
                Icon = _icon,
                Seconds = Mathf.Max(0f, left),
            };
        }

        public bool Use()
        {
            return _owner.UseActive();
        }

        public override Font StatusFont
        {
            get
            {
                if (_font == null) { _font = Resources.Load<Font>(StatusFontResource); }
                return _font;
            }
        }

        /// <summary>
        /// The owned abilities (only the top level of a pair), sorted, then the cloak while
        /// it hides the tank and the marks other crews put on it.
        /// </summary>
        public override void CollectStatuses(List<BattleStatusTile> tiles)
        {
            var state = _owner.State;
            if (state == null) { return; }

            var now = Time.time;
            _statusIds.Clear();
            foreach (var id in state.Owned)
            {
                if (!id.EndsWith("1", StringComparison.Ordinal) || !state.Has(id.Substring(0, id.Length - 1) + "2")) { _statusIds.Add(id); }
            }
            _statusIds.Sort(StringComparer.Ordinal);
            if (state.CloakUntil > now) { _statusIds.Add("moonwalk"); }
            for (var i = 0; i < Received.Length; i++)
            {
                if (_owner.HasStatus(Received[i])) { _statusIds.Add(Received[i]); }
            }

            for (var i = 0; i < _statusIds.Count; i++)
            {
                var id = _statusIds[i];
                var debuff = Array.IndexOf(Received, id) >= 0;
                var running = debuff || id == "moonwalk";
                tiles.Add(new BattleStatusTile
                {
                    Id = id,
                    Icon = Icon(id),
                    Seconds = id == "moonwalk" ? Mathf.Max(0f, (float)state.CloakUntil - now)
                        : debuff ? _owner.StatusSeconds(id) : _owner.PassiveCooldown(id),
                    Total = state.Number(id, running ? "duration" : "cooldown", 10),
                    Running = running,
                    Debuff = debuff,
                    Spent = id == "comeBackKit" && state.LastStandUsed,
                });
            }
        }

        private string Icon(string id)
        {
            string icon;
            if (!_icons.TryGetValue(id, out icon))
            {
                icon = "Uprising/Icons/" + id;
                _icons[id] = icon;
            }
            return icon;
        }

        /// <summary>A targeted active with no live, spotted, visible enemy under the sight.</summary>
        private bool TargetMissing(UprisingState state)
        {
            if (!state.Catalog.Get(state.ActiveId)["targeted"].AsBool()) { return false; }

            var victim = _owner.Tank.GunTraceTank;
            if (victim == null || victim.Dead || victim.Team == _owner.Tank.Team || !victim.Spotted) { return true; }

            var other = victim.GetComponent<UprisingTank>();
            return other != null && other.Concealed;
        }
    }
}
