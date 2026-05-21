using System;

namespace PropFirmGuardian.Utils
{
    public static class TimeZoneHelper
    {
        public static readonly TimeZoneInfo EasternZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

        public static DateTime ToUtc(DateTime easternTime)
        {
            DateTime unspecifiedEastern = DateTime.SpecifyKind(easternTime, DateTimeKind.Unspecified);
            return TimeZoneInfo.ConvertTimeToUtc(unspecifiedEastern, EasternZone);
        }

        public static DateTime FromUtc(DateTime utcTime)
        {
            DateTime normalizedUtc = utcTime.Kind == DateTimeKind.Utc
                ? utcTime
                : DateTime.SpecifyKind(utcTime, DateTimeKind.Utc);

            return TimeZoneInfo.ConvertTimeFromUtc(normalizedUtc, EasternZone);
        }

        public static DateTime GetNextMidnightEt()
        {
            DateTime easternNow = FromUtc(DateTime.UtcNow);
            DateTime nextMidnightEastern = easternNow.Date.AddDays(1);
            return ToUtc(nextMidnightEastern);
        }

        public static bool IsDstActive()
        {
            return EasternZone.IsDaylightSavingTime(FromUtc(DateTime.UtcNow));
        }
    }
}
