using System;

namespace PropFirmGuardian.Models
{
    public class SessionSnapshot
    {
        public string AccountName { get; set; }
        public double PeakUnrealizedPnL { get; set; }
        public double SessionStartBalance { get; set; }
        public double CurrentRealizedPnL { get; set; }
        public DateTime? LockedUntil { get; set; }
        public AccountState LastKnownStatus { get; set; }
        public string LockReason { get; set; }
        public bool NewsLockoutActive { get; set; }
        public bool IsHardLock { get; set; }
        public DateTime LastUpdateTime { get; set; }
        public int TradesToday { get; set; }
        public int DailyTradeLimit { get; set; }
        public bool EmergencyOverrideUsed { get; set; }
        public int EmergencyOverrideTradesRemaining { get; set; }
        public double SessionQualityScore { get; set; }
        public string SessionQualityMessage { get; set; }
        public string ActiveGuards { get; set; }
        public double PassProbability { get; set; }
        public double RequiredDailyAverage { get; set; }
        public DateTime EvalStartDateUtc { get; set; }
        public bool GraceWindowActive { get; set; }
        public DateTime? GraceWindowEndsUtc { get; set; }
        public bool RecoveryModeOffered { get; set; }
        public bool RecoveryModeUsed { get; set; }
        public string RecoveryJournalText { get; set; }
        public double ActualPnL { get; set; }
        public double GuardianModePnL { get; set; }
        public double LargestTradePercent { get; set; }
        public double GrossProfit { get; set; }
        public double GrossLoss { get; set; }
        public int ConsecutiveLosses { get; set; }
    }
}
