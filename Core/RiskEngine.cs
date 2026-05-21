using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using NinjaTrader.Cbi;
using PropFirmGuardian.Intelligence;

namespace PropFirmGuardian.Core
{
    public sealed class RiskEngine
    {
        private const double PaProfitActivationThreshold = 2600.0;
        private const double PaMinimumBalanceFloor = 50150.0;
        private readonly AccountMonitor _accountMonitor;
        private readonly ConcurrentDictionary<string, bool> _paBufferActivated;

        public RiskEngine(AccountMonitor accountMonitor)
        {
            if (accountMonitor == null)
                throw new ArgumentNullException("accountMonitor");

            _accountMonitor = accountMonitor;
            _paBufferActivated = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }

        public event Action<string, string, double> OnBreachDetected;

        public List<RuleBreach> EvaluateRules(string accountName)
        {
            List<RuleBreach> breaches = new List<RuleBreach>();

            AccountMonitor.AccountMonitorState state;
            if (!_accountMonitor.TryGetAccountState(accountName, out state))
            {
                Debug.WriteLine(string.Format("[RISK] Account state not found: {0}", accountName));
                return breaches;
            }

            lock (state.LockObject)
            {
                Account account = state.AccountRef;
                if (account == null || state.Config == null || state.Snapshot == null)
                {
                    Debug.WriteLine(string.Format("[RISK] Account state incomplete: {0}", accountName));
                    return breaches;
                }

                PropFirmProfile profile = PropFirmPresets.GetPreset(state.Config.PropFirmName);
                double safetyBuffer = state.Config.SafetyBuffer > 0.0 ? state.Config.SafetyBuffer : 25.0;
                double realizedPnL = state.Snapshot.CurrentRealizedPnL;
                double currentUnrealizedPnL = ReadUnrealizedPnL(account);
                double currentTotalPnL = realizedPnL + currentUnrealizedPnL;
                double currentBalance = ReadBalance(account);
                double maxPositionQuantity = ReadMaxAbsolutePositionQuantity(account);

                Debug.WriteLine(string.Format(
                    "[RISK] Checking account {0}: realized={1}, peak={2}, current={3}",
                    accountName,
                    realizedPnL,
                    state.Snapshot.PeakUnrealizedPnL,
                    currentUnrealizedPnL));

                // Rule precedence is intentional: environmental locks first, then exposure
                // controls, then firm-loss math. This prevents a softer target condition from
                // masking a hard breach that requires immediate flattening.
                bool newsBreached = state.Snapshot.NewsLockoutActive;
                LogRuleCheck("NewsLockout", 0.0, newsBreached ? 1.0 : 0.0, newsBreached);
                if (newsBreached)
                {
                    AddBreach(breaches, new RuleBreach
                    {
                        RuleType = "NewsLockout",
                        AccountName = accountName,
                        BreachAmount = 0.0,
                        LimitValue = 0.0,
                        Description = "News lockout is active for this account.",
                        IsHardBreach = true
                    });
                }

                bool maxPositionBreached = state.Config.MaxPositionSize > 0.0 && maxPositionQuantity > state.Config.MaxPositionSize;
                LogRuleCheck("MaxPositionSize", state.Config.MaxPositionSize, maxPositionQuantity, maxPositionBreached);
                if (maxPositionBreached)
                {
                    AddBreach(breaches, new RuleBreach
                    {
                        RuleType = "MaxPositionSize",
                        AccountName = accountName,
                        BreachAmount = maxPositionQuantity - state.Config.MaxPositionSize,
                        LimitValue = state.Config.MaxPositionSize,
                        Description = "Open position size exceeds the configured account maximum.",
                        IsHardBreach = true
                    });
                }

                bool paBufferIsActive = IsPaBufferActive(accountName, state.Config.IsLivePA, realizedPnL);
                bool paBufferBreached = paBufferIsActive && currentBalance < PaMinimumBalanceFloor + safetyBuffer;
                LogRuleCheck("PABufferGuard", PaMinimumBalanceFloor + safetyBuffer, currentBalance, paBufferBreached);
                if (paBufferBreached)
                {
                    AddBreach(breaches, new RuleBreach
                    {
                        RuleType = "PABufferGuard",
                        AccountName = accountName,
                        BreachAmount = (PaMinimumBalanceFloor + safetyBuffer) - currentBalance,
                        LimitValue = PaMinimumBalanceFloor,
                        Description = "Live PA buffer floor is active after profit threshold was reached.",
                        IsHardBreach = true
                    });
                }

                double dailyLossLimit = ResolveDailyLossLimit(state, profile, currentBalance);
                double effectiveDailyLossLimit = dailyLossLimit > 0.0 ? Math.Max(0.0, dailyLossLimit - safetyBuffer) : 0.0;
                bool dailyLossBreached = dailyLossLimit > 0.0 && realizedPnL < -effectiveDailyLossLimit;
                LogRuleCheck("DailyLossLimit", effectiveDailyLossLimit, realizedPnL, dailyLossBreached);
                if (dailyLossLimit > 0.0)
                {
                    // The safety buffer moves every hard line inward. Triggering before the
                    // exact firm limit absorbs commission rounding, slippage, and one-tick
                    // timing differences between NT8 callbacks and broker state.
                    if (dailyLossBreached)
                    {
                        AddBreach(breaches, new RuleBreach
                        {
                            RuleType = "DailyLossLimit",
                            AccountName = accountName,
                            BreachAmount = Math.Abs(realizedPnL) - effectiveDailyLossLimit,
                            LimitValue = dailyLossLimit,
                            Description = "Realized session loss exceeded the buffered daily loss limit.",
                            IsHardBreach = true
                        });
                    }
                }

                double trailingDrawdown = ResolveTrailingDrawdown(state, profile, currentBalance);
                double effectiveTrailingLimit = trailingDrawdown > 0.0 ? Math.Max(0.0, trailingDrawdown - safetyBuffer) : 0.0;
                double drawdownFromPeak = state.Snapshot.PeakUnrealizedPnL - currentTotalPnL;
                bool trailingBreached = trailingDrawdown > 0.0 && drawdownFromPeak >= effectiveTrailingLimit;
                LogRuleCheck("TrailingDrawdown", effectiveTrailingLimit, drawdownFromPeak, trailingBreached);
                if (trailingDrawdown > 0.0)
                {
                    if (trailingBreached)
                    {
                        AddBreach(breaches, new RuleBreach
                        {
                            RuleType = "TrailingDrawdown",
                            AccountName = accountName,
                            BreachAmount = drawdownFromPeak - effectiveTrailingLimit,
                            LimitValue = trailingDrawdown,
                            Description = "Drawdown from peak PnL exceeded the buffered trailing drawdown limit.",
                            IsHardBreach = true
                        });
                    }
                }

                bool staticBreached = false;
                double bufferedStaticFloor = 0.0;
                if (profile.StaticMaxLoss.HasValue)
                {
                    double staticFloor = profile.StaticMaxLoss.Value;
                    bufferedStaticFloor = staticFloor + safetyBuffer;
                    staticBreached = currentBalance < bufferedStaticFloor;
                    LogRuleCheck("StaticMaxLoss", bufferedStaticFloor, currentBalance, staticBreached);
                    if (staticBreached)
                    {
                        AddBreach(breaches, new RuleBreach
                        {
                            RuleType = "StaticMaxLoss",
                            AccountName = accountName,
                            BreachAmount = bufferedStaticFloor - currentBalance,
                            LimitValue = staticFloor,
                            Description = "Account balance is below the buffered static max loss floor.",
                            IsHardBreach = true
                        });
                    }
                }
                else
                {
                    LogRuleCheck("StaticMaxLoss", 0.0, currentBalance, false);
                }

                double effectiveProfitTarget = profile.ProfitTarget.HasValue ? Math.Max(0.0, profile.ProfitTarget.Value - safetyBuffer) : 0.0;
                bool profitTargetBreached = profile.ProfitTarget.HasValue && realizedPnL >= effectiveProfitTarget;
                LogRuleCheck("ProfitTarget", effectiveProfitTarget, realizedPnL, profitTargetBreached);
                if (profile.ProfitTarget.HasValue)
                {
                    if (profitTargetBreached)
                    {
                        AddBreach(breaches, new RuleBreach
                        {
                            RuleType = "ProfitTarget",
                            AccountName = accountName,
                            BreachAmount = realizedPnL - effectiveProfitTarget,
                            LimitValue = profile.ProfitTarget.Value,
                            Description = "Profit target reached; soft lockout is available for payout protection.",
                            IsHardBreach = false
                        });
                    }
                }
            }

            return breaches;
        }

        public bool ShouldFlatten(string accountName)
        {
            List<RuleBreach> breaches = EvaluateRules(accountName);
            foreach (RuleBreach breach in breaches)
            {
                if (breach.IsHardBreach)
                    return true;
            }

            return false;
        }

        private void AddBreach(List<RuleBreach> breaches, RuleBreach breach)
        {
            breaches.Add(breach);
            Debug.WriteLine(string.Format(
                "[RISK] BREACH DETECTED: {0} | {1} | amount={2}",
                breach.AccountName,
                breach.RuleType,
                breach.BreachAmount));

            Action<string, string, double> handler = OnBreachDetected;
            if (handler != null)
                handler(breach.AccountName, breach.RuleType, breach.BreachAmount);
        }

        private static void LogRuleCheck(string ruleName, double limitValue, double currentValue, bool breached)
        {
            Debug.WriteLine(string.Format(
                "[RISK] Rule check: {0} | limit={1} | current={2} | breached={3}",
                ruleName,
                limitValue,
                currentValue,
                breached));
        }

        private bool IsPaBufferActive(string accountName, bool isLivePA, double realizedPnL)
        {
            if (!isLivePA)
                return false;

            if (realizedPnL > PaProfitActivationThreshold)
            {
                _paBufferActivated[accountName] = true;
                return true;
            }

            bool activated;
            return _paBufferActivated.TryGetValue(accountName, out activated) && activated;
        }

        private static double ResolveDailyLossLimit(AccountMonitor.AccountMonitorState state, PropFirmProfile profile, double currentBalance)
        {
            if (state.Config.DailyLossLimit > 0.0)
                return state.Config.DailyLossLimit;

            if (profile.DailyLossLimit.HasValue)
                return ResolveAmountOrPercent(profile.DailyLossLimit.Value, currentBalance);

            return 0.0;
        }

        private static double ResolveTrailingDrawdown(AccountMonitor.AccountMonitorState state, PropFirmProfile profile, double currentBalance)
        {
            if (state.Config.TrailingDrawdown > 0.0)
                return state.Config.TrailingDrawdown;

            if (profile.TrailingDrawdownAmount.HasValue)
                return ResolveAmountOrPercent(profile.TrailingDrawdownAmount.Value, currentBalance);

            return 0.0;
        }

        private static double ResolveAmountOrPercent(double value, double currentBalance)
        {
            if (value > 0.0 && value < 1.0)
                return currentBalance * value;

            return value;
        }

        private static double ReadMaxAbsolutePositionQuantity(Account account)
        {
            double maxQuantity = 0.0;

            foreach (Position position in account.Positions)
            {
                if (position == null || position.MarketPosition == MarketPosition.Flat)
                    continue;

                maxQuantity = Math.Max(maxQuantity, Math.Abs(position.Quantity));
            }

            return maxQuantity;
        }

        private static double ReadUnrealizedPnL(Account account)
        {
            double unrealizedPnL = 0.0;

            foreach (Position position in account.Positions)
            {
                if (position == null || position.MarketPosition == MarketPosition.Flat)
                    continue;

                double marketPrice = position.GetMarketPrice();
                unrealizedPnL += position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, marketPrice);
            }

            return unrealizedPnL;
        }

        private static double ReadBalance(Account account)
        {
            double netLiquidation = ReadAccountItem(account, AccountItem.NetLiquidation);
            if (netLiquidation > 0.0)
                return netLiquidation;

            return ReadAccountItem(account, AccountItem.CashValue);
        }

        private static double ReadAccountItem(Account account, AccountItem accountItem)
        {
            try
            {
                return account.Get(accountItem, account.Denomination);
            }
            catch
            {
                return 0.0;
            }
        }
    }

    public sealed class RuleBreach
    {
        public string RuleType { get; set; }
        public string AccountName { get; set; }
        public double BreachAmount { get; set; }
        public double LimitValue { get; set; }
        public string Description { get; set; }
        public bool IsHardBreach { get; set; }
    }
}
