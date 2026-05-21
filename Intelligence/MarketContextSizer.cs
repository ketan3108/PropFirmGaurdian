using System;
using NinjaTrader.Cbi;

namespace PropFirmGuardian.Intelligence
{
    public sealed class MarketContextSizer
    {
        private MarketRegime _lastRegime;
        private string _lastReason;

        public MarketContextSizer()
        {
            _lastRegime = MarketRegime.Unknown;
            _lastReason = "No market context available yet.";
        }

        public SizeSuggestion GetSizeSuggestion(Account account, Instrument instrument)
        {
            if (instrument == null)
                return new SizeSuggestion(100, MarketRegime.Unknown, "No instrument selected.");

            return new SizeSuggestion(GetSizePercent(_lastRegime), _lastRegime, _lastReason);
        }

        public void UpdateRegime(double atr, double atrAverage, double adx, bool bollingerContracting)
        {
            if (adx > 25.0 && atr >= atrAverage)
            {
                _lastRegime = MarketRegime.Trending;
                _lastReason = "Market is moving. Edge is present.";
            }
            else if (adx < 20.0 && atrAverage > 0.0 && atr < atrAverage * 0.80)
            {
                _lastRegime = MarketRegime.Choppy;
                _lastReason = "Choppy conditions. Preserve capital.";
            }
            else if (bollingerContracting)
            {
                _lastRegime = MarketRegime.Ranging;
                _lastReason = "Range compression. Breakout risk elevated.";
            }
            else
            {
                _lastRegime = MarketRegime.Normal;
                _lastReason = "Normal market context.";
            }
        }

        public string GetRegimeReason()
        {
            return _lastReason;
        }

        private static int GetSizePercent(MarketRegime regime)
        {
            if (regime == MarketRegime.Choppy)
                return 50;
            if (regime == MarketRegime.Ranging)
                return 75;
            return 100;
        }
    }

    public enum MarketRegime
    {
        Unknown,
        Normal,
        Trending,
        Choppy,
        Ranging
    }

    public sealed class SizeSuggestion
    {
        public SizeSuggestion(int sizePercent, MarketRegime regime, string reason)
        {
            SizePercent = sizePercent;
            Regime = regime;
            Reason = reason ?? string.Empty;
        }

        public int SizePercent { get; private set; }
        public MarketRegime Regime { get; private set; }
        public string Reason { get; private set; }
    }
}
