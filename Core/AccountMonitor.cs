using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NinjaTrader.Cbi;
using PropFirmGuardian.Models;

namespace PropFirmGuardian.Core
{
    public sealed class AccountMonitor : IDisposable
    {
        private const int PnLSamplingIntervalMs = 100;
        private readonly ConcurrentDictionary<string, AccountMonitorState> _accountStates;
        private readonly Timer _pnlTimer;
        private RiskEngine _riskEngine;
        private int _isSampling;
        private bool _isDisposed;

        public AccountMonitor()
        {
            _accountStates = new ConcurrentDictionary<string, AccountMonitorState>(StringComparer.OrdinalIgnoreCase);
            _pnlTimer = new Timer(OnPnLTimerTick, null, PnLSamplingIntervalMs, PnLSamplingIntervalMs);
        }

        public event Action<string, string, double> OnBreachDetected;

        public void SetRiskEngine(RiskEngine riskEngine)
        {
            _riskEngine = riskEngine;
        }

        public bool TryGetAccountState(string accountName, out AccountMonitorState state)
        {
            return _accountStates.TryGetValue(accountName, out state);
        }

        public bool RemoveAccount(string accountName)
        {
            if (string.IsNullOrWhiteSpace(accountName))
                return false;

            AccountMonitorState removedState;
            return _accountStates.TryRemove(accountName, out removedState);
        }

        public IReadOnlyCollection<AccountMonitorState> GetAccountStates()
        {
            return _accountStates.Values.ToArray();
        }

        public int CopyAccountStatesTo(AccountMonitorState[] buffer)
        {
            if (buffer == null || buffer.Length == 0)
                return 0;

            int index = 0;
            foreach (AccountMonitorState state in _accountStates.Values)
            {
                if (index >= buffer.Length)
                    break;

                buffer[index] = state;
                index++;
            }

            return index;
        }

        public ConcurrentDictionary<string, SessionSnapshot> ExportSnapshots()
        {
            ConcurrentDictionary<string, SessionSnapshot> snapshots =
                new ConcurrentDictionary<string, SessionSnapshot>(StringComparer.OrdinalIgnoreCase);

            foreach (AccountMonitorState state in _accountStates.Values)
            {
                lock (state.LockObject)
                    snapshots[state.Snapshot.AccountName] = CloneSnapshot(state.Snapshot);
            }

            return snapshots;
        }

        public void RestoreSnapshot(string accountName, SessionSnapshot snapshot)
        {
            if (snapshot == null)
                return;

            AccountMonitorState state;
            if (!_accountStates.TryGetValue(accountName, out state))
                return;

            lock (state.LockObject)
            {
                state.Snapshot.PeakUnrealizedPnL = snapshot.PeakUnrealizedPnL;
                state.Snapshot.SessionStartBalance = snapshot.SessionStartBalance;
                state.Snapshot.CurrentRealizedPnL = snapshot.CurrentRealizedPnL;
                state.Snapshot.LockedUntil = snapshot.LockedUntil;
                state.Snapshot.LastKnownStatus = snapshot.LastKnownStatus;
                state.Snapshot.LockReason = snapshot.LockReason ?? string.Empty;
                state.Snapshot.NewsLockoutActive = snapshot.NewsLockoutActive;
                state.Snapshot.IsHardLock = snapshot.IsHardLock;
                state.Snapshot.TradesToday = snapshot.TradesToday;
                state.Snapshot.DailyTradeLimit = snapshot.DailyTradeLimit;
                state.Snapshot.EmergencyOverrideUsed = snapshot.EmergencyOverrideUsed;
                state.Snapshot.EmergencyOverrideTradesRemaining = snapshot.EmergencyOverrideTradesRemaining;
                state.Snapshot.SessionQualityScore = snapshot.SessionQualityScore;
                state.Snapshot.SessionQualityMessage = snapshot.SessionQualityMessage;
                state.Snapshot.ActiveGuards = snapshot.ActiveGuards;
                state.Snapshot.PassProbability = snapshot.PassProbability;
                state.Snapshot.RequiredDailyAverage = snapshot.RequiredDailyAverage;
                state.Snapshot.EvalStartDateUtc = snapshot.EvalStartDateUtc;
                state.Snapshot.GraceWindowActive = snapshot.GraceWindowActive;
                state.Snapshot.GraceWindowEndsUtc = snapshot.GraceWindowEndsUtc;
                state.Snapshot.RecoveryModeOffered = snapshot.RecoveryModeOffered;
                state.Snapshot.RecoveryModeUsed = snapshot.RecoveryModeUsed;
                state.Snapshot.RecoveryJournalText = snapshot.RecoveryJournalText;
                state.Snapshot.ActualPnL = snapshot.ActualPnL;
                state.Snapshot.GuardianModePnL = snapshot.GuardianModePnL;
                state.Snapshot.LargestTradePercent = snapshot.LargestTradePercent;
                state.Snapshot.GrossProfit = snapshot.GrossProfit;
                state.Snapshot.GrossLoss = snapshot.GrossLoss;
                state.Snapshot.ConsecutiveLosses = snapshot.ConsecutiveLosses;
                state.Snapshot.LastUpdateTime = DateTime.UtcNow;
            }
        }

        public void UpdateConfig(AccountConfig config)
        {
            if (config == null || string.IsNullOrWhiteSpace(config.AccountName))
                return;

            AccountMonitorState state;
            if (!_accountStates.TryGetValue(config.AccountName, out state))
                return;

            lock (state.LockObject)
            {
                state.Config = config;
                state.Snapshot.LastUpdateTime = DateTime.UtcNow;
            }
        }

        public void InitializeAccount(Account account, AccountConfig config)
        {
            if (account == null)
                throw new ArgumentNullException("account");

            if (config == null)
                throw new ArgumentNullException("config");

            if (string.IsNullOrWhiteSpace(config.AccountName))
                config.AccountName = account.Name;

            AccountMonitorState state = new AccountMonitorState
            {
                AccountRef = account,
                Config = config,
                Snapshot = new SessionSnapshot
                {
                    AccountName = account.Name,
                    PeakUnrealizedPnL = 0.0,
                    SessionStartBalance = ReadAccountItem(account, AccountItem.CashValue),
                    CurrentRealizedPnL = ReadAccountItem(account, AccountItem.RealizedProfitLoss),
                    LastKnownStatus = AccountState.Active,
                    LockReason = string.Empty,
                    NewsLockoutActive = false,
                    DailyTradeLimit = config.DailyTradeLimit,
                    SessionQualityScore = 50.0,
                    SessionQualityMessage = "No closed trades yet.",
                    ActiveGuards = string.Empty,
                    PassProbability = 50.0,
                    EvalStartDateUtc = DateTime.UtcNow,
                    LastUpdateTime = DateTime.UtcNow
                },
                LockObject = new object(),
                LastFlattenTime = DateTime.MinValue,
                FlattenAttempts = 0
            };

            _accountStates.AddOrUpdate(account.Name, state, (name, existing) =>
            {
                lock (existing.LockObject)
                {
                    existing.AccountRef = account;
                    existing.Config = config;
                    existing.Snapshot.SessionStartBalance = state.Snapshot.SessionStartBalance;
                    existing.Snapshot.CurrentRealizedPnL = state.Snapshot.CurrentRealizedPnL;
                    existing.Snapshot.LastUpdateTime = DateTime.UtcNow;
                    return existing;
                }
            });
        }

        public void UpdateRealizedPnL(string accountName, double realizedPnL)
        {
            AccountMonitorState state;
            if (!_accountStates.TryGetValue(accountName, out state))
                return;

            lock (state.LockObject)
            {
                state.Snapshot.CurrentRealizedPnL = realizedPnL;
                state.Snapshot.LastUpdateTime = DateTime.UtcNow;
                CheckDailyLossLimit(state);
            }
        }

        public void UpdateUnrealizedPnL(string accountName, double unrealizedPnL)
        {
            AccountMonitorState state;
            if (!_accountStates.TryGetValue(accountName, out state))
                return;

            lock (state.LockObject)
            {
                double totalSessionPnL = state.Snapshot.CurrentRealizedPnL + unrealizedPnL;
                state.Snapshot.PeakUnrealizedPnL = Math.Max(state.Snapshot.PeakUnrealizedPnL, totalSessionPnL);
                state.Snapshot.LastUpdateTime = DateTime.UtcNow;
                CheckTrailingDrawdown(state, unrealizedPnL);
            }
        }

        public void CalculatePeakPnL(string accountName)
        {
            AccountMonitorState state;
            if (!_accountStates.TryGetValue(accountName, out state))
                return;

            lock (state.LockObject)
            {
                double unrealizedPnL = ReadUnrealizedPnL(state.AccountRef);
                double totalSessionPnL = state.Snapshot.CurrentRealizedPnL + unrealizedPnL;
                state.Snapshot.PeakUnrealizedPnL = Math.Max(state.Snapshot.PeakUnrealizedPnL, totalSessionPnL);
                state.Snapshot.LastUpdateTime = DateTime.UtcNow;
            }
        }

        public AccountState GetCurrentState(string accountName)
        {
            AccountMonitorState state;
            if (!_accountStates.TryGetValue(accountName, out state))
                return AccountState.Disconnected;

            lock (state.LockObject)
            {
                return state.Snapshot.LastKnownStatus;
            }
        }

        public void TransitionState(string accountName, AccountState newState, string reason)
        {
            AccountMonitorState state;
            if (!_accountStates.TryGetValue(accountName, out state))
                return;

            lock (state.LockObject)
            {
                state.Snapshot.LastKnownStatus = newState;
                state.Snapshot.LockReason = reason ?? string.Empty;
                if (newState == AccountState.Active)
                    state.Snapshot.IsHardLock = false;
                state.Snapshot.LastUpdateTime = DateTime.UtcNow;
            }
        }

        public bool CanTrade(string accountName)
        {
            AccountMonitorState state;
            if (!_accountStates.TryGetValue(accountName, out state))
                return false;

            lock (state.LockObject)
            {
                ClearExpiredHardLock(state);
                AccountState currentState = state.Snapshot.LastKnownStatus;
                if (state.Snapshot.IsHardLock && state.Snapshot.LockedUntil.HasValue && state.Snapshot.LockedUntil.Value > DateTime.Now)
                    return false;

                return currentState != AccountState.Locked
                    && currentState != AccountState.HardLocked
                    && currentState != AccountState.Flattening
                    && currentState != AccountState.NewsLocked;
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            _pnlTimer.Dispose();
        }

        private void OnPnLTimerTick(object timerState)
        {
            if (_isDisposed)
                return;

            if (Interlocked.Exchange(ref _isSampling, 1) == 1)
                return;

            try
            {
                foreach (AccountMonitorState state in _accountStates.Values)
                {
                    if (state == null || state.Config == null || state.Config.IsExcluded)
                        continue;

                    string accountName;
                    lock (state.LockObject)
                    {
                        ClearExpiredHardLock(state);
                        accountName = state.Snapshot.AccountName;
                        double unrealizedPnL = ReadUnrealizedPnL(state.AccountRef);
                        double realizedPnL = ReadAccountItem(state.AccountRef, AccountItem.RealizedProfitLoss);

                        state.Snapshot.CurrentRealizedPnL = realizedPnL;
                        double totalSessionPnL = realizedPnL + unrealizedPnL;
                        state.Snapshot.PeakUnrealizedPnL = Math.Max(state.Snapshot.PeakUnrealizedPnL, totalSessionPnL);
                        state.Snapshot.LastUpdateTime = DateTime.UtcNow;

                        CheckTrailingDrawdown(state, unrealizedPnL);
                        CheckDailyLossLimit(state);
                    }

                    if (_riskEngine != null)
                    {
                        Debug.WriteLine(string.Format("[MONITOR] Evaluating rules for {0}", accountName));
                        _riskEngine.EvaluateRules(accountName);
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _isSampling, 0);
            }
        }

        private static double ReadUnrealizedPnL(Account account)
        {
            if (account == null)
                return 0.0;

            double unrealizedPnL = 0.0;

            foreach (Position position in account.Positions)
            {
                if (position == null || position.MarketPosition == MarketPosition.Flat)
                    continue;

                double marketPrice = position.GetMarketPrice();
                unrealizedPnL += position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, marketPrice);
            }

            return unrealizedPnL;
        }

        private static double ReadAccountItem(Account account, AccountItem accountItem)
        {
            if (account == null)
                return 0.0;

            try
            {
                return account.Get(accountItem, account.Denomination);
            }
            catch (Exception exception)
            {
                Debug.WriteLine(string.Format(
                    "[PropFirmGuardian] Unable to read account item {0} for {1}: {2}",
                    accountItem,
                    account.Name,
                    exception.Message));
                return 0.0;
            }
        }

        private void CheckTrailingDrawdown(AccountMonitorState state, double currentUnrealizedPnL)
        {
            if (state.Config.TrailingDrawdown <= 0.0)
                return;

            double effectiveLimit = Math.Max(0.0, state.Config.TrailingDrawdown - state.Config.SafetyBuffer);
            double currentTotalPnL = state.Snapshot.CurrentRealizedPnL + currentUnrealizedPnL;
            double drawdownFromPeak = state.Snapshot.PeakUnrealizedPnL - currentTotalPnL;

            if (drawdownFromPeak >= effectiveLimit)
                RaiseBreachDetected(state, "TrailingDrawdown", drawdownFromPeak);
        }

        private void CheckDailyLossLimit(AccountMonitorState state)
        {
            if (state.Config.DailyLossLimit <= 0.0)
                return;

            double effectiveLimit = Math.Max(0.0, state.Config.DailyLossLimit - state.Config.SafetyBuffer);
            double lossAmount = Math.Max(0.0, -state.Snapshot.CurrentRealizedPnL);

            if (lossAmount >= effectiveLimit)
                RaiseBreachDetected(state, "DailyLossLimit", lossAmount);
        }

        private void RaiseBreachDetected(AccountMonitorState state, string ruleType, double breachAmount)
        {
            state.Snapshot.LastKnownStatus = AccountState.Warning;
            state.Snapshot.LockReason = string.Format("{0} breach detected", ruleType);

            Action<string, string, double> handler = OnBreachDetected;
            if (handler != null)
                handler(state.Snapshot.AccountName, ruleType, breachAmount);
        }

        private static SessionSnapshot CloneSnapshot(SessionSnapshot source)
        {
            return new SessionSnapshot
            {
                AccountName = source.AccountName,
                PeakUnrealizedPnL = source.PeakUnrealizedPnL,
                SessionStartBalance = source.SessionStartBalance,
                CurrentRealizedPnL = source.CurrentRealizedPnL,
                LockedUntil = source.LockedUntil,
                LastKnownStatus = source.LastKnownStatus,
                LockReason = source.LockReason,
                NewsLockoutActive = source.NewsLockoutActive,
                IsHardLock = source.IsHardLock,
                TradesToday = source.TradesToday,
                DailyTradeLimit = source.DailyTradeLimit,
                EmergencyOverrideUsed = source.EmergencyOverrideUsed,
                EmergencyOverrideTradesRemaining = source.EmergencyOverrideTradesRemaining,
                SessionQualityScore = source.SessionQualityScore,
                SessionQualityMessage = source.SessionQualityMessage,
                ActiveGuards = source.ActiveGuards,
                PassProbability = source.PassProbability,
                RequiredDailyAverage = source.RequiredDailyAverage,
                EvalStartDateUtc = source.EvalStartDateUtc,
                GraceWindowActive = source.GraceWindowActive,
                GraceWindowEndsUtc = source.GraceWindowEndsUtc,
                RecoveryModeOffered = source.RecoveryModeOffered,
                RecoveryModeUsed = source.RecoveryModeUsed,
                RecoveryJournalText = source.RecoveryJournalText,
                ActualPnL = source.ActualPnL,
                GuardianModePnL = source.GuardianModePnL,
                LargestTradePercent = source.LargestTradePercent,
                GrossProfit = source.GrossProfit,
                GrossLoss = source.GrossLoss,
                ConsecutiveLosses = source.ConsecutiveLosses,
                LastUpdateTime = source.LastUpdateTime
            };
        }

        private static void ClearExpiredHardLock(AccountMonitorState state)
        {
            if (!state.Snapshot.IsHardLock)
                return;

            if (state.Snapshot.LockedUntil.HasValue && state.Snapshot.LockedUntil.Value > DateTime.Now)
                return;

            state.Snapshot.IsHardLock = false;
            state.Snapshot.LockedUntil = null;
            state.Snapshot.LastKnownStatus = AccountState.Active;
            state.Snapshot.LockReason = string.Empty;
            state.Snapshot.LastUpdateTime = DateTime.UtcNow;
        }

        public sealed class AccountMonitorState
        {
            public Account AccountRef { get; set; }
            public AccountConfig Config { get; set; }
            public SessionSnapshot Snapshot { get; set; }

            // Each account has its own lock so fast PnL sampling on one account never blocks
            // unrelated accounts. The concurrent dictionary protects account lookup and
            // membership changes; this lock protects the mutable snapshot for one account.
            public object LockObject { get; set; }

            public DateTime LastFlattenTime { get; set; }
            public int FlattenAttempts { get; set; }
        }
    }
}
