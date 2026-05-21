using System;
using System.Collections.Generic;
using System.Linq;

namespace PropFirmGuardian.Intelligence
{
    public sealed class PropFirmProfile
    {
        public string FirmName { get; set; }
        public string RuleType { get; set; }
        public double? TrailingDrawdownAmount { get; set; }
        public double? DailyLossLimit { get; set; }
        public double? StaticMaxLoss { get; set; }
        public double? ProfitTarget { get; set; }
        public int? MaxPositionSize { get; set; }
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
                {
                    "Apex",
                    new PropFirmProfile
                    {
                        FirmName = "Apex",
                        RuleType = "Trailing",
                        TrailingDrawdownAmount = 2500.0,
                        DailyLossLimit = null,
                        StaticMaxLoss = null,
                        ProfitTarget = null,
                        MaxPositionSize = null,
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
                        RuleType = "DailyLoss",
                        TrailingDrawdownAmount = null,
                        DailyLossLimit = 1000.0,
                        StaticMaxLoss = null,
                        ProfitTarget = null,
                        MaxPositionSize = null,
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
                        RuleType = "Hybrid",
                        TrailingDrawdownAmount = 2500.0,
                        DailyLossLimit = 1000.0,
                        StaticMaxLoss = null,
                        ProfitTarget = null,
                        MaxPositionSize = null,
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
                        RuleType = "Static",
                        TrailingDrawdownAmount = null,
                        DailyLossLimit = null,
                        StaticMaxLoss = 5000.0,
                        ProfitTarget = 5000.0,
                        MaxPositionSize = null,
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
                        RuleType = "Hybrid",
                        TrailingDrawdownAmount = 0.10,
                        DailyLossLimit = 0.05,
                        StaticMaxLoss = null,
                        ProfitTarget = null,
                        MaxPositionSize = null,
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
                        RuleType = "Custom",
                        TrailingDrawdownAmount = null,
                        DailyLossLimit = null,
                        StaticMaxLoss = null,
                        ProfitTarget = null,
                        MaxPositionSize = null,
                        SessionResetTime = TimeSpan.Zero,
                        CountsCommissionsInDrawdown = false,
                        Description = "Manual configuration profile."
                    }
                }
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
