using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using PropFirmGuardian.Core;
using PropFirmGuardian.Models;

namespace PropFirmGuardian.Intelligence
{
    public sealed class TradeRecord
    {
        public DateTime EntryTime { get; set; }
        public DateTime? ExitTime { get; set; }
        public int Quantity { get; set; }
        public double EntryPrice { get; set; }
        public double? ExitPrice { get; set; }
        public double RealizedPnL { get; set; }
        public string Instrument { get; set; }
    }

    public sealed class TiltDetector
    {
        private const int MaxTradesPerAccount = 50;
        private static readonly TimeSpan RapidReEntryWindow = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan SizeEscalationLockout = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan DeathSpiralWindow = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan DeathSpiralLockout = TimeSpan.FromMinutes(60);
        private readonly AccountMonitor _accountMonitor;
        private readonly ConcurrentDictionary<string, List<TradeRecord>> _tradeHistory;
        private readonly ConcurrentDictionary<string, object> _historyLocks;

        public TiltDetector(AccountMonitor accountMonitor)
        {
            if (accountMonitor == null)
                throw new ArgumentNullException("accountMonitor");

            _accountMonitor = accountMonitor;
            _tradeHistory = new ConcurrentDictionary<string, List<TradeRecord>>(StringComparer.OrdinalIgnoreCase);
            _historyLocks = new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        public event Action<string, string, string> OnTiltWarning;
        public event Action<string, string, TimeSpan> OnTiltLockout;
        public event Action<string, TimeSpan> OnDeathSpiral;

        public void ProcessTrade(string accountName, TradeRecord trade)
        {
            if (string.IsNullOrWhiteSpace(accountName) || trade == null)
                return;

            Debug.WriteLine(string.Format(
                "[TILT] Trade closed: {0} | PnL={1} | time={2:O}",
                accountName,
                trade.RealizedPnL,
                DateTime.Now));

            object historyLock = GetHistoryLock(accountName);
            lock (historyLock)
            {
                List<TradeRecord> history = GetHistory(accountName);
                history.Add(trade);

                while (history.Count > MaxTradesPerAccount)
                    history.RemoveAt(0);
            }

            if (trade.ExitTime.HasValue)
            {
                DetectRapidReEntry(accountName);
                DetectSizeEscalation(accountName);
                DetectDeathSpiral(accountName);
            }
        }

        public void RecordTrade(string accountName, TradeRecord trade)
        {
            ProcessTrade(accountName, trade);
        }

        public void DetectRapidReEntry(string accountName)
        {
            List<TradeRecord> trades = GetHistorySnapshot(accountName);
            if (trades.Count < 2)
                return;

            TradeRecord previous = trades[trades.Count - 2];
            TradeRecord current = trades[trades.Count - 1];
            if (!previous.ExitTime.HasValue)
                return;

            TimeSpan reEntryDelay = current.EntryTime - previous.ExitTime.Value;
            if (reEntryDelay >= TimeSpan.Zero && reEntryDelay < RapidReEntryWindow)
            {
                Debug.WriteLine(string.Format(
                    "[TILT] Rapid re-entry detected: {0} | gap={1}s",
                    accountName,
                    reEntryDelay.TotalSeconds));

                string message = string.Format(
                    "Rapid re-entry detected: new trade entered {0:n0} seconds after the prior exit.",
                    reEntryDelay.TotalSeconds);

                Debug.WriteLine(string.Format("[PropFirmGuardian] Tilt warning for {0}: {1}", accountName, message));

                Action<string, string, string> handler = OnTiltWarning;
                if (handler != null)
                    handler(accountName, "RapidReEntry", message);
            }
        }

        public void DetectSizeEscalation(string accountName)
        {
            List<TradeRecord> trades = GetHistorySnapshot(accountName);
            if (trades.Count < 2)
                return;

            TradeRecord previous = trades[trades.Count - 2];
            TradeRecord current = trades[trades.Count - 1];

            if (previous.RealizedPnL < 0.0 && Math.Abs(current.Quantity) > Math.Abs(previous.Quantity))
            {
                Debug.WriteLine(string.Format(
                    "[TILT] Size escalation detected: {0} | prevQty={1} | newQty={2}",
                    accountName,
                    previous.Quantity,
                    current.Quantity));

                ApplyLockout(accountName, SizeEscalationLockout, "SizeEscalation", true, false);

                Debug.WriteLine(string.Format(
                    "[PropFirmGuardian] Tilt lockout for {0}: size escalation after loss.",
                    accountName));

                Action<string, string, TimeSpan> handler = OnTiltLockout;
                if (handler != null)
                    handler(accountName, "SizeEscalation", SizeEscalationLockout);
            }
        }

        public void DetectDeathSpiral(string accountName)
        {
            List<TradeRecord> trades = GetHistorySnapshot(accountName)
                .Where(trade => trade.ExitTime.HasValue)
                .OrderBy(trade => trade.ExitTime.Value)
                .ToList();

            if (trades.Count < 3)
                return;

            DateTime latestExit = trades[trades.Count - 1].ExitTime.Value;
            List<TradeRecord> recentTrades = trades
                .Where(trade => latestExit - trade.ExitTime.Value <= DeathSpiralWindow)
                .ToList();

            Debug.WriteLine(string.Format(
                "[TILT] Checking patterns for {0}: trades in last 10min = {1}",
                accountName,
                recentTrades.Count));

            if (recentTrades.Count >= 3 && recentTrades.All(trade => trade.RealizedPnL < 0.0))
            {
                double minutes = (latestExit - recentTrades[0].ExitTime.Value).TotalMinutes;
                Debug.WriteLine(string.Format(
                    "[TILT] DEATH SPIRAL DETECTED: {0} | 3 losses in {1}m",
                    accountName,
                    minutes));

                ApplyLockout(accountName, DeathSpiralLockout, "DeathSpiral", false, true);

                Debug.WriteLine(string.Format(
                    "[PropFirmGuardian] Mandatory death spiral lockout for {0}.",
                    accountName));

                Action<string, TimeSpan> handler = OnDeathSpiral;
                if (handler != null)
                    handler(accountName, DeathSpiralLockout);
            }
        }

        private void ApplyLockout(string accountName, TimeSpan duration, string reason, bool userAcknowledgmentAllowed, bool isHardLock)
        {
            AccountMonitor.AccountMonitorState state;
            if (!_accountMonitor.TryGetAccountState(accountName, out state))
                return;

            lock (state.LockObject)
            {
                state.Snapshot.LastKnownStatus = AccountState.Locked;
                state.Snapshot.LockedUntil = DateTime.Now.Add(duration);
                state.Snapshot.LockReason = reason;
                state.Snapshot.IsHardLock = isHardLock;
                state.Snapshot.LastUpdateTime = DateTime.UtcNow;
            }

            Debug.WriteLine(string.Format(
                "[TILT] LOCKOUT TRIGGERED: {0} | duration={1}m | override={2}",
                accountName,
                duration.TotalMinutes,
                userAcknowledgmentAllowed));
        }

        private List<TradeRecord> GetHistory(string accountName)
        {
            return _tradeHistory.GetOrAdd(accountName, name => new List<TradeRecord>());
        }

        private object GetHistoryLock(string accountName)
        {
            return _historyLocks.GetOrAdd(accountName, name => new object());
        }

        private List<TradeRecord> GetHistorySnapshot(string accountName)
        {
            object historyLock = GetHistoryLock(accountName);
            lock (historyLock)
                return new List<TradeRecord>(GetHistory(accountName));
        }
    }
}
