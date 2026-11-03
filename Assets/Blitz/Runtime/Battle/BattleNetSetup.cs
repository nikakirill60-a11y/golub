using System.Collections.Generic;
using BlitzLegacy.Core.Battle;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// How a networked battle is to be seated. The host passes its guests and
    /// gets <see cref="BattleSession.NetPlan"/> back; a client passes the plan
    /// it received from the host.
    /// </summary>
    public sealed class BattleNetSetup
    {
        public bool IsClient;

        /// <summary>Host-selected battle mode; clients use the mode carried by <see cref="Plan"/>.</summary>
        public BattleMode Mode = BattleMode.Normal;

        /// <summary>Fusion PlayerId of this peer.</summary>
        public int LocalOwner;

        /// <summary>This peer's public seat proof (see <c>BattleNetPlan.Slot.Seat</c>).</summary>
        public string LocalSeat = string.Empty;

        /// <summary>Client only: the host's plan.</summary>
        public BattleNetPlan Plan;

        /// <summary>Build id written into the plan (host).</summary>
        public string Build = string.Empty;

        /// <summary>Host only: the remote players to seat, in join order.</summary>
        public readonly List<Guest> Guests = new List<Guest>();

        /// <summary>
        /// Dedicated server: no human plays on this peer. The battle is seated
        /// by the host path around <see cref="Anchor"/> (it takes the slot a
        /// hosting player would, so map and bots follow its tank), but that
        /// slot is driven remotely like every other human; there are no local
        /// controls and no local account is touched.
        /// </summary>
        public bool Dedicated;

        /// <summary>
        /// Dedicated only: the human seated in the host slot. Its
        /// <see cref="Guest.Owner"/> and <see cref="Guest.Seat"/> are mirrored
        /// into <see cref="LocalOwner"/> / <see cref="LocalSeat"/>; it is not in
        /// <see cref="Guests"/>.
        /// </summary>
        public Guest Anchor;

        /// <summary>
        /// Dedicated: picks the first guest as the anchor and moves it out of
        /// <see cref="Guests"/>. Returns false with no guest to seat.
        /// </summary>
        public bool PromoteAnchor()
        {
            if (!Dedicated || Guests.Count == 0) { return false; }
            Anchor = Guests[0];
            Guests.RemoveAt(0);
            LocalOwner = Anchor.Owner;
            LocalSeat = Anchor.Seat ?? string.Empty;
            return Anchor.Hello != null;
        }

        public sealed class Guest
        {
            public int Owner;
            public string Seat = string.Empty;
            public BattleNetHello Hello;
        }
    }
}
