using System;
using System.Collections.Generic;
using System.Linq;

namespace PropFirmGuardian.Intelligence
{
    public sealed class PropFirmProfile
    {
        public string FirmName { get; set; }
        public string PresetName { get; set; }
        public string ProgramType { get; set; }
        public double AccountSize { get; set; }
        public string RuleType { get; set; }
        public double? TrailingDrawdownAmount { get; set; }
        public double? DailyLossLimit { get; set; }
        public double? StaticMaxLoss { get; set; }
        public double? ProfitTarget { get; set; }
        public int? MaxPositionSize { get; set; }
        public int DailyTradeLimit { get; set; }
        public double ConsistencyThreshold { get; set; }
        public int RequiredTradingDays { get; set; }
        public TimeSpan SessionResetTime { get; set; }
        public bool CountsCommissionsInDrawdown { get; set; }
        public string Description { get; set; }
    }

    public static class PropFirmPresets
    {
        private static readonly Dictionary<string, PropFirmProfile> Profiles;

        static PropFirmPresets()
        {
            Profiles = new Dictionary<string, PropFirmProfile>(StringComparer.OrdinalIgnoreCase)
            {
                { "Apex 25K Eval", Futures("Apex", "Apex 25K Eval", "Eval", 25000, "Trailing", 1500, null, null, 1500, 4, 10, 0.30, 7, new TimeSpan(17, 0, 0), true) },
                { "Apex 50K Eval", Futures("Apex", "Apex 50K Eval", "Eval", 50000, "Trailing", 2500, null, null, 3000, 10, 12, 0.30, 7, new TimeSpan(17, 0, 0), true) },
                { "Apex 100K Eval", Futures("Apex", "Apex 100K Eval", "Eval", 100000, "Trailing", 3000, null, null, 6000, 14, 12, 0.30, 7, new TimeSpan(17, 0, 0), true) },
                { "Apex PA 50K", Futures("Apex", "Apex PA 50K", "LivePA", 50000, "Trailing", 2500, null, null, 0, 10, 10, 0.30, 1, new TimeSpan(17, 0, 0), true) },
                { "TakeProfitTrader 50K Eval", Futures("TakeProfitTrader", "TakeProfitTrader 50K Eval", "Eval", 50000, "Trailing", 2000, 1100, null, 3000, 5, 12, 0.30, 5, new TimeSpan(17, 0, 0), true) },
                { "TakeProfitTrader 100K Eval", Futures("TakeProfitTrader", "TakeProfitTrader 100K Eval", "Eval", 100000, "Trailing", 3000, 2200, null, 6000, 10, 12, 0.30, 5, new TimeSpan(17, 0, 0), true) },
                { "Tradeify 50K Eval", Futures("Tradeify", "Tradeify 50K Eval", "Eval", 50000, "Trailing", 2000, 1000, null, 3000, 5, 12, 0.30, 5, new TimeSpan(17, 0, 0), true) },
                { "Tradeify 100K Eval", Futures("Tradeify", "Tradeify 100K Eval", "Eval", 100000, "Trailing", 3000, 2000, null, 6000, 10, 12, 0.30, 5, new TimeSpan(17, 0, 0), true) },
                {
                    "Apex",
                    new PropFirmProfile
                    {
                        FirmName = "Apex",
                        PresetName = "Apex",
                        ProgramType = "Eval",
                        AccountSize = 50000.0,
                        RuleType = "Trailing",
                        TrailingDrawdownAmount = 2500.0,
                        DailyLossLimit = null,
                        StaticMaxLoss = null,
                        ProfitTarget = null,
                        MaxPositionSize = 10,
                        DailyTradeLimit = 12,
                        ConsistencyThreshold = 0.30,
                        RequiredTradingDays = 7,
                        SessionResetTime = new TimeSpan(17, 0, 0),
                        CountsCommissionsInDrawdown = true,
                        Description = "Trailing drawdown model with 5 PM ET session reset and commissions included in drawdown."
                    }
                },
                {
                    "Topstep",
                    new PropFirmProfile
                    {
                        FirmName = "Topstep",
                        PresetName = "Topstep",
                        ProgramType = "Eval",
                        AccountSize = 50000.0,
                        RuleType = "DailyLoss",
                        TrailingDrawdownAmount = null,
                        DailyLossLimit = 1000.0,
                        StaticMaxLoss = null,
                        ProfitTarget = null,
                        MaxPositionSize = 5,
                        DailyTradeLimit = 12,
                        ConsistencyThreshold = 0.30,
                        RequiredTradingDays = 5,
                        SessionResetTime = TimeSpan.Zero,
                        CountsCommissionsInDrawdown = false,
                        Description = "Daily loss model with midnight ET reset and commissions excluded from the preset drawdown calculation."
                    }
                },
                {
                    "Leeloo",
                    new PropFirmProfile
                    {
                        FirmName = "Leeloo",
                        PresetName = "Leeloo",
                        ProgramType = "Eval",
                        AccountSize = 50000.0,
                        RuleType = "Hybrid",
                        TrailingDrawdownAmount = 2500.0,
                        DailyLossLimit = 1000.0,
                        StaticMaxLoss = null,
                        ProfitTarget = null,
                        MaxPositionSize = 10,
                        DailyTradeLimit = 12,
                        ConsistencyThreshold = 0.30,
                        RequiredTradingDays = 7,
                        SessionResetTime = new TimeSpan(17, 0, 0),
                        CountsCommissionsInDrawdown = true,
                        Description = "Hybrid trailing and daily loss model with 5 PM ET reset."
                    }
                },
                {
                    "FTMO",
                    new PropFirmProfile
                    {
                        FirmName = "FTMO",
                        PresetName = "FTMO",
                        ProgramType = "Eval",
                        AccountSize = 50000.0,
                        RuleType = "Static",
                        TrailingDrawdownAmount = null,
                        DailyLossLimit = null,
                        StaticMaxLoss = 5000.0,
                        ProfitTarget = 5000.0,
                        MaxPositionSize = null,
                        DailyTradeLimit = 12,
                        ConsistencyThreshold = 0.30,
                        RequiredTradingDays = 5,
                        SessionResetTime = TimeSpan.Zero,
                        CountsCommissionsInDrawdown = true,
                        Description = "Static max loss profile with matching profit target and consistency-rule awareness."
                    }
                },
                {
                    "The5ers",
                    new PropFirmProfile
                    {
                        FirmName = "The5ers",
                        PresetName = "The5ers",
                        ProgramType = "Eval",
                        AccountSize = 50000.0,
                        RuleType = "Hybrid",
                        TrailingDrawdownAmount = 0.10,
                        DailyLossLimit = 0.05,
                        StaticMaxLoss = null,
                        ProfitTarget = null,
                        MaxPositionSize = null,
                        DailyTradeLimit = 12,
                        ConsistencyThreshold = 0.30,
                        RequiredTradingDays = 5,
                        SessionResetTime = TimeSpan.Zero,
                        CountsCommissionsInDrawdown = true,
                        Description = "Hybrid percentage model: daily loss is 5% of account value and trailing drawdown is 10%, reset at midnight."
                    }
                },
                {
                    "Custom",
                    new PropFirmProfile
                    {
                        FirmName = "Custom",
                        PresetName = "Custom",
                        ProgramType = "Custom",
                        AccountSize = 0.0,
                        RuleType = "Custom",
                        TrailingDrawdownAmount = null,
                        DailyLossLimit = null,
                        StaticMaxLoss = null,
                        ProfitTarget = null,
                        MaxPositionSize = null,
                        DailyTradeLimit = 10,
                        ConsistencyThreshold = 0.30,
                        RequiredTradingDays = 1,
                        SessionResetTime = TimeSpan.Zero,
                        CountsCommissionsInDrawdown = false,
                        Description = "Manual configuration profile."
                    }
                }
            };
        }

        private static PropFirmProfile Futures(string firm, string preset, string programType, double accountSize, string ruleType, double? trailing, double? dailyLoss, double? staticLoss, double? target, int? maxContracts, int dailyTradeLimit, double consistencyThreshold, int requiredDays, TimeSpan reset, bool commissions)
        {
            return new PropFirmProfile
            {
                FirmName = firm,
                PresetName = preset,
                ProgramType = programType,
                AccountSize = accountSize,
                RuleType = ruleType,
                TrailingDrawdownAmount = trailing,
                DailyLossLimit = dailyLoss,
                StaticMaxLoss = staticLoss,
                ProfitTarget = target,
                MaxPositionSize = maxContracts,
                DailyTradeLimit = dailyTradeLimit,
                ConsistencyThreshold = consistencyThreshold,
                RequiredTradingDays = requiredDays,
                SessionResetTime = reset,
                CountsCommissionsInDrawdown = commissions,
                Description = preset + " futures funding rule template. Verify against the firm dashboard before live enforcement."
            };
        }

        public static PropFirmProfile GetPreset(string firmName)
        {
            if (string.IsNullOrWhiteSpace(firmName))
                firmName = "Custom";

            PropFirmProfile profile;
            if (Profiles.TryGetValue(firmName, out profile))
                return profile;

            return Profiles["Custom"];
        }

        public static string[] GetAllPresetNames()
        {
            return Profiles.Keys.OrderBy(name => name).ToArray();
        }
    }
}
