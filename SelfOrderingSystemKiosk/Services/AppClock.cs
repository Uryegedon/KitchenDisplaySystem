namespace SelfOrderingSystemKiosk.Services
{
    public static class AppClock
    {
        private static readonly TimeZoneInfo BusinessTimeZone = ResolveBusinessTimeZone();

        public static DateTime UtcNow => DateTime.UtcNow;

        public static DateTime LocalNow => TimeZoneInfo.ConvertTimeFromUtc(UtcNow, BusinessTimeZone);

        public static DateTime ToLocal(DateTime value)
        {
            var utc = value.Kind switch
            {
                DateTimeKind.Utc => value,
                DateTimeKind.Local => value.ToUniversalTime(),
                _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
            };

            return TimeZoneInfo.ConvertTimeFromUtc(utc, BusinessTimeZone);
        }

        public static DateTime ToUtc(DateTime value)
        {
            return value.Kind switch
            {
                DateTimeKind.Utc => value,
                DateTimeKind.Local => value.ToUniversalTime(),
                _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
            };
        }

        public static (DateTime StartUtc, DateTime EndUtc) LocalDateRange(DateTime localDate)
        {
            var startLocal = DateTime.SpecifyKind(localDate.Date, DateTimeKind.Unspecified);
            var endLocal = startLocal.AddDays(1);
            return (TimeZoneInfo.ConvertTimeToUtc(startLocal, BusinessTimeZone),
                TimeZoneInfo.ConvertTimeToUtc(endLocal, BusinessTimeZone));
        }

        public static (DateTime StartUtc, DateTime EndUtc) CurrentLocalWeekRange()
        {
            var today = LocalNow.Date;
            var dayOfWeek = today.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)today.DayOfWeek;
            return LocalDateRange(today.AddDays(-(dayOfWeek - 1)), 7);
        }

        public static (DateTime StartUtc, DateTime EndUtc) CurrentLocalMonthRange()
        {
            var today = LocalNow.Date;
            var start = new DateTime(today.Year, today.Month, 1);
            var end = start.AddMonths(1);
            return LocalDateRange(start, (end - start).Days);
        }

        public static (DateTime StartUtc, DateTime EndUtc) CurrentLocalYearRange()
        {
            var today = LocalNow.Date;
            var start = new DateTime(today.Year, 1, 1);
            var end = start.AddYears(1);
            return LocalDateRange(start, (end - start).Days);
        }

        public static (DateTime StartUtc, DateTime EndUtc) LocalDateRange(DateTime startLocalDate, int days)
        {
            var startLocal = DateTime.SpecifyKind(startLocalDate.Date, DateTimeKind.Unspecified);
            var endLocal = startLocal.AddDays(Math.Max(1, days));
            return (TimeZoneInfo.ConvertTimeToUtc(startLocal, BusinessTimeZone),
                TimeZoneInfo.ConvertTimeToUtc(endLocal, BusinessTimeZone));
        }

        public static (DateTime StartUtc, DateTime EndUtc) LocalDateRange(DateTime startLocalDate, DateTime endLocalDateInclusive)
        {
            var startLocal = DateTime.SpecifyKind(startLocalDate.Date, DateTimeKind.Unspecified);
            var endLocal = DateTime.SpecifyKind(endLocalDateInclusive.Date.AddDays(1), DateTimeKind.Unspecified);
            if (endLocal <= startLocal)
                endLocal = startLocal.AddDays(1);

            return (TimeZoneInfo.ConvertTimeToUtc(startLocal, BusinessTimeZone),
                TimeZoneInfo.ConvertTimeToUtc(endLocal, BusinessTimeZone));
        }

        private static TimeZoneInfo ResolveBusinessTimeZone()
        {
            foreach (var id in new[] { "Asia/Manila", "Singapore Standard Time" })
            {
                try
                {
                    return TimeZoneInfo.FindSystemTimeZoneById(id);
                }
                catch (TimeZoneNotFoundException)
                {
                }
                catch (InvalidTimeZoneException)
                {
                }
            }

            return TimeZoneInfo.Utc;
        }
    }
}
