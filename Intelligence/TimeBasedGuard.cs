using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using PropFirmGuardian.Core;
using PropFirmGuardian.Models;
using PropFirmGuardian.Utils;

namespace PropFirmGuardian.Intelligence
{
    public sealed class TimeBasedGuard : IDisposable
    {
        private readonly AccountMonitor _accountMonitor;
        private readonly Timer _timer;
        private int _isChecking;
        private bool _isDisposed;

        public TimeBasedGuard(AccountMonitor accountMonitor)
        {
            if (accountMonitor == null)
                throw new ArgumentNullException("accountMonitor");

            _accountMonitor = accountMonitor;
            _timer = new Timer(OnTimerTick, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60));
        }

        public event Action<string, string> OnGuardActivated;

        public void CheckGuards()
        {
            DateTime easternNow = TimeZoneHelper.FromUtc(DateTime.UtcNow);

            foreach (AccountMonitor.AccountMonitorState state in _accountMonitor.GetAccountStates())
            {
                if (state == null || state.Config == null || state.Snapshot == null || state.Config.IsExcluded)
                    continue;

                string activeGuard = GetActiveGuard(state.Config, easternNow);
                lock (state.LockObject)
                {
                    state.Snapshot.ActiveGuards = activeGuard;
                    bool isEnforcedGuard = string.Equals(activeGuard, "Weekend Guard", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(activeGuard, "End of Session Guard", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(activeGuard, "Overnight Guard", StringComparison.OrdinalIgnoreCase);

                    if (!string.IsNullOrEmpty(activeGuard) && state.Config.IsLivePA && isEnforcedGuard)
                    {
                        state.Snapshot.LastKnownStatus = AccountState.Warning;
                        state.Snapshot.LockReason = activeGuard;
                    }
                }

                if (!string.IsNullOrEmpty(activeGuard))
                    RaiseGuardActivated(state.Snapshot.AccountName, activeGuard);
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            _timer.Dispose();
        }

        private static string GetActiveGuard(AccountConfig config, DateTime easternNow)
        {
            TimeSpan time = easternNow.TimeOfDay;

            if (config.WeekendGuardEnabled && easternNow.DayOfWeek == DayOfWeek.Friday && time >= new TimeSpan(15, 0, 0) && !config.WeekendOverrideAccepted)
                return "Weekend Guard";

            if (config.EndOfSessionGuardEnabled && time >= new TimeSpan(15, 30, 0) && time <= new TimeSpan(17, 0, 0))
                return "End of Session Guard";

            if (config.OvernightGuardEnabled && time >= new TimeSpan(17, 0, 0))
                return "Overnight Guard";

            if (config.PreMarketGuardEnabled && time < new TimeSpan(9, 30, 0))
                return "Pre-Market Guard";

            if (config.LunchGuardMode != GuardMode.Off && time >= config.LunchStartTime && time <= config.LunchEndTime)
                return "Lunch Guard";

            return string.Empty;
        }

        private void OnTimerTick(object state)
        {
            if (_isDisposed || Interlocked.Exchange(ref _isChecking, 1) == 1)
                return;

            try
            {
                CheckGuards();
            }
            catch (Exception exception)
            {
                Debug.WriteLine(string.Format("[GUARD] Time guard check failed: {0}", exception.Message));
            }
            finally
            {
                Interlocked.Exchange(ref _isChecking, 0);
            }
        }

        private void RaiseGuardActivated(string accountName, string guardName)
        {
            Action<string, string> handler = OnGuardActivated;
            if (handler != null)
                handler(accountName, guardName);
        }
    }
}
