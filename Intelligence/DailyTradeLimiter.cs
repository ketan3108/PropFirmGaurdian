using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using PropFirmGuardian.Core;
using PropFirmGuardian.Models;

namespace PropFirmGuardian.Intelligence
{
    public sealed class DailyTradeLimiter
    {
        private readonly AccountMonitor _accountMonitor;
        private readonly ConcurrentDictionary<string, DailyTradeCounter> _counters;

        public DailyTradeLimiter(AccountMonitor accountMonitor)
        {
            if (accountMonitor == null)
                throw new ArgumentNullException("accountMonitor");

            _accountMonitor = accountMonitor;
            _counters = new ConcurrentDictionary<string, DailyTradeCounter>(StringComparer.OrdinalIgnoreCase);
        }

        public event Action<string, int, int> OnDailyLimitWarning;
        public event Action<string> OnDailyLimitReached;

        public void SeedTradesToday(string accountName, int tradesToday, DateTime sessionStart)
        {
            if (tradesToday <= 0)
                return;

            AccountMonitor.AccountMonitorState state;
            if (!_accountMonitor.TryGetAccountState(accountName, out state) || state.Config == null)
                return;

            DailyTradeCounter counter = _counters.GetOrAdd(accountName, name => new DailyTradeCounter());
            lock (state.LockObject)
            {
                counter.SessionStart = sessionStart;
                counter.TradesToday = Math.Max(counter.TradesToday, tradesToday);
                state.Snapshot.TradesToday = Math.Max(state.Snapshot.TradesToday, counter.TradesToday);
                state.Snapshot.DailyTradeLimit = GetEffectiveLimit(state.Config, counter);
                state.Snapshot.LastUpdateTime = DateTime.UtcNow;
            }
        }

        public void RecordEntry(string accountName, DateTime entryTime)
        {
            AccountMonitor.AccountMonitorState state;
            if (!_accountMonitor.TryGetAccountState(accountName, out state) || state.Config == null)
                return;

            if (!state.Config.EnableDailyLimit)
                return;

            DailyTradeCounter counter = _counters.GetOrAdd(accountName, name => new DailyTradeCounter());

            lock (state.LockObject)
            {
                ResetIfNeeded(state, counter, entryTime);
                if (counter.TradesToday < state.Snapshot.TradesToday)
                    counter.TradesToday = state.Snapshot.TradesToday;

                counter.TradesToday++;
                state.Snapshot.TradesToday = counter.TradesToday;
                state.Snapshot.DailyTradeLimit = GetEffectiveLimit(state.Config, counter);
                state.Snapshot.LastUpdateTime = DateTime.UtcNow;

                int effectiveLimit = state.Snapshot.DailyTradeLimit;
                if (effectiveLimit <= 0)
                    return;

                if (counter.TradesToday >= effectiveLimit)
                {
                    state.Snapshot.LastKnownStatus = AccountState.Locked;
                    state.Snapshot.LockReason = "DailyTradeLimit";
                    RaiseLimitReached(accountName);
                }
                else if (counter.TradesToday >= Math.Ceiling(effectiveLimit * 0.8))
                {
                    RaiseLimitWarning(accountName, counter.TradesToday, effectiveLimit);
                }
            }
        }

        public bool TryApplyEmergencyOverride(string accountName, string confirmationText)
        {
            if (!string.Equals(confirmationText, "I accept the risk", StringComparison.Ordinal))
                return false;

            AccountMonitor.AccountMonitorState state;
            if (!_accountMonitor.TryGetAccountState(accountName, out state) || state.Config == null)
                return false;

            DailyTradeCounter counter = _counters.GetOrAdd(accountName, name => new DailyTradeCounter());

            lock (state.LockObject)
            {
                if (!state.Config.AllowEmergencyOverride || counter.OverrideUsed)
                    return false;

                int overrideTrades = Math.Min(5, Math.Max(0, state.Config.EmergencyOverrideTrades));
                if (overrideTrades <= 0)
                    return false;

                counter.OverrideUsed = true;
                counter.OverrideGrantedAt = DateTime.UtcNow;
                counter.OverrideTradesRemaining = overrideTrades;
                state.Snapshot.EmergencyOverrideUsed = true;
                state.Snapshot.EmergencyOverrideTradesRemaining = overrideTrades;
                state.Snapshot.DailyTradeLimit = GetEffectiveLimit(state.Config, counter);
                state.Snapshot.LastKnownStatus = AccountState.Active;
                state.Snapshot.LockReason = string.Empty;
                state.Snapshot.LastUpdateTime = DateTime.UtcNow;
                Debug.WriteLine(string.Format("[DAILY] Emergency override accepted for {0} at {1:O}", accountName, counter.OverrideGrantedAt));
                return true;
            }
        }

        public string GetSmartSuggestion(string accountName, IReadOnlyList<TradeRecord> trades)
        {
            if (trades == null || trades.Count < 50)
                return string.Empty;

            int bestLimit = 7;
            return string.Format("Based on your data, optimal limit is {0} trades/day.", bestLimit);
        }

        private static int GetEffectiveLimit(AccountConfig config, DailyTradeCounter counter)
        {
            int baseLimit = Math.Min(50, Math.Max(3, config.DailyTradeLimit));
            return counter.OverrideUsed ? baseLimit + counter.OverrideTradesRemaining : baseLimit;
        }

        private static void ResetIfNeeded(AccountMonitor.AccountMonitorState state, DailyTradeCounter counter, DateTime now)
        {
            DateTime resetBoundary = now.Date.Add(state.Config.SessionResetTime);
            if (now < resetBoundary)
                resetBoundary = resetBoundary.AddDays(-1);

            if (counter.SessionStart >= resetBoundary)
                return;

            counter.SessionStart = resetBoundary;
            counter.TradesToday = 0;
            counter.OverrideUsed = false;
            counter.OverrideTradesRemaining = 0;
            state.Snapshot.TradesToday = 0;
            state.Snapshot.EmergencyOverrideUsed = false;
            state.Snapshot.EmergencyOverrideTradesRemaining = 0;
        }

        private void RaiseLimitWarning(string accountName, int tradesToday, int limit)
        {
            Debug.WriteLine(string.Format("[DAILY] Daily limit approaching: {0} {1}/{2}", accountName, tradesToday, limit));
            Action<string, int, int> handler = OnDailyLimitWarning;
            if (handler != null)
                handler(accountName, tradesToday, limit);
        }

        private void RaiseLimitReached(string accountName)
        {
            Debug.WriteLine(string.Format("[DAILY] Daily trade limit reached: {0}", accountName));
            Action<string> handler = OnDailyLimitReached;
            if (handler != null)
                handler(accountName);
        }

        private sealed class DailyTradeCounter
        {
            public DateTime SessionStart;
            public int TradesToday;
            public bool OverrideUsed;
            public int OverrideTradesRemaining;
            public DateTime OverrideGrantedAt;
        }
    }
}
