using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NinjaTrader.Cbi;
using PropFirmGuardian.Intelligence;

namespace PropFirmGuardian.Core
{
    public sealed class SessionTracker
    {
        private readonly AccountMonitor _accountMonitor;
        private readonly TiltDetector _tiltDetector;
        private readonly ConcurrentDictionary<string, SessionData> _sessions;
        private readonly ConcurrentDictionary<string, object> _sessionLocks;

        public SessionTracker(AccountMonitor accountMonitor)
            : this(accountMonitor, new TiltDetector(accountMonitor))
        {
        }

        public SessionTracker(AccountMonitor accountMonitor, TiltDetector tiltDetector)
        {
            if (accountMonitor == null)
                throw new ArgumentNullException("accountMonitor");

            if (tiltDetector == null)
                throw new ArgumentNullException("tiltDetector");

            _accountMonitor = accountMonitor;
            _tiltDetector = tiltDetector;
            _sessions = new ConcurrentDictionary<string, SessionData>(StringComparer.OrdinalIgnoreCase);
            _sessionLocks = new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        public TiltDetector TiltDetector
        {
            get { return _tiltDetector; }
        }

        public void OnTradeClosed(string accountName, TradeRecord trade)
        {
            if (string.IsNullOrWhiteSpace(accountName) || trade == null)
                return;

            object sessionLock = GetSessionLock(accountName);
            lock (sessionLock)
            {
                SessionData session = GetOrCreateSession(accountName);
                session.TodayTrades.Add(trade);
                session.TotalPnL += trade.RealizedPnL;

                if (Math.Abs(trade.RealizedPnL) > Math.Abs(session.LargestTradePnL))
                {
                    session.LargestTradePnL = trade.RealizedPnL;
                    session.TimeInLargestTrade = trade.ExitTime.HasValue
                        ? trade.ExitTime.Value - trade.EntryTime
                        : TimeSpan.Zero;
                }
            }

            _tiltDetector.ProcessTrade(accountName, trade);
            Debug.WriteLine("[SESSION] Trade processed, tilt check triggered");
        }

        public SessionStats GetSessionStats(string accountName)
        {
            object sessionLock = GetSessionLock(accountName);
            lock (sessionLock)
            {
                SessionData session = GetOrCreateSession(accountName);
                List<TradeRecord> closedTrades = session.TodayTrades
                    .Where(trade => trade.ExitTime.HasValue)
                    .ToList();

                int tradeCount = closedTrades.Count;
                int winners = closedTrades.Count(trade => trade.RealizedPnL > 0.0);
                int losers = closedTrades.Count(trade => trade.RealizedPnL < 0.0);
                double grossProfit = closedTrades.Where(trade => trade.RealizedPnL > 0.0).Sum(trade => trade.RealizedPnL);
                double grossLoss = Math.Abs(closedTrades.Where(trade => trade.RealizedPnL < 0.0).Sum(trade => trade.RealizedPnL));
                double averageWinner = winners > 0 ? grossProfit / winners : 0.0;
                double averageLoser = losers > 0 ? grossLoss / losers : 0.0;
                double profitFactor = grossLoss > 0.0 ? grossProfit / grossLoss : (grossProfit > 0.0 ? double.PositiveInfinity : 0.0);
                double largestTradePercent = Math.Abs(session.TotalPnL) > 0.0
                    ? Math.Abs(session.LargestTradePnL) / Math.Abs(session.TotalPnL)
                    : 0.0;

                return new SessionStats
                {
                    AccountName = accountName,
                    SessionStart = session.SessionStart,
                    SessionStartBalance = session.SessionStartBalance,
                    WinRate = tradeCount > 0 ? (double)winners / tradeCount : 0.0,
                    AverageWinner = averageWinner,
                    AverageLoser = averageLoser,
                    ProfitFactor = profitFactor,
                    LargestTradePercentOfTotalPnL = largestTradePercent,
                    TradeCount = tradeCount,
                    LargestTradePnL = session.LargestTradePnL,
                    TotalPnL = session.TotalPnL,
                    TimeInLargestTrade = session.TimeInLargestTrade
                };
            }
        }

        public List<TradeRecord> GetClosedTrades(string accountName)
        {
            object sessionLock = GetSessionLock(accountName);
            lock (sessionLock)
            {
                SessionData session = GetOrCreateSession(accountName);
                return session.TodayTrades
                    .Where(trade => trade.ExitTime.HasValue)
                    .ToList();
            }
        }

        public void ResetSession(string accountName)
        {
            if (string.IsNullOrWhiteSpace(accountName))
                return;

            object sessionLock = GetSessionLock(accountName);
            lock (sessionLock)
                _sessions[accountName] = CreateSession(accountName);
        }

        private SessionData GetOrCreateSession(string accountName)
        {
            return _sessions.GetOrAdd(accountName, CreateSession);
        }

        private SessionData CreateSession(string accountName)
        {
            return new SessionData
            {
                SessionStart = DateTime.Now,
                SessionStartBalance = ReadSessionStartBalance(accountName),
                TodayTrades = new List<TradeRecord>(),
                LargestTradePnL = 0.0,
                TotalPnL = 0.0,
                TimeInLargestTrade = TimeSpan.Zero
            };
        }

        private double ReadSessionStartBalance(string accountName)
        {
            AccountMonitor.AccountMonitorState state;
            if (!_accountMonitor.TryGetAccountState(accountName, out state))
                return 0.0;

            lock (state.LockObject)
            {
                Account account = state.AccountRef;
                if (account == null)
                    return state.Snapshot != null ? state.Snapshot.SessionStartBalance : 0.0;

                try
                {
                    return account.Get(AccountItem.CashValue, account.Denomination);
                }
                catch
                {
                    return state.Snapshot != null ? state.Snapshot.SessionStartBalance : 0.0;
                }
            }
        }

        private object GetSessionLock(string accountName)
        {
            return _sessionLocks.GetOrAdd(accountName, name => new object());
        }

        private sealed class SessionData
        {
            public DateTime SessionStart { get; set; }
            public double SessionStartBalance { get; set; }
            public List<TradeRecord> TodayTrades { get; set; }
            public double LargestTradePnL { get; set; }
            public double TotalPnL { get; set; }
            public TimeSpan TimeInLargestTrade { get; set; }
        }
    }

    public sealed class SessionStats
    {
        public string AccountName { get; set; }
        public DateTime SessionStart { get; set; }
        public double SessionStartBalance { get; set; }
        public double WinRate { get; set; }
        public double AverageWinner { get; set; }
        public double AverageLoser { get; set; }
        public double ProfitFactor { get; set; }
        public double LargestTradePercentOfTotalPnL { get; set; }
        public int TradeCount { get; set; }
        public double LargestTradePnL { get; set; }
        public double TotalPnL { get; set; }
        public TimeSpan TimeInLargestTrade { get; set; }
    }
}
