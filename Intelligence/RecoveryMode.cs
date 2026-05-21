using System;
using PropFirmGuardian.Core;
using PropFirmGuardian.Models;

namespace PropFirmGuardian.Intelligence
{
    public sealed class RecoveryMode
    {
        private readonly AccountMonitor _accountMonitor;

        public RecoveryMode(AccountMonitor accountMonitor)
        {
            if (accountMonitor == null)
                throw new ArgumentNullException("accountMonitor");

            _accountMonitor = accountMonitor;
        }

        public bool TryStart(string accountName)
        {
            AccountMonitor.AccountMonitorState state;
            if (!_accountMonitor.TryGetAccountState(accountName, out state) || state.Config == null)
                return false;

            lock (state.LockObject)
            {
                if (!state.Config.RecoveryModeEnabled || state.Snapshot.RecoveryModeUsed)
                    return false;

                state.Snapshot.LastKnownStatus = AccountState.RecoveryMode;
                state.Snapshot.RecoveryModeOffered = true;
                state.Snapshot.LockedUntil = DateTime.Now.AddMinutes(15);
                state.Snapshot.LastUpdateTime = DateTime.UtcNow;
                return true;
            }
        }

        public bool ResumeWithHalfSize(string accountName, string journalText)
        {
            AccountMonitor.AccountMonitorState state;
            if (!_accountMonitor.TryGetAccountState(accountName, out state))
                return false;

            lock (state.LockObject)
            {
                if (state.Snapshot.RecoveryModeUsed)
                    return false;

                state.Snapshot.RecoveryJournalText = journalText ?? string.Empty;
                state.Snapshot.RecoveryModeUsed = true;
                state.Snapshot.LastKnownStatus = AccountState.Active;
                state.Snapshot.LockReason = "Recovery mode: 50% size cap";
                state.Snapshot.LastUpdateTime = DateTime.UtcNow;
                return true;
            }
        }
    }
}
