using System;
using PropFirmGuardian.Core;
using PropFirmGuardian.Models;

namespace PropFirmGuardian.Intelligence
{
    public sealed class PaceKeeper
    {
        private readonly AccountMonitor _accountMonitor;

        public PaceKeeper(AccountMonitor accountMonitor)
        {
            if (accountMonitor == null)
                throw new ArgumentNullException("accountMonitor");

            _accountMonitor = accountMonitor;
        }

        public PaceDecision Evaluate(string accountName, SessionStats stats)
        {
            AccountMonitor.AccountMonitorState state;
            if (!_accountMonitor.TryGetAccountState(accountName, out state) || state.Config == null)
                return new PaceDecision(12, "Stable", "No monitored pace data.");

            int cap = Math.Max(3, state.Config.TradeCap);
            string velocity = "Stable";
            string message = string.Format("Pace: {0}/{1}.", stats != null ? stats.TradeCount : 0, cap);

            if (stats != null && stats.TradeCount >= cap && stats.WinRate > 0.60 && stats.ProfitFactor > 1.5)
            {
                cap += 3;
                velocity = "Improving";
                message = string.Format("Edge confirmed. Extended to {0} trades.", cap);
            }
            else if (stats != null && stats.TradeCount >= 8 && stats.WinRate < 0.40)
            {
                cap = Math.Min(cap, 10);
                velocity = "Declining";
                message = "Win rate declining. Protect session.";
            }

            return new PaceDecision(cap, velocity, message);
        }
    }

    public sealed class PaceDecision
    {
        public PaceDecision(int effectiveCap, string velocity, string message)
        {
            EffectiveCap = effectiveCap;
            Velocity = velocity;
            Message = message;
        }

        public int EffectiveCap { get; private set; }
        public string Velocity { get; private set; }
        public string Message { get; private set; }
    }
}
