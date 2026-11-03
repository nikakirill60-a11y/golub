using System;
using BlitzLegacy.Core.Hangar;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Runtime.Hangar.Halloween;

namespace BlitzLegacy.Runtime.Hangar
{
    /// <summary>
    /// What a hangar choice is judged against: the clock, the account's premium and whether this device
    /// can draw the ported hangars. The hangar screen and the settings screen resolve the shown hangar
    /// through the same instance type, so the name in the settings is always the hangar behind them.
    /// </summary>
    public sealed class HangarChoiceContext
    {
        private readonly Func<double> _now;
        private readonly Func<bool> _premium;
        private readonly Func<bool> _portedDrawable;

        public HangarChoiceContext(Func<double> now, Func<bool> premium, Func<bool> portedDrawable = null)
        {
            _now = now ?? throw new ArgumentNullException("now");
            _premium = premium ?? throw new ArgumentNullException("premium");
            _portedDrawable = portedDrawable ?? (() => HalloweenHangarScene.CanRender);
        }

        public double NowUnixSeconds
        {
            get { return _now(); }
        }

        public bool Premium
        {
            get { return _premium(); }
        }

        public bool PortedDrawable
        {
            get { return _portedDrawable(); }
        }

        /// <summary>The hangar shown for <paramref name="settings"/> right now.</summary>
        public HangarKind Current(SettingsData settings)
        {
            return HangarSelection.ResolveLocal(
                settings == null ? null : settings.Hangar, NowUnixSeconds, Premium, PortedDrawable);
        }

        /// <summary>Whether the picker lets the player choose <paramref name="info"/>.</summary>
        public bool IsAvailable(HangarInfo info)
        {
            return HangarSelection.IsAvailable(info, Premium, PortedDrawable);
        }

        /// <summary>The signed-in account of the running client; no account means no premium.</summary>
        public static HangarChoiceContext Live()
        {
            return new HangarChoiceContext(
                () =>
                {
                    var bootstrap = BlitzBootstrap.Current;
                    return bootstrap != null && bootstrap.Clock != null
                        ? bootstrap.Clock.NowUnixSeconds
                        : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;
                },
                () =>
                {
                    var bootstrap = BlitzBootstrap.Current;
                    var account = bootstrap == null || bootstrap.Accounts == null
                        ? null
                        : bootstrap.Accounts.GetCurrentAccount();
                    return account != null && account.HasPremium(
                        bootstrap.Clock != null
                            ? bootstrap.Clock.NowUnixSeconds
                            : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d);
                });
        }
    }
}
