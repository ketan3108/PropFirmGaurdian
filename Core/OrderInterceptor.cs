using System;
using System.Diagnostics;
using NinjaTrader.Cbi;
using PropFirmGuardian.Models;

namespace PropFirmGuardian.Core
{
    public sealed class OrderInterceptor
    {
        private readonly AccountMonitor _accountMonitor;

        public OrderInterceptor(AccountMonitor accountMonitor)
        {
            if (accountMonitor == null)
                throw new ArgumentNullException("accountMonitor");

            _accountMonitor = accountMonitor;
        }

        public event Action<string, TimeSpan> OnOrderBlocked;

        public void OnOrderUpdate(Order order, Account account)
        {
            if (order == null || account == null)
                return;

            AccountMonitor.AccountMonitorState state;
            if (!_accountMonitor.TryGetAccountState(account.Name, out state))
                return;

            TimeSpan remainingLockout;
            bool shouldBlock;

            lock (state.LockObject)
            {
                AccountState currentState = state.Snapshot.LastKnownStatus;
                shouldBlock = currentState == AccountState.Locked
                    || currentState == AccountState.HardLocked
                    || currentState == AccountState.Flattening
                    || currentState == AccountState.GraceWindow
                    || currentState == AccountState.RecoveryMode
                    || currentState == AccountState.NewsLocked;
                remainingLockout = state.Snapshot.LockedUntil.HasValue
                    ? state.Snapshot.LockedUntil.Value - DateTime.Now
                    : TimeSpan.Zero;
            }

            if (!shouldBlock)
                return;

            if (IsPendingOrWorking(order.OrderState) && IsEntryOrder(order))
            {
                // NT8 exposes cancellation through the owning Account. This blocks fresh
                // orders during lockout without starting another flatten cycle, avoiding
                // recursive cancel/flatten storms while the protocol is already active.
                account.Cancel(new[] { order });
                Debug.WriteLine(string.Format(
                    "[PropFirmGuardian] Blocked order for {0}: Tilt lockout active",
                    account.Name));

                Action<string, TimeSpan> handler = OnOrderBlocked;
                if (handler != null)
                    handler(account.Name, remainingLockout > TimeSpan.Zero ? remainingLockout : TimeSpan.Zero);
            }
            else if (order.OrderState == OrderState.Filled)
            {
                // Race condition handling: an order can fill between NT8 raising the order
                // update and our cancellation request. We log it, but intentionally do not
                // call FlattenProtocol again here; the active flatten confirmation loop owns
                // liquidation so this path cannot create a self-triggering flatten loop.
                Debug.WriteLine(string.Format(
                    "[PropFirmGuardian] Race condition: Order filled before cancel for {0}",
                    account.Name));
            }
        }

        private static bool IsPendingOrWorking(OrderState orderState)
        {
            return orderState == OrderState.Submitted
                || orderState == OrderState.Accepted
                || orderState == OrderState.AcceptedByRisk
                || orderState == OrderState.TriggerPending
                || orderState == OrderState.Working;
        }

        private static bool IsEntryOrder(Order order)
        {
            string action = order.OrderAction.ToString();
            return action.IndexOf("SellShort", StringComparison.OrdinalIgnoreCase) >= 0
                || (action.IndexOf("Buy", StringComparison.OrdinalIgnoreCase) >= 0
                    && action.IndexOf("Cover", StringComparison.OrdinalIgnoreCase) < 0);
        }
    }
}
