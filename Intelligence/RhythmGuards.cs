using System;
using System.Threading;
using PropFirmGuardian.Core;
using PropFirmGuardian.Models;
using PropFirmGuardian.Utils;

namespace PropFirmGuardian.Intelligence
{
    public sealed class RhythmGuards : IDisposable
    {
        private readonly AccountMonitor _accountMonitor;
        private readonly Timer _timer;
        private bool _isDisposed;

        public RhythmGuards(AccountMonitor accountMonitor)
        {
            if (accountMonitor == null)
                throw new ArgumentNullException("accountMonitor");

            _accountMonitor = accountMonitor;
            _timer = new Timer(OnTimerTick, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60));
        }

        public event Action<string, string, RhythmGuardMode> OnGuardActive;

        public void Evaluate()
        {
            DateTime eastern = TimeZoneHelper.FromUtc(DateTime.UtcNow);
            foreach (AccountMonitor.AccountMonitorState state in _accountMonitor.GetAccountStates())
            {
                if (state == null || state.Config == null || state.Config.IsExcluded)
                    continue;

                GuardDecision decision = GetDecision(state.Config, eastern);
                lock (state.LockObject)
                {
                    state.Snapshot.ActiveGuards = decision.Name;
                    if (decision.Mode == RhythmGuardMode.Enforce && state.Snapshot.LastKnownStatus == AccountState.Active)
                    {
                        state.Snapshot.LastKnownStatus = AccountState.Warning;
                        state.Snapshot.LockReason = decision.Name;
                    }
                }

                if (!string.IsNullOrEmpty(decision.Name))
                    RaiseGuardActive(state.Snapshot.AccountName, decision.Name, decision.Mode);
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            _timer.Dispose();
        }

        private static GuardDecision GetDecision(AccountConfig config, DateTime eastern)
        {
            TimeSpan time = eastern.TimeOfDay;
            if (eastern.DayOfWeek == DayOfWeek.Friday && time >= new TimeSpan(15, 0, 0) && config.WeekendRhythmMode != RhythmGuardMode.Off)
                return new GuardDecision("Weekend Guard", config.WeekendRhythmMode);
            if (time >= new TimeSpan(15, 30, 0) && time <= new TimeSpan(17, 0, 0) && config.EndOfSessionRhythmMode != RhythmGuardMode.Off)
                return new GuardDecision("End of Session", config.EndOfSessionRhythmMode);
            if (time >= config.LunchStartTime && time <= config.LunchEndTime && config.LunchRhythmMode != RhythmGuardMode.Off)
                return new GuardDecision("Lunch Guard", config.LunchRhythmMode);
            return new GuardDecision(string.Empty, RhythmGuardMode.Off);
        }

        private void OnTimerTick(object state)
        {
            if (!_isDisposed)
                Evaluate();
        }

        private void RaiseGuardActive(string accountName, string guardName, RhythmGuardMode mode)
        {
            Action<string, string, RhythmGuardMode> handler = OnGuardActive;
            if (handler != null)
                handler(accountName, guardName, mode);
        }

        private struct GuardDecision
        {
            public GuardDecision(string name, RhythmGuardMode mode)
            {
                Name = name;
                Mode = mode;
            }

            public readonly string Name;
            public readonly RhythmGuardMode Mode;
        }
    }
}
