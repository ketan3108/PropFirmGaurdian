using System;
using PropFirmGuardian.Core;
using PropFirmGuardian.Models;

namespace PropFirmGuardian.Intelligence
{
    public sealed class ConsistencyRuleGuardian
    {
        private readonly AccountMonitor _accountMonitor;

        public ConsistencyRuleGuardian(AccountMonitor accountMonitor)
        {
            if (accountMonitor == null)
                throw new ArgumentNullException("accountMonitor");

            _accountMonitor = accountMonitor;
        }

        public event Action<string, string> OnConsistencyWarning;

        public void EvaluateClosedTrade(string accountName, TradeRecord trade, SessionStats stats)
        {
            if (trade == null || stats == null || stats.TotalPnL <= 0.0)
                return;

            AccountMonitor.AccountMonitorState state;
            if (!_accountMonitor.TryGetAccountState(accountName, out state) || state.Config == null)
                return;

            double threshold = state.Config.ConsistencyThreshold <= 0.0 ? 0.30 : state.Config.ConsistencyThreshold;
            double percent = Math.Abs(trade.RealizedPnL) / Math.Abs(stats.TotalPnL);
            lock (state.LockObject)
            {
                state.Snapshot.LargestTradePercent = Math.Max(state.Snapshot.LargestTradePercent, percent * 100.0);
                if (percent > threshold)
                {
                    state.Snapshot.LastKnownStatus = AccountState.Warning;
                    state.Snapshot.LockReason = "Consistency rule warning";
                    RaiseWarning(accountName, string.Format("Consistency rule warning: Last trade was {0:0}% of profit.", percent * 100.0));
                }
            }
        }

        private void RaiseWarning(string accountName, string message)
        {
            Action<string, string> handler = OnConsistencyWarning;
            if (handler != null)
                handler(accountName, message);
        }
    }
}
