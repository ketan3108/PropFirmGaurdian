using System;
using NinjaTrader.Cbi;
using PropFirmGuardian.Core;
using PropFirmGuardian.Models;

namespace PropFirmGuardian.Intelligence
{
    public sealed class PassProbabilityEngine
    {
        private readonly AccountMonitor _accountMonitor;

        public PassProbabilityEngine(AccountMonitor accountMonitor)
        {
            if (accountMonitor == null)
                throw new ArgumentNullException("accountMonitor");

            _accountMonitor = accountMonitor;
        }

        public PassProbabilityResult GetPassProbability(Account account)
        {
            if (account == null)
                return new PassProbabilityResult(0.0, 0.0, "No account selected.");

            AccountMonitor.AccountMonitorState state;
            if (!_accountMonitor.TryGetAccountState(account.Name, out state))
                return new PassProbabilityResult(0.0, 0.0, "Account is not monitored.");

            lock (state.LockObject)
            {
                return Calculate(state.Config, state.Snapshot);
            }
        }

        public double GetRequiredDailyAverage(Account account)
        {
            return GetPassProbability(account).RequiredDailyAverage;
        }

        public PassProbabilityResult Calculate(AccountConfig config, SessionSnapshot snapshot)
        {
            if (config == null || snapshot == null)
                return new PassProbabilityResult(0.0, 0.0, "Missing account configuration.");

            double target = Math.Max(1.0, config.ProfitTarget);
            double currentPnL = snapshot.CurrentRealizedPnL;
            int requiredDays = Math.Max(1, config.RequiredTradingDays);
            DateTime start = snapshot.EvalStartDateUtc == DateTime.MinValue ? DateTime.UtcNow : snapshot.EvalStartDateUtc;
            int daysElapsed = Math.Max(1, (int)Math.Ceiling((DateTime.UtcNow.Date - start.Date).TotalDays) + 1);
            int remainingDays = Math.Max(1, requiredDays - daysElapsed + 1);
            double remainingTarget = Math.Max(0.0, target - currentPnL);
            double requiredDailyAverage = remainingTarget / remainingDays;
            double progressScore = Clamp01(currentPnL / target);
            double projectedPnL = (currentPnL / Math.Max(1, daysElapsed)) * requiredDays;
            double trajectoryScore = Clamp01(projectedPnL / target);
            double paceScore = requiredDailyAverage <= 0.0 ? 1.0 : Clamp01((target / requiredDays) / requiredDailyAverage);
            double configuredDrawdown = config.MaxDrawdown > 0.0 ? config.MaxDrawdown : config.TrailingDrawdown;
            double drawdownUsed = Math.Max(0.0, snapshot.PeakUnrealizedPnL - currentPnL);
            double drawdownPenalty = configuredDrawdown > 0.0 ? Math.Min(0.30, drawdownUsed / configuredDrawdown * 0.30) : 0.0;
            double dailyLossPenalty = config.DailyLossLimit > 0.0 && currentPnL < 0.0
                ? Math.Min(0.20, Math.Abs(currentPnL) / config.DailyLossLimit * 0.20)
                : 0.0;
            double consistencyPenalty = snapshot.LargestTradePercent > config.ConsistencyThreshold * 100.0 ? 0.15 : 0.0;
            double probability = (progressScore * 0.35 + trajectoryScore * 0.30 + paceScore * 0.35 - drawdownPenalty - dailyLossPenalty - consistencyPenalty) * 100.0;
            probability = Math.Max(0.0, Math.Min(100.0, probability));
            string tooltip = string.Format("{0} {1:0}. {2:0}% pass chance. Need +${3:0}/day. Target ${4:0}, DD ${5:0}.",
                config.PropFirmName,
                config.AccountSize,
                probability,
                requiredDailyAverage,
                target,
                configuredDrawdown);
            return new PassProbabilityResult(probability, requiredDailyAverage, tooltip);
        }

        private static double Clamp01(double value)
        {
            return Math.Max(0.0, Math.Min(1.0, value));
        }
    }

    public sealed class PassProbabilityResult
    {
        public PassProbabilityResult(double probability, double requiredDailyAverage, string tooltip)
        {
            Probability = probability;
            RequiredDailyAverage = requiredDailyAverage;
            Tooltip = tooltip ?? string.Empty;
        }

        public double Probability { get; private set; }
        public double RequiredDailyAverage { get; private set; }
        public string Tooltip { get; private set; }
    }
}
