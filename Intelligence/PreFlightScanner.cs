using System;
using NinjaTrader.Cbi;
using PropFirmGuardian.Models;

namespace PropFirmGuardian.Intelligence
{
    public sealed class PreFlightScanner
    {
        public PreFlightReport Scan(Account account, AccountConfig config)
        {
            PreFlightReport report = new PreFlightReport
            {
                AccountName = account != null ? account.Name : string.Empty
            };

            if (account == null)
            {
                report.Warnings.Add(new PreFlightWarning { Code = "NO_ACCOUNT", Message = "No NinjaTrader account is available.", SuggestedAction = "Connect the account before trading." });
                return report;
            }

            if (config == null)
            {
                report.Warnings.Add(new PreFlightWarning { Code = "NO_CONFIG", Message = "Account rules are not configured.", SuggestedAction = "Select a prop firm preset." });
                return report;
            }

            if (config.DailyLossLimit <= 0.0)
                report.Warnings.Add(new PreFlightWarning { Code = "DAILY_LIMIT", Message = "Daily loss limit is not configured.", SuggestedAction = "Set the prop firm daily loss limit." });

            if (config.ProfitTarget <= 0.0)
                report.Warnings.Add(new PreFlightWarning { Code = "PROFIT_TARGET", Message = "Profit target is not configured.", SuggestedAction = "Set the eval profit target." });

            return report;
        }
    }
}
