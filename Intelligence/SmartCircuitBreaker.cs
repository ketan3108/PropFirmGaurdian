using System;
using System.Diagnostics;
using System.Threading;
using PropFirmGuardian.Core;
using PropFirmGuardian.Models;

namespace PropFirmGuardian.Intelligence
{
    public sealed class SmartCircuitBreaker : IDisposable
    {
        private readonly AccountMonitor _accountMonitor;
        private readonly Timer _timer;
        private bool _isDisposed;

        public SmartCircuitBreaker(AccountMonitor accountMonitor)
        {
            if (accountMonitor == null)
                throw new ArgumentNullException("accountMonitor");

            _accountMonitor = accountMonitor;
            _timer = new Timer(OnTimerTick, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        public event Action<string, string> OnWarning;
        public event Action<string> OnGraceWindowStarted;
        public event Action<string> OnHardLockRequired;
        public event Action<string> OnRecoveryAvailable;

        public void Evaluate(string accountName)
        {
            AccountMonitor.AccountMonitorState state;
            if (!_accountMonitor.TryGetAccountState(accountName, out state) || state.Config == null)
                return;

            lock (state.LockObject)
            {
                EvaluateLocked(state);
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            _timer.Dispose();
        }

        private void EvaluateLocked(AccountMonitor.AccountMonitorState state)
        {
            double limit = Math.Max(1.0, state.Config.DailyLossLimit);
            if (limit <= 1.0)
                return;

            double loss = Math.Max(0.0, -state.Snapshot.CurrentRealizedPnL);
            double ratio = loss / limit;

            if (ratio >= 1.0)
            {
                state.Snapshot.LastKnownStatus = AccountState.Locked;
                state.Snapshot.LockReason = "Daily loss reached";
                state.Snapshot.LockedUntil = DateTime.Now.AddMinutes(15);
                RaiseHardLockRequired(state.Snapshot.AccountName);
            }
            else if (ratio >= 0.95 && !state.Snapshot.GraceWindowActive)
            {
                state.Snapshot.LastKnownStatus = AccountState.GraceWindow;
                state.Snapshot.GraceWindowActive = true;
                state.Snapshot.GraceWindowEndsUtc = DateTime.UtcNow.AddSeconds(60);
                state.Snapshot.LockReason = "Daily loss grace window";
                RaiseGraceWindowStarted(state.Snapshot.AccountName);
            }
            else if (ratio >= 0.80)
            {
                RaiseWarning(state.Snapshot.AccountName, "Approaching daily limit. Consider stopping after this trade.");
            }
        }

        private void OnTimerTick(object stateObject)
        {
            if (_isDisposed)
                return;

            foreach (AccountMonitor.AccountMonitorState state in _accountMonitor.GetAccountStates())
            {
                if (state == null || state.Snapshot == null)
                    continue;

                lock (state.LockObject)
                {
                    if (state.Snapshot.GraceWindowActive && state.Snapshot.GraceWindowEndsUtc.HasValue && DateTime.UtcNow >= state.Snapshot.GraceWindowEndsUtc.Value)
                    {
                        state.Snapshot.GraceWindowActive = false;
                        state.Snapshot.LastKnownStatus = AccountState.Locked;
                        state.Snapshot.LockedUntil = DateTime.Now.AddMinutes(15);
                        state.Snapshot.RecoveryModeOffered = true;
                        RaiseRecoveryAvailable(state.Snapshot.AccountName);
                    }
                }
            }
        }

        private void RaiseWarning(string accountName, string message)
        {
            Debug.WriteLine(string.Format("[CIRCUIT] {0}: {1}", accountName, message));
            Action<string, string> handler = OnWarning;
            if (handler != null)
                handler(accountName, message);
        }

        private void RaiseGraceWindowStarted(string accountName)
        {
            Action<string> handler = OnGraceWindowStarted;
            if (handler != null)
                handler(accountName);
        }

        private void RaiseHardLockRequired(string accountName)
        {
            Action<string> handler = OnHardLockRequired;
            if (handler != null)
                handler(accountName);
        }

        private void RaiseRecoveryAvailable(string accountName)
        {
            Action<string> handler = OnRecoveryAvailable;
            if (handler != null)
                handler(accountName);
        }
    }
}
