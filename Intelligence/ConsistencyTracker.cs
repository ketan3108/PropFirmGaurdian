using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using PropFirmGuardian.Models;

namespace PropFirmGuardian.Intelligence
{
    public sealed class ConsistencyTracker
    {
        private readonly ConcurrentDictionary<string, List<double>> _scoreHistory;

        public ConsistencyTracker()
        {
            _scoreHistory = new ConcurrentDictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
        }

        public SessionQualityResult Calculate(string accountName, IReadOnlyList<TradeRecord> trades)
        {
            if (trades == null || trades.Count == 0)
                return Store(accountName, new SessionQualityResult(50.0, "No closed trades yet.", "Stable", new double[0]));

            int total = trades.Count;
            int wins = trades.Count(trade => trade.RealizedPnL > 0.0);
            double grossWins = trades.Where(trade => trade.RealizedPnL > 0.0).Sum(trade => trade.RealizedPnL);
            double grossLosses = Math.Abs(trades.Where(trade => trade.RealizedPnL < 0.0).Sum(trade => trade.RealizedPnL));
            double totalPnL = trades.Sum(trade => trade.RealizedPnL);
            double largestTrade = trades.Max(trade => Math.Abs(trade.RealizedPnL));
            int consecutiveLosses = CountConsecutiveLosses(trades);
            double winRate = wins * 100.0 / total;
            double profitFactor = grossLosses > 0.0 ? grossWins / grossLosses : grossWins > 0.0 ? 3.0 : 0.0;
            double largestTradePercent = Math.Abs(totalPnL) > double.Epsilon ? largestTrade / Math.Abs(totalPnL) * 100.0 : 100.0;
            double averageHoldSeconds = trades.Where(trade => trade.ExitTime.HasValue).Select(trade => (trade.ExitTime.Value - trade.EntryTime).TotalSeconds).DefaultIfEmpty(180.0).Average();
            double timeSinceLastTrade = trades[trades.Count - 1].ExitTime.HasValue ? (DateTime.Now - trades[trades.Count - 1].ExitTime.Value).TotalSeconds : 0.0;

            double score = 50.0;
            score += Math.Min(20.0, winRate * 0.20);
            score += Math.Max(-15.0, Math.Min(15.0, (profitFactor - 1.0) * 10.0));
            score -= largestTradePercent > 30.0 ? largestTradePercent * 0.25 : 0.0;
            score -= consecutiveLosses > 2 ? consecutiveLosses * 5.0 : 0.0;
            score -= Math.Abs(averageHoldSeconds - 180.0) * 0.02;
            score -= timeSinceLastTrade > 300.0 ? timeSinceLastTrade * 0.001 : 0.0;
            score = Math.Max(0.0, Math.Min(100.0, score));

            string message = BuildMessage(score, winRate, largestTradePercent, consecutiveLosses);
            string trend = CalculateTrend(accountName, score);
            return Store(accountName, new SessionQualityResult(score, message, trend, GetSparkline(accountName)));
        }

        private SessionQualityResult Store(string accountName, SessionQualityResult result)
        {
            List<double> scores = _scoreHistory.GetOrAdd(accountName ?? string.Empty, name => new List<double>());
            lock (scores)
            {
                scores.Add(result.Score);
                while (scores.Count > 10)
                    scores.RemoveAt(0);
            }

            return result;
        }

        private static int CountConsecutiveLosses(IReadOnlyList<TradeRecord> trades)
        {
            int count = 0;
            for (int index = trades.Count - 1; index >= 0; index--)
            {
                if (trades[index].RealizedPnL >= 0.0)
                    break;
                count++;
            }

            return count;
        }

        private static string BuildMessage(double score, double winRate, double largestTradePercent, int consecutiveLosses)
        {
            if (consecutiveLosses > 2)
                return string.Format("Losing streak: {0} trades.", consecutiveLosses);
            if (largestTradePercent > 30.0)
                return string.Format("Largest trade concentration: {0:0}%.", largestTradePercent);
            if (winRate < 45.0)
                return string.Format("Win rate declining: {0:0}%.", winRate);
            if (score >= 80.0)
                return "Edge confirmed. Maintain discipline.";
            if (score >= 60.0)
                return "Solid session. Stay focused.";
            if (score >= 40.0)
                return "Tighten risk. Consider smaller size.";
            if (score >= 20.0)
                return "Stop trading recommended.";
            return "Data says stop.";
        }

        private string CalculateTrend(string accountName, double currentScore)
        {
            double[] values = GetSparkline(accountName);
            if (values.Length < 3)
                return "Stable";

            double previous = values.Take(Math.Max(1, values.Length - 3)).DefaultIfEmpty(currentScore).Average();
            double recent = values.Skip(Math.Max(0, values.Length - 3)).Average();
            if (recent > previous + 3.0)
                return "Improving";
            if (recent < previous - 3.0)
                return "Declining";
            return "Stable";
        }

        private double[] GetSparkline(string accountName)
        {
            List<double> scores;
            if (!_scoreHistory.TryGetValue(accountName ?? string.Empty, out scores))
                return new double[0];

            lock (scores)
                return scores.ToArray();
        }
    }

    public sealed class SessionQualityResult
    {
        public SessionQualityResult(double score, string message, string trend, double[] sparkline)
        {
            Score = score;
            Message = message;
            Trend = trend;
            Sparkline = sparkline ?? new double[0];
        }

        public double Score { get; private set; }
        public string Message { get; private set; }
        public string Trend { get; private set; }
        public double[] Sparkline { get; private set; }
    }
}
