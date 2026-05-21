using System;

namespace PropFirmGuardian.News
{
    public class NewsEvent
    {
        public DateTime EventTimeUtc { get; set; }
        public string Title { get; set; }
        public string Currency { get; set; }
        public string Impact { get; set; }
        public double? Forecast { get; set; }
        public double? Previous { get; set; }
        public double? Actual { get; set; }
        public bool IsDeviationAnalyzed { get; set; }
        public double? DeviationScore { get; set; }
    }
}
