using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Tanks.Ai;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle.Ai
{
    /// <summary>
    /// Port of <c>ai_state_fight_single.gd</c> (<c>AIStateCombatSingle</c>):
    /// duel the chosen target, and circle it when the bot plays light.
    /// </summary>
    public sealed class AiStateSingle : AiStateCombat
    {
        protected override void Think()
        {
            base.Think();
            if (!Ai.EnemyTargetIsValid())
            {
                return;
            }

            var target = Ai.TargetTank;
            var tank = Tank();
            var isTd = tank != null && tank.Card != null
                && tank.Card.Class == TankClass.TankDestroyer;
            if (Ai.PlayStyleIs(AiPlayStyle.Light) && !isTd)
            {
                var dir = Ai.DirToPos2d(target.Pos());
                var past = target.Pos()
                    + (new Vector3(dir.x, 0f, dir.y) * target.HullLength);
                Ai.DriveToPos(past, true, true);
                return;
            }

            if (Ai.GetTankDangerLevel(target) > 2 || !Ai.CanShoot())
            {
                Retreat();
            }
            else
            {
                Push();
            }
        }
    }
}
