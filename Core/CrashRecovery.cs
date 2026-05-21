using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using NinjaTrader.Cbi;
using PropFirmGuardian.Models;
using PropFirmGuardian.Services;
using PropFirmGuardian.Utils;

namespace PropFirmGuardian.Core
{
    public sealed class CrashRecovery
    {
        private readonly AccountMonitor _accountMonitor;
        private readonly PersistenceService _persistenceService;

        public event Action<string, string> OnRecoveryAlert;

        public CrashRecovery(AccountMonitor accountMonitor, PersistenceService persistenceService)
        {
            if (accountMonitor == null)
                throw new ArgumentNullException("accountMonitor");

            if (persistenceService == null)
                throw new ArgumentNullException("persistenceService");

            _accountMonitor = accountMonitor;
            _persistenceService = persistenceService;
        }

        public void PerformRecovery()
        {
            ConcurrentDictionary<string, SessionSnapshot> loadedStates = _persistenceService.LoadState();
            Debug.WriteLine(string.Format("[RECOVERY] Starting recovery for {0} accounts", loadedStates.Count));

            foreach (SessionSnapshot snapshot in loadedStates.Values)
            {
                if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.AccountName))
                    continue;

                Account liveAccount = Account.All.FirstOrDefault(account =>
                    account != null && string.Equals(account.Name, snapshot.AccountName, StringComparison.OrdinalIgnoreCase));

                if (liveAccount == null)
                {
                    Debug.WriteLine(string.Format(
                        "[RECOVERY] Account not found in NT8: {0}",
                        snapshot.AccountName));
                    continue;
                }

                Debug.WriteLine(string.Format(
                    "[RECOVERY] Account {0}: file status = {1}, live status = {2}",
                    snapshot.AccountName,
                    snapshot.LastKnownStatus,
                    liveAccount.ConnectionStatus));

                AccountMonitor.AccountMonitorState monitorState;
                if (!_accountMonitor.TryGetAccountState(snapshot.AccountName, out monitorState))
                {
                    AccountConfig config = new AccountConfig
                    {
                        AccountName = snapshot.AccountName,
                        PropFirmName = "Custom"
                    };
                    _accountMonitor.InitializeAccount(liveAccount, config);
                }

                _accountMonitor.RestoreSnapshot(snapshot.AccountName, snapshot);
                Debug.WriteLine(string.Format("[RECOVERY] PeakPnL restored: {0} (was zero)", snapshot.PeakUnrealizedPnL));
                ValidateAndRepairRecoveredState(snapshot.AccountName, snapshot, liveAccount);
            }
        }

        public void ShowRecoveryAlert(string accountName, string message)
        {
            ThreadSafeDispatcher.SafeInvoke(() =>
            {
                Action<string, string> alert = OnRecoveryAlert;
                if (alert != null)
                    alert(accountName, message);

                MessageBox.Show(
                    message,
                    string.Format("Prop Firm Guardian Recovery Alert - {0}", accountName),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            });
        }

        public bool ValidateStateConsistency(string accountName, SessionSnapshot snapshot, Account liveAccount)
        {
            string reason;
            return ValidateStateConsistency(accountName, snapshot, liveAccount, out reason);
        }

        public bool ValidateStateConsistency(string accountName, SessionSnapshot snapshot, Account liveAccount, out string reason)
        {
            reason = string.Empty;

            if (snapshot == null)
            {
                reason = "No saved snapshot was available.";
                return false;
            }

            if (liveAccount == null)
            {
                reason = "No matching live NT8 account was found.";
                return false;
            }

            bool hasOpenPosition = HasOpenPosition(liveAccount);

            if (snapshot.LastKnownStatus == AccountState.Flattening && hasOpenPosition)
            {
                reason = "Saved state was Flattening and live account still has open positions.";
                return false;
            }

            if (snapshot.LastKnownStatus == AccountState.Locked
                && snapshot.LockedUntil.HasValue
                && snapshot.LockedUntil.Value <= DateTime.Now)
            {
                reason = "Saved lockout has expired.";
                return false;
            }

            if (snapshot.LastKnownStatus == AccountState.Active
                && !hasOpenPosition
                && snapshot.CurrentRealizedPnL <= -Math.Max(500.0, snapshot.SessionStartBalance * 0.01))
            {
                reason = "Saved state was Active but live account is flat after a large realized loss.";
                return false;
            }

            return true;
        }

        private void ValidateAndRepairRecoveredState(string accountName, SessionSnapshot snapshot, Account liveAccount)
        {
            string reason;
            bool consistent = ValidateStateConsistency(accountName, snapshot, liveAccount, out reason);

            if (!consistent)
                Debug.WriteLine(string.Format("[RECOVERY] Consistency warning for {0}: {1}", accountName, reason));

            if (snapshot.LastKnownStatus == AccountState.Flattening && HasOpenPosition(liveAccount))
            {
                Debug.WriteLine("[RECOVERY] MISMATCH: file says Flattening but live has open position!");
                ShowRecoveryAlert(
                    accountName,
                    string.Format("RECOVERY ALERT: {0} was flattening when NinjaTrader stopped, and live positions are still open. Review and flatten manually if needed.", accountName));
                return;
            }

            if ((snapshot.LastKnownStatus == AccountState.Flattening || snapshot.LastKnownStatus == AccountState.Warning)
                && !HasOpenPosition(liveAccount)
                && !HasActiveOrder(liveAccount))
            {
                _accountMonitor.TransitionState(accountName, AccountState.Active, "Recovered flat transient state");
                return;
            }

            if (snapshot.LastKnownStatus == AccountState.Locked
                && !HasOpenPosition(liveAccount)
                && !HasActiveOrder(liveAccount)
                && IsRecoverableRiskLock(snapshot.LockReason))
            {
                _accountMonitor.TransitionState(accountName, AccountState.Active, "Recovered stale risk lock");
                return;
            }

            if (snapshot.LastKnownStatus == AccountState.Locked
                && snapshot.LockedUntil.HasValue
                && snapshot.LockedUntil.Value <= DateTime.Now)
            {
                _accountMonitor.TransitionState(accountName, AccountState.Active, "Recovered expired lockout");
                return;
            }

            if (snapshot.LastKnownStatus == AccountState.Active
                && !HasOpenPosition(liveAccount)
                && snapshot.CurrentRealizedPnL <= -Math.Max(500.0, snapshot.SessionStartBalance * 0.01))
            {
                Debug.WriteLine(string.Format(
                    "[RECOVERY] Possible crash during flatten for {0}: account is flat with large realized loss.",
                    accountName));
            }
        }

        private static bool HasOpenPosition(Account account)
        {
            foreach (Position position in account.Positions)
            {
                if (position != null && (position.Quantity != 0 || position.MarketPosition != MarketPosition.Flat))
                    return true;
            }

            return false;
        }

        private static bool HasActiveOrder(Account account)
        {
            foreach (Order order in account.Orders)
            {
                if (order == null)
                    continue;

                if (order.OrderState == OrderState.Accepted
                    || order.OrderState == OrderState.AcceptedByRisk
                    || order.OrderState == OrderState.Submitted
                    || order.OrderState == OrderState.TriggerPending
                    || order.OrderState == OrderState.Working
                    || order.OrderState == OrderState.PartFilled
                    || order.OrderState == OrderState.ChangePending
                    || order.OrderState == OrderState.ChangeSubmitted
                    || order.OrderState == OrderState.CancelPending
                    || order.OrderState == OrderState.CancelSubmitted)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsRecoverableRiskLock(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return true;

            return reason.IndexOf("TrailingDrawdown", StringComparison.OrdinalIgnoreCase) >= 0
                || reason.IndexOf("Risk breach", StringComparison.OrdinalIgnoreCase) >= 0
                || reason.IndexOf("Flatten completed", StringComparison.OrdinalIgnoreCase) >= 0
                || reason.IndexOf("Hard enforcement", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
