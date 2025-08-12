using PortfolioSignalWorker.Models;

namespace PortfolioSignalWorker.Services
{
    public class MarketHoursService
    {

        public MarketHoursService()
        {
        }

        public bool IsMarketOpen(string symbol)
        {
            var market = GetMarketFromSymbol(symbol);
            var now = DateTime.UtcNow;

            return market switch
            {
                MarketRegion.US => IsUSMarketOpen(now),
                MarketRegion.Europe => IsEuropeanMarketOpen(now, symbol),
                _ => true // Default to always open for unknown markets
            };
        }

        public TimeSpan GetTimeUntilMarketOpen(string symbol)
        {
            var market = GetMarketFromSymbol(symbol);
            var now = DateTime.UtcNow;

            return market switch
            {
                MarketRegion.US => GetTimeUntilUSMarketOpen(now),
                MarketRegion.Europe => GetTimeUntilEuropeanMarketOpen(now, symbol),
                _ => TimeSpan.Zero
            };
        }

        private MarketRegion GetMarketFromSymbol(string symbol)
        {
            if (symbol.Contains(".MI") || symbol.Contains(".AS") || symbol.Contains(".DE") ||
                symbol.Contains(".PA") || symbol.Contains(".SW") || symbol.Contains(".L") ||
                symbol.Contains(".MC"))
                return MarketRegion.Europe;

            return MarketRegion.US;
        }

        private bool IsUSMarketOpen(DateTime utcNow)
        {
            // Convert to EST/EDT (New York time)
            var nyTime = TimeZoneInfo.ConvertTimeFromUtc(utcNow,
                TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"));

            // Check if it's a weekday
            if (nyTime.DayOfWeek == DayOfWeek.Saturday || nyTime.DayOfWeek == DayOfWeek.Sunday)
                return false;

            // Market hours: 9:30 AM - 4:00 PM EST/EDT
            var marketOpen = new TimeSpan(9, 30, 0);
            var marketClose = new TimeSpan(16, 0, 0);

            return nyTime.TimeOfDay >= marketOpen && nyTime.TimeOfDay <= marketClose;
        }

        private bool IsEuropeanMarketOpen(DateTime utcNow, string symbol)
        {
            var timeZoneId = GetEuropeanTimeZone(symbol);
            var localTime = TimeZoneInfo.ConvertTimeFromUtc(utcNow,
                TimeZoneInfo.FindSystemTimeZoneById(timeZoneId));

            // Check if it's a weekday
            if (localTime.DayOfWeek == DayOfWeek.Saturday || localTime.DayOfWeek == DayOfWeek.Sunday)
                return false;

            // European market hours: 9:00 AM - 5:30 PM local time (varies by exchange)
            var (marketOpen, marketClose) = GetEuropeanMarketHours(symbol);

            return localTime.TimeOfDay >= marketOpen && localTime.TimeOfDay <= marketClose;
        }

        private string GetEuropeanTimeZone(string symbol)
        {
            return symbol switch
            {
                var s when s.Contains(".MI") => "Central European Standard Time", // Milan
                var s when s.Contains(".AS") => "Central European Standard Time", // Amsterdam
                var s when s.Contains(".DE") => "Central European Standard Time", // Frankfurt
                var s when s.Contains(".PA") => "Central European Standard Time", // Paris
                var s when s.Contains(".SW") => "Central European Standard Time", // Swiss
                var s when s.Contains(".L") => "GMT Standard Time",               // London
                var s when s.Contains(".MC") => "Central European Standard Time", // Madrid
                _ => "Central European Standard Time"
            };
        }

        private (TimeSpan open, TimeSpan close) GetEuropeanMarketHours(string symbol)
        {
            return symbol switch
            {
                var s when s.Contains(".MI") => (new TimeSpan(9, 0, 0), new TimeSpan(17, 30, 0)),   // Milan
                var s when s.Contains(".AS") => (new TimeSpan(9, 0, 0), new TimeSpan(17, 30, 0)),   // Amsterdam
                var s when s.Contains(".DE") => (new TimeSpan(9, 0, 0), new TimeSpan(17, 30, 0)),   // Frankfurt
                var s when s.Contains(".PA") => (new TimeSpan(9, 0, 0), new TimeSpan(17, 30, 0)),   // Paris
                var s when s.Contains(".SW") => (new TimeSpan(9, 0, 0), new TimeSpan(17, 30, 0)),   // Swiss
                var s when s.Contains(".L") => (new TimeSpan(8, 0, 0), new TimeSpan(16, 30, 0)),    // London
                var s when s.Contains(".MC") => (new TimeSpan(9, 0, 0), new TimeSpan(17, 30, 0)),   // Madrid
                _ => (new TimeSpan(9, 0, 0), new TimeSpan(17, 30, 0))
            };
        }

        private TimeSpan GetTimeUntilUSMarketOpen(DateTime utcNow)
        {
            var nyTime = TimeZoneInfo.ConvertTimeFromUtc(utcNow,
                TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"));

            var marketOpen = new TimeSpan(9, 30, 0);

            // If market is currently open, return 0
            if (IsUSMarketOpen(utcNow))
                return TimeSpan.Zero;

            // Calculate next market open
            var nextOpen = nyTime.Date.Add(marketOpen);

            // If today's market already closed, next open is tomorrow
            if (nyTime.TimeOfDay > new TimeSpan(16, 0, 0))
                nextOpen = nextOpen.AddDays(1);

            // Skip weekends
            while (nextOpen.DayOfWeek == DayOfWeek.Saturday || nextOpen.DayOfWeek == DayOfWeek.Sunday)
                nextOpen = nextOpen.AddDays(1);

            return nextOpen - nyTime;
        }

        private TimeSpan GetTimeUntilEuropeanMarketOpen(DateTime utcNow, string symbol)
        {
            var timeZoneId = GetEuropeanTimeZone(symbol);
            var localTime = TimeZoneInfo.ConvertTimeFromUtc(utcNow,
                TimeZoneInfo.FindSystemTimeZoneById(timeZoneId));

            var (marketOpen, _) = GetEuropeanMarketHours(symbol);

            // If market is currently open, return 0
            if (IsEuropeanMarketOpen(utcNow, symbol))
                return TimeSpan.Zero;

            // Calculate next market open
            var nextOpen = localTime.Date.Add(marketOpen);

            // If today's market already closed, next open is tomorrow
            if (localTime.TimeOfDay > GetEuropeanMarketHours(symbol).close)
                nextOpen = nextOpen.AddDays(1);

            // Skip weekends
            while (nextOpen.DayOfWeek == DayOfWeek.Saturday || nextOpen.DayOfWeek == DayOfWeek.Sunday)
                nextOpen = nextOpen.AddDays(1);

            return nextOpen - localTime;
        }

        public string GetMarketStatus(string symbol)
        {
            var isOpen = IsMarketOpen(symbol);
            var market = GetMarketFromSymbol(symbol);

            if (isOpen)
                return $"{market} Market OPEN";

            var timeUntilOpen = GetTimeUntilMarketOpen(symbol);
            if (timeUntilOpen.TotalHours < 24)
                return $"{market} Market CLOSED - Opens in {timeUntilOpen.Hours}h {timeUntilOpen.Minutes}m";

            return $"{market} Market CLOSED - Opens {timeUntilOpen.Days}d {timeUntilOpen.Hours}h";
        }
    }

    public enum MarketRegion
    {
        US,
        Europe,
        Asia // For future expansion
    }
}