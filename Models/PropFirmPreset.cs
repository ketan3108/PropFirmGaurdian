using System;

namespace PropFirmGuardian.Models
{
    public sealed class PropFirmPreset
    {
        public string Name { get; set; }
        public double ProfitTarget { get; set; }
        public double DailyLossLimit { get; set; }
        public double MaxDrawdown { get; set; }
        public double ConsistencyThreshold { get; set; }
        public int RequiredTradingDays { get; set; }
        public TimeSpan SessionResetTime { get; set; }
        public string AllowedInstrumentsCsv { get; set; }
    }
}
