using System;

namespace PropFirmGuardian.Models
{
    public class AccountConfig
    {
        public AccountConfig()
        {
            SafetyBuffer = 25.0;
            HardEnforcementEnabled = true;
            DailyTradeLimit = 10;
            EnableDailyLimit = true;
            AllowEmergencyOverride = true;
            EmergencyOverrideTrades = 2;
            ProfitTarget = 3000.0;
            MaxDrawdown = 2500.0;
            ConsistencyThreshold = 0.30;
            RequiredTradingDays = 8;
            TradeCap = 12;
            RecoveryModeEnabled = true;
            SizeSuggestionEnabled = true;
            PreFlightEnabled = true;
            LunchRhythmMode = RhythmGuardMode.Suggest;
            EndOfSessionRhythmMode = RhythmGuardMode.Warn;
            WeekendRhythmMode = RhythmGuardMode.Enforce;
            NewsRhythmMode = RhythmGuardMode.Warn;
            LunchGuardMode = GuardMode.Warning;
            EndOfSessionGuardEnabled = false;
            WeekendGuardEnabled = false;
            OvernightGuardEnabled = false;
            PreMarketGuardEnabled = false;
            LunchStartTime = new TimeSpan(12, 0, 0);
            LunchEndTime = new TimeSpan(13, 30, 0);
        }

        public string AccountName { get; set; }
        public string PropFirmName { get; set; }
        public double DailyLossLimit { get; set; }
        public double TrailingDrawdown { get; set; }
        public double MaxPositionSize { get; set; }
        public double SafetyBuffer { get; set; }
        public bool IsLivePA { get; set; }
        public bool IsEval { get; set; }
        public TimeSpan SessionResetTime { get; set; }
        public bool IsExcluded { get; set; }
        public bool ShadowModeEnabled { get; set; }
        public bool HardEnforcementEnabled { get; set; }
        public int DailyTradeLimit { get; set; }
        public bool EnableDailyLimit { get; set; }
        public bool AllowEmergencyOverride { get; set; }
        public int EmergencyOverrideTrades { get; set; }
        public GuardMode LunchGuardMode { get; set; }
        public TimeSpan LunchStartTime { get; set; }
        public TimeSpan LunchEndTime { get; set; }
        public bool EndOfSessionGuardEnabled { get; set; }
        public bool WeekendGuardEnabled { get; set; }
        public bool WeekendOverrideAccepted { get; set; }
        public bool OvernightGuardEnabled { get; set; }
        public bool PreMarketGuardEnabled { get; set; }
        public double ProfitTarget { get; set; }
        public double MaxDrawdown { get; set; }
        public double ConsistencyThreshold { get; set; }
        public int RequiredTradingDays { get; set; }
        public int TradeCap { get; set; }
        public bool RecoveryModeEnabled { get; set; }
        public bool RecoveryUsedToday { get; set; }
        public bool SizeSuggestionEnabled { get; set; }
        public bool PreFlightEnabled { get; set; }
        public RhythmGuardMode LunchRhythmMode { get; set; }
        public RhythmGuardMode EndOfSessionRhythmMode { get; set; }
        public RhythmGuardMode WeekendRhythmMode { get; set; }
        public RhythmGuardMode NewsRhythmMode { get; set; }
    }

    public enum GuardMode
    {
        Off,
        Warning,
        ReducedSize,
        Lockout
    }

    public enum RhythmGuardMode
    {
        Off,
        Suggest,
        Warn,
        Enforce
    }
}
