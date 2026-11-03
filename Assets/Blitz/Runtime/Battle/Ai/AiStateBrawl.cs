using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Tanks.Ai;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle.Ai
{
    /// <summary>
    /// Port of <c>ai_state_fight_brawl.gd</c> (<c>AIStateCombatBrawl</c>): keep
    /// the front towards the enemies and shuffle in and out of the fight.
    /// </summary>
    public sealed class AiStateBrawl : AiStateCombat
    {
        private float _side = 1f;

        public override void Enter(AiTankController ai)
        {
            base.Enter(ai);
            _side = Random.value < 0.5f ? -1f : 1f;
        }

        /// <summary>
        /// <c>belly_to_dir(dir)</c>: the front plate goes to the enemies. The
        /// port used to steer at a point behind the hull, which turned the
        /// rear - the thinnest plate - to the guns. The front is also angled
        /// when the enemy gun sits on the edge of penetrating it.
        /// </summary>
        private void BellyToDir(Vector3 dir)
        {
            var tank = Tank();
            if (tank == null)
            {
                return;
            }

            var angle = AiEngagementMath.FrontalAngle(Ai.Engagement.TargetFrontPenetration);
            Ai.FaceTowards(Ai.Pos() + (dir * tank.HullLength * 4f), angle * _side);
        }

        protected override void Think()
        {
            base.Think();
            if (!Ai.EnemyTargetIsValid())
            {
                return;
            }

            const float AttentionRadius = 200f;

            var enemies = Ai.GetTanksAroundPosTeam(Ai.Pos(), AttentionRadius, true);
            var dir = enemies >= 3
                ? Ai.DirToEnemies()
                : Ai.DirToPos2d(Ai.TargetTank.Pos());
            var danger = Ai.GetEnemiesDangerLevel(100f);

            if (TargetIsPrey() || danger <= 0)
            {
                Push();
                return;
            }

            var tank = Tank();
            var isTd = tank != null && tank.Card != null
                && tank.Card.Class == TankClass.TankDestroyer;
            if (LastAimPoint != null && isTd)
            {
                return;
            }

            BellyToDir(new Vector3(dir.x, 0f, dir.y));

            if (Ai.DelayElapsedSet("next_motion", Ai.ReactionTime() * 2f))
            {
                var forward = AiSkillTable.RandomChance(50f, Random.value) ? 1f : -1f;
                Ai.Drive(forward, tank != null ? tank.RightAxis : 0f);
            }

            if (Ai.DistSqrToEnemies() < 70f * 70f)
            {
                Retreat();
            }
        }
    }
}
