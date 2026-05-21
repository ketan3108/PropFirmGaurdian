using System;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace PropFirmGuardian.News
{
    public sealed class DeviationAnalyzer
    {
        private static readonly ConcurrentDictionary<string, bool> ExplosiveHistory =
            new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        public void AnalyzeEvent(NewsEvent newsEvent)
        {
            if (newsEvent == null)
                throw new ArgumentNullException("newsEvent");

            if (newsEvent.Actual.HasValue && newsEvent.Forecast.HasValue && Math.Abs(newsEvent.Forecast.Value) > double.Epsilon)
            {
                double deviation = (newsEvent.Actual.Value - newsEvent.Forecast.Value) / Math.Abs(newsEvent.Forecast.Value);
                newsEvent.DeviationScore = deviation;
                Debug.WriteLine(string.Format(
                    "[NEWS] Deviation analysis: event={0} | forecast={1} | actual={2} | deviation={3}",
                    newsEvent.Title,
                    newsEvent.Forecast.Value,
                    newsEvent.Actual.Value,
                    deviation));

                if (Math.Abs(deviation) > 2.0)
                    ExplosiveHistory[GetHistoryKey(newsEvent)] = true;
            }

            newsEvent.IsDeviationAnalyzed = true;
        }

        public string GetRiskLevel(NewsEvent newsEvent)
        {
            if (newsEvent == null)
                return "Normal";

            if (string.Equals(newsEvent.Impact, "High", StringComparison.OrdinalIgnoreCase)
                && !newsEvent.DeviationScore.HasValue)
            {
                return "High";
            }

            if (string.Equals(newsEvent.Impact, "High", StringComparison.OrdinalIgnoreCase)
                && Math.Abs(newsEvent.DeviationScore.GetValueOrDefault()) > 2.0)
            {
                return "Explosive";
            }

            if (string.Equals(newsEvent.Impact, "Medium", StringComparison.OrdinalIgnoreCase)
                && Math.Abs(newsEvent.DeviationScore.GetValueOrDefault()) > 3.0)
            {
                return "High";
            }

            return "Normal";
        }

        public bool ShouldFlattenEarly(NewsEvent newsEvent)
        {
            if (newsEvent == null)
                return false;

            bool wasExplosive;
            return ExplosiveHistory.TryGetValue(GetHistoryKey(newsEvent), out wasExplosive) && wasExplosive;
        }

        private static string GetHistoryKey(NewsEvent newsEvent)
        {
            return string.Format("{0}|{1}", newsEvent.Currency ?? string.Empty, newsEvent.Title ?? string.Empty);
        }
    }
}
