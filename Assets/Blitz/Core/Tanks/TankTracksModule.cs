using System;

namespace BlitzLegacy.Core.Tanks
{
    public enum TrackSide { Left, Right }

    /// <summary>Independent chassis HP/repair timers behind the original one-row damage-panel API.
    /// Generic tracks damage/repair addresses both sides, preserving existing scripts/consumables.
    /// The facade exposes the worse state/minimum HP; it never becomes a third damageable pool.</summary>
    public sealed class TankTracksModule : TankModule
    {
        public TankModule Left { get; }
        public TankModule Right { get; }
        private int _batchDepth;
        private bool _damaged, _crit, _repaired;
        public TankTracksModule(int health, Func<float> repairTime, Func<bool> ownerDead)
            : base(TankModuleIds.Tracks, health, repairTime, ownerDead, false)
        {
            Left = new TankModule(TankModuleIds.TrackLeft, health, repairTime, ownerDead);
            Right = new TankModule(TankModuleIds.TrackRight, health, repairTime, ownerDead);
            Bind(Left); Bind(Right);
        }
        public TankModule Side(TrackSide side) => side == TrackSide.Left ? Left : Right;
        public override float RegenHealth
        {
            get => base.RegenHealth;
            set { base.RegenHealth = value; if (Left != null) Left.RegenHealth = value; if (Right != null) Right.RegenHealth = value; }
        }
        public override Status GetStatus(bool oneTimer = false) => (Status)Math.Max((int)Left.GetStatus(oneTimer), (int)Right.GetStatus(oneTimer));
        public override bool RepairPending => Left.RepairPending || Right.RepairPending;
        public override void ScheduleRepair(float now) { Left.ScheduleRepair(now); Right.ScheduleRepair(now); }
        public override void ReceiveDamage(int dmg, float now)
        { BeginBatch(); try { Left.ReceiveDamage(dmg, now); Right.ReceiveDamage(dmg, now); } finally { EndBatch(); } }
        public override void Tick(float now)
        { BeginBatch(); try { Left.Tick(now); Right.Tick(now); } finally { EndBatch(); } }
        public override bool Repair(bool half = false)
        {
            BeginBatch();
            try { var left = Left.Repair(half); var right = Right.Repair(half); return left || right; }
            finally { EndBatch(); }
        }
        private void Bind(TankModule side)
        {
            side.HealthComponent.HealthChanged += _ => SyncFacade();
            side.OnDamaged += _ => { SyncFacade(); if (_batchDepth > 0) _damaged = true; else RaiseDamaged(); };
            side.OnCrit += _ => { SyncFacade(); if (_batchDepth > 0) _crit = true; else RaiseCrit(); };
            side.OnRepaired += _ => { SyncFacade(); if (_batchDepth > 0) _repaired = true; else RaiseRepaired(); };
        }
        // HealthComponent is the legacy read snapshot. Mutations go through module methods/side modules.
        private void SyncFacade() { HealthComponent.Health = Math.Min(Left.HealthComponent.Health, Right.HealthComponent.Health); }
        private void BeginBatch() { _batchDepth++; }
        private void EndBatch()
        {
            if (--_batchDepth != 0) return;
            SyncFacade();
            var damaged = _damaged; var crit = _crit; var repaired = _repaired;
            _damaged = _crit = _repaired = false;
            if (damaged) RaiseDamaged(); if (crit) RaiseCrit(); if (repaired) RaiseRepaired();
        }
    }
}
