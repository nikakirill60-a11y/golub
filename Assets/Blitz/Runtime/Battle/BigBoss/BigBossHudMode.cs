using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Runtime.Ui;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// What Big Boss adds to the standard battle HUD: the role's button ability in the mode
    /// slot, and its self-activating abilities, running effects and a received shield or
    /// poison in the status row. Roles are assigned after the HUD is built and change with
    /// every life and level, so both read the current role each tick. Every crew returns
    /// after a death but an exhausted boss.
    /// </summary>
    public sealed class BigBossHudMode : BattleHudMode, IBattleModeAbility
    {
        /// <summary>BossmodeAbility draws the 256 px source art at 128 px.</summary>
        public const float AbilityIconSize = 128f;

        /// <summary>Status-row ids that are not abilities of the local role.</summary>
        public const string ShieldStatus = "status:shield", PoisonStatus = "status:poisoned";

        public const string StatusFontResource = "Fonts/WarHeliosCondCBold";

        private const string ShieldIcon = "BigBoss/Textures/consumable_energy-shield_xs";
        private const string PoisonIcon = "BigBoss/Textures/consumable_poisoned-shells_xs";

        private readonly BigBossTank _owner;
        private readonly Dictionary<string, string> _smallIcons = new Dictionary<string, string>(StringComparer.Ordinal);
        private string _id, _family, _icon;
        private Font _font;

        public BigBossHudMode(BigBossTank owner)
        {
            _owner = owner;
        }

        public BigBossTank Owner { get { return _owner; } }

        public override IBattleModeAbility Ability { get { return this; } }

        public override bool Respawns { get { return true; } }

        /// <summary>
        /// Ordinary roles always return and the boss returns until its lives are spent, so
        /// only an exhausted boss watches the team.
        /// </summary>
        public override bool CanSpectate
        {
            get { var state = _owner.State; return state != null && state.Exhausted; }
        }

        /// <summary>The role panel, the boss lives and the next role; a dedicated server shows
        /// nobody them.</summary>
        public override void CreateWidgets(BattleSession session, RectTransform hudRoot)
        {
            if (session.Dedicated) { return; }
            BigBossBattleUi.Create(hudRoot, _owner);
        }

        /// <summary>Profile art names sit under BigBoss/Textures unless they carry a path.</summary>
        public static string Art(string name)
        {
            return name.IndexOf('/') >= 0 ? name : "BigBoss/Textures/" + name;
        }

        public BattleModeAbilityState Read(float now)
        {
            var id = _owner.ManualAbility;
            if (id == null) { return default(BattleModeAbilityState); }

            var state = _owner.State;
            if (id != _id)
            {
                _id = id;
                var ability = state.Rules.Ability(id);
                _family = ability["family"].AsString();
                _icon = Art(ability["iconBig"].AsString());
            }
            var running = _owner.RunningSeconds(_family);
            var active = running > 0f || (_family == "shieldGiverAbility" && _owner.ShieldArmed);
            var cooldown = (float)state.Remaining(id, now);
            return new BattleModeAbilityState
            {
                Phase = active ? BattleModeAbilityPhase.Active
                    : cooldown > 0f ? BattleModeAbilityPhase.Cooling
                    : BattleModeAbilityPhase.Ready,
                Icon = _icon,
                IconSize = new Vector2(AbilityIconSize, AbilityIconSize),
                Seconds = running > 0f ? running : cooldown,
            };
        }

        public bool Use()
        {
            return _owner.UseRoleAbility();
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
        /// The role's self-activating abilities with their cooldown and any ability while its
        /// timed effect runs, then a received energy shield and poison.
        /// </summary>
        public override void CollectStatuses(List<BattleStatusTile> tiles)
        {
            var state = _owner.State;
            if (state == null || _owner.Tank.Dead) { return; }

            var now = Time.time;
            var rules = state.Rules;
            var abilities = rules.Abilities(state.RoleId, state.Level);
            for (var i = 0; i < abilities.Length; i++)
            {
                var id = abilities[i];
                var ability = rules.Ability(id);
                if (ability["kind"].AsString() != "active") { continue; }

                var family = ability["family"].AsString();
                var run = _owner.RunningSeconds(family);
                if (!state.AutoActivated(id) && run <= 0f) { continue; }

                var armed = (family == "railgunCharger" && _owner.RailgunArmed)
                    || (family == "shieldGiverAbility" && _owner.ShieldArmed);
                var tile = new BattleStatusTile { Id = id, Icon = SmallIcon(id, ability), Running = run > 0f || armed, Armed = armed };
                if (run > 0f)
                {
                    tile.Seconds = run;
                    tile.Total = _owner.RunningTotal(family);
                }
                else
                {
                    tile.Seconds = armed ? 0f : (float)state.Remaining(id, now);
                    tile.Total = Mathf.Max(rules.Number(id, "cooldown", 1), rules.Number(id, "startCooldown"));
                }
                tiles.Add(tile);
            }

            if (_owner.ShieldUntil > now)
            {
                tiles.Add(new BattleStatusTile
                {
                    Id = ShieldStatus, Icon = ShieldIcon, Running = true,
                    Seconds = Mathf.Max(0f, _owner.ShieldUntil - now), Total = _owner.ShieldDuration,
                });
            }
            if (_owner.Poisoned)
            {
                float total;
                var seconds = _owner.PoisonedSeconds(out total);
                tiles.Add(new BattleStatusTile
                {
                    Id = PoisonStatus, Icon = PoisonIcon, Running = true, Debuff = true,
                    Seconds = seconds, Total = total,
                });
            }
        }

        private string SmallIcon(string id, JsonNode ability)
        {
            string icon;
            if (!_smallIcons.TryGetValue(id, out icon))
            {
                icon = Art(ability["iconSmall"].AsString());
                _smallIcons[id] = icon;
            }
            return icon;
        }
    }
}
