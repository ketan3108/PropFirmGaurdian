using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NinjaTrader.Cbi;
using PropFirmGuardian.Models;

namespace PropFirmGuardian.Core
{
    public sealed class FlattenProtocol
    {
        private const int FlattenRateLimitMs = 500;
        private const int ConfirmationIntervalMs = 100;
        private const int ConfirmationTimeoutMs = 5000;
        private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
        private readonly AccountMonitor _accountMonitor;

        public FlattenProtocol(AccountMonitor accountMonitor)
        {
            if (accountMonitor == null)
                throw new ArgumentNullException("accountMonitor");

            _accountMonitor = accountMonitor;
        }

        public void ExecuteFlatten(string accountName, string reason)
        {
            AccountMonitor.AccountMonitorState state;
            if (!_accountMonitor.TryGetAccountState(accountName, out state))
                return;

            Account account;
            lock (state.LockObject)
            {
                if (state.Snapshot.LastKnownStatus == AccountState.Flattening)
                    return;

                DateTime now = DateTime.UtcNow;
                if ((now - state.LastFlattenTime).TotalMilliseconds < FlattenRateLimitMs)
                    return;

                state.LastFlattenTime = now;
                state.FlattenAttempts++;
                state.Snapshot.LastKnownStatus = AccountState.Flattening;
                state.Snapshot.LockReason = reason ?? "Flatten requested";
                state.Snapshot.LastUpdateTime = now;
                account = state.AccountRef;
            }

            if (account == null)
                return;

            if (!HasOpenPosition(account) && AreAllWorkingOrdersCancelled(account))
            {
                lock (state.LockObject)
                {
                    state.Snapshot.LastKnownStatus = AccountState.Active;
                    state.Snapshot.LockReason = string.Empty;
                    state.Snapshot.LastUpdateTime = DateTime.UtcNow;
                }

                return;
            }

            Task.Run(() => ExecuteFlattenSequence(accountName, reason, state, account));
        }

        public bool IsAccountLocked(string accountName)
        {
            AccountMonitor.AccountMonitorState state;
            if (!_accountMonitor.TryGetAccountState(accountName, out state))
                return false;

            lock (state.LockObject)
            {
                if (state.Snapshot.LastKnownStatus != AccountState.Locked)
                    return false;

                if (state.Snapshot.LockedUntil.HasValue && state.Snapshot.LockedUntil.Value > DateTime.Now)
                    return true;

                state.Snapshot.LastKnownStatus = AccountState.Active;
                state.Snapshot.LockedUntil = null;
                state.Snapshot.LockReason = string.Empty;
                state.Snapshot.LastUpdateTime = DateTime.UtcNow;
                return false;
            }
        }

        private void ExecuteFlattenSequence(string accountName, string reason, AccountMonitor.AccountMonitorState state, Account account)
        {
            try
            {
                // Atomic sequence: cancel working orders first, then flatten every open
                // instrument, then confirm both order and position state. The confirmation
                // loop runs on a background Task and uses Thread.Sleep so the NT8 UI
                // dispatcher is never blocked during emergency liquidation.
                CancelActiveOrders(account);
                FlattenOpenPositions(accountName, account);

                bool confirmed = WaitForFlatAndCancelled(account, ConfirmationTimeoutMs);
                if (!confirmed && HasOpenPosition(account))
                {
                    Debug.WriteLine(string.Format(
                        "[PropFirmGuardian] Warning: {0} still has open position after {1}ms; retrying flatten once.",
                        accountName,
                        ConfirmationTimeoutMs));

                    Thread.Sleep(FlattenRateLimitMs);
                    FlattenOpenPositions(accountName, account);
                    WaitForFlatAndCancelled(account, ConfirmationTimeoutMs);
                }

                bool finalConfirmed = AreAllPositionsFlat(account) && AreAllWorkingOrdersCancelled(account);
                LockAccount(state, finalConfirmed, reason);
            }
            catch (Exception exception)
            {
                Debug.WriteLine(string.Format(
                    "[PropFirmGuardian] Flatten protocol error for {0}: {1}",
                    accountName,
                    exception));

                LockAccount(state, false, reason);
            }
        }

        private static void FlattenOpenPositions(string accountName, Account account)
        {
            foreach (Position position in account.Positions)
            {
                if (position == null || position.Instrument == null)
                    continue;

                if (position.Quantity == 0 && position.MarketPosition == MarketPosition.Flat)
                    continue;

                account.Flatten(new[] { position.Instrument });
                Debug.WriteLine(string.Format(
                    "[PropFirmGuardian] Flattening {0} on {1}",
                    accountName,
                    GetInstrumentName(position.Instrument)));
            }
        }

        private static void CancelActiveOrders(Account account)
        {
            foreach (Order order in account.Orders)
            {
                if (order != null && IsWorkingOrder(order.OrderState))
                    account.Cancel(new[] { order });
            }
        }

        private static bool WaitForFlatAndCancelled(Account account, int timeoutMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (DateTime.UtcNow < deadline)
            {
                if (AreAllPositionsFlat(account) && AreAllWorkingOrdersCancelled(account))
                    return true;

                Thread.Sleep(ConfirmationIntervalMs);
            }

            return AreAllPositionsFlat(account) && AreAllWorkingOrdersCancelled(account);
        }

        private static bool AreAllPositionsFlat(Account account)
        {
            foreach (Position position in account.Positions)
            {
                if (position != null && (position.Quantity != 0 || position.MarketPosition != MarketPosition.Flat))
                    return false;
            }

            return true;
        }

        private static bool HasOpenPosition(Account account)
        {
            return !AreAllPositionsFlat(account);
        }

        private static bool AreAllWorkingOrdersCancelled(Account account)
        {
            foreach (Order order in account.Orders)
            {
                if (order != null && IsWorkingOrder(order.OrderState))
                    return false;
            }

            return true;
        }

        private static bool IsWorkingOrder(OrderState orderState)
        {
            return orderState == OrderState.Accepted
                || orderState == OrderState.AcceptedByRisk
                || orderState == OrderState.Submitted
                || orderState == OrderState.TriggerPending
                || orderState == OrderState.Working
                || orderState == OrderState.PartFilled
                || orderState == OrderState.ChangePending
                || orderState == OrderState.ChangeSubmitted
                || orderState == OrderState.CancelPending
                || orderState == OrderState.CancelSubmitted;
        }

        private static string GetInstrumentName(Instrument instrument)
        {
            if (instrument == null)
                return "Unknown";

            return !string.IsNullOrEmpty(instrument.FullName) ? instrument.FullName : instrument.ToString();
        }

        private static void LockAccount(AccountMonitor.AccountMonitorState state, bool confirmed, string reason)
        {
            lock (state.LockObject)
            {
                state.Snapshot.LastKnownStatus = AccountState.Locked;
                state.Snapshot.LockedUntil = DateTime.Now.Add(LockoutDuration);
                state.Snapshot.LockReason = reason ?? "Flatten completed";
                state.Snapshot.LastUpdateTime = DateTime.UtcNow;
            }

            if (confirmed)
            {
                Debug.WriteLine(string.Format(
                    "[PropFirmGuardian] Flatten confirmed and account locked: {0}",
                    state.Snapshot.AccountName));
            }
            else
            {
                Debug.WriteLine(string.Format(
                    "[PropFirmGuardian] Warning: flatten confirmation timed out; account locked fail-safe: {0}",
                    state.Snapshot.AccountName));
            }
        }
    }
}
