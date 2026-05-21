using System.Collections.Generic;

namespace PropFirmGuardian.Models
{
    public sealed class PreFlightReport
    {
        public PreFlightReport()
        {
            Warnings = new List<PreFlightWarning>();
        }

        public string AccountName { get; set; }
        public List<PreFlightWarning> Warnings { get; private set; }

        public bool HasWarnings
        {
            get { return Warnings.Count > 0; }
        }
    }

    public sealed class PreFlightWarning
    {
        public string Code { get; set; }
        public string Message { get; set; }
        public string SuggestedAction { get; set; }
    }
}
