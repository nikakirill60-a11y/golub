using BlitzLegacy.Core.Tanks.Ai;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle.Ai
{
    /// <summary>
    /// The fight is lost where it stands: too many guns, or one the bot
    /// cannot win against. Fall back into cover - in reverse, so the front
    /// plate stays on the guns, and shooting back all the way - then hold
    /// there and only peek out, from where the last shot went, while the gun
    /// is loaded (and, for a crew that tracks reloads, while the enemy's is
    /// not). With no cover near, give ground towards the own spawn.
    /// </summary>
    public sealed class AiStateWithdraw : AiStateCombat
    {
        public const float CoverReach = 55f;
        public const float CoverRefresh = 2f;
        public const float RetreatRefresh = 1.5f;
        public const float RetreatDistance = 40f;
        public const float PeekSeconds = 3f;
        public const float PeekCooldown = 2f;
        public const float PatientPeekSeconds = 6f;

        private Vector3 _cover;
        private float _coverAt = -1f;
        private Vector3 _retreat;
        private float _retreatAt = -1f;
        private Vector3 _peekSpot;
        private bool _peeking;
        private float _peekUntil = -1f;
        private float _peekReadyAt;
        private float _inCoverSince = -1f;
        private float _lastShot = float.NaN;

        /// <summary>The cover the bot falls back to; zero when there is none.</summary>
        public Vector3 Cover
        {
            get { return _cover; }
        }

        public bool Peeking
        {
            get { return _peeking; }
        }

        /// <summary>A hull falling back never parks in the open to shoot.</summary>
        protected override bool HaltsToShoot
        {
            get { return false; }
        }

        public override void Enter(AiTankController ai)
        {
            base.Enter(ai);
            _peekSpot = ai != null ? ai.Pos() : Vector3.zero;
            _cover = Vector3.zero;
            _coverAt = -1f;
            _retreat = Vector3.zero;
            _retreatAt = -1f;
            _peeking = false;
            _peekUntil = -1f;
            _peekReadyAt = Time.time + 1.5f;
            _inCoverSince = -1f;
            var tank = ai != null ? ai.Tank : null;
            _lastShot = tank != null && tank.Gun != null ? tank.Gun.LastShot : float.NaN;
        }

        protected override void Think()
        {
            base.Think();
            var tank = Tank();
            if (tank == null)
            {
                return;
            }

            var now = Time.time;
            var picture = Ai.Engagement;

            // Shoot back at the gun that hurts most, not the one the rating liked.
            var worst = picture.MostDangerous;
            if (worst != null && worst != Ai.TargetTank && Ai.TankIsValid(worst)
                && Ai.SkillReached(AiSkillLevel.GreenArmy))
            {
                Ai.TargetTank = worst;
                LastAimPoint = null;
            }

            var threat = picture.Spotted > 0 && picture.ThreatPosition != Vector3.zero
                ? picture.ThreatPosition
                : Ai.LastThreatPosition;
            if (threat == Vector3.zero)
            {
                Ai.Drive(0f, 0f);
                return;
            }

            if (tank.Gun != null && !Mathf.Approximately(tank.Gun.LastShot, _lastShot))
            {
                // That spot had a line on the enemy: the next peek goes there.
                _lastShot = tank.Gun.LastShot;
                _peekSpot = tank.Pos();
                if (_peeking)
                {
                    EndPeek(now);
                }
            }

            if (now >= _coverAt)
            {
                _cover = Ai.FindCoverFrom(threat, CoverReach);
                _coverAt = now + CoverRefresh;
            }

            if (_peeking)
            {
                if (now < _peekUntil && Ai.CanShoot())
                {
                    Ai.NoteDriveIntent(false);
                    Ai.DriveToPos(_peekSpot, true, true);
                    return;
                }

                EndPeek(now);
            }

            Ai.NoteDriveIntent(true);
            if (_cover == Vector3.zero)
            {
                GiveGround(threat, now);
                return;
            }

            var reach = tank.HullLength * 0.75f;
            if ((_cover - tank.Pos()).sqrMagnitude > reach * reach)
            {
                _inCoverSince = -1f;
                Ai.DriveToPos(_cover, true, true);
                return;
            }

            if (_inCoverSince < 0f)
            {
                _inCoverSince = now;
            }

            if (ShouldPeek(now, picture))
            {
                _peeking = true;
                _peekUntil = now + PeekSeconds;
                Ai.NoteDriveIntent(false);
                Ai.DriveToPos(_peekSpot, true, true);
                return;
            }

            Ai.HoldFacing(null, threat, 0f);
        }

        /// <summary>No cover near: back away towards the team, nose on the guns.</summary>
        private void GiveGround(Vector3 threat, float now)
        {
            if (now >= _retreatAt)
            {
                _retreat = Ai.RetreatPoint(threat, RetreatDistance);
                _retreatAt = now + RetreatRefresh;
            }

            if (_retreat != Vector3.zero)
            {
                Ai.DriveToPos(_retreat, true, true);
            }
            else
            {
                Ai.HoldFacing(null, threat, 0f, -1f);
            }
        }

        private bool ShouldPeek(float now, AiEngagementPicture picture)
        {
            if (!Ai.CanShoot() || now < _peekReadyAt || _peekSpot == Vector3.zero)
            {
                return false;
            }

            if ((_peekSpot - Ai.Pos()).sqrMagnitude < 4f || Ai.HealthCoeff() < 0.15f)
            {
                return false;
            }

            // A Big Boss intoxicator fights from cover: out as soon as the gun
            // is loaded, back in to reload.
            if (Ai.CoverFighter)
            {
                return true;
            }

            // A crew below green army does not track reloads, but it still
            // looks out after a while instead of hiding for good.
            var inCover = _inCoverSince >= 0f ? now - _inCoverSince : 0f;
            if (!Ai.SkillReached(AiSkillLevel.GreenArmy))
            {
                return inCover > PatientPeekSeconds * 1.5f;
            }

            // A crew that tracks reloads waits for the enemy gun to fire, but
            // not forever.
            var patient = inCover > PatientPeekSeconds;
            return patient
                || !picture.HasTarget
                || picture.TargetReload < 0f
                || picture.TargetReload >= 1.5f;
        }

        private void EndPeek(float now)
        {
            _peeking = false;
            _peekReadyAt = now + PeekCooldown;
        }
    }
}