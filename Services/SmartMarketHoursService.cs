using PortfolioSignalWorker.Models;

namespace PortfolioSignalWorker.Services
{
    public class SmartMarketHoursService : MarketHoursService
    {
        private readonly ILogger<SmartMarketHoursService> _logger;
        public SmartMarketHoursService(ILogger<SmartMarketHoursService> logger)
        {
            _logger = logger;
        }

        public enum AnalysisMode
        {
            FullAnalysis,      // Durante orari di mercato - analisi completa
            PreMarketWatch,    // 2 ore prima apertura - setup detection
            OffHoursMonitor,   // Mercato chiuso - solo segnali forti
            Skip               // Non analizzare (troppo lontano dall'apertura)
        }

        public AnalysisMode GetAnalysisMode(string symbol)
        {
            var utcNow = DateTime.UtcNow;
            var isOpen = IsMarketOpen(symbol);
            var timeUntilOpen = GetTimeUntilMarketOpen(symbol);

            return (isOpen, timeUntilOpen.TotalHours) switch
            {
                // ✅ Mercato aperto → Analisi completa con tutti i segnali
                (true, _) => AnalysisMode.FullAnalysis,

                // 🟡 Pre-market window (2 ore prima apertura) → Setup detection
                (false, <= 2 and > 0) => AnalysisMode.PreMarketWatch,

                // 🟠 Post-market o weekend (fino a 20 ore) → Solo segnali forti
                (false, <= 20 and > 2) => AnalysisMode.OffHoursMonitor,

                // ❌ Troppo lontano dall'apertura → Skip
                (false, > 20) => AnalysisMode.Skip
            };
        }

        public double GetConfidenceThreshold(AnalysisMode mode)
        {
            // 🔧 CONFIDENCE THRESHOLDS RILASSATI
            return mode switch
            {
                // VECCHIE SOGLIE (TROPPO SEVERE):
                // FullAnalysis => 60.0,      
                // PreMarketWatch => 80.0,    
                // OffHoursMonitor => double.MaxValue (mai)

                // 🔧 NUOVE SOGLIE RILASSATE:
                AnalysisMode.FullAnalysis => 63.0,
                AnalysisMode.PreMarketWatch => 63.0,
                AnalysisMode.OffHoursMonitor => 63.0,   // post-close Window 0: candela completa, segnali affidabili
                AnalysisMode.Skip => double.MaxValue,   // Skip rimane disabilitato
                _ => 50.0
            };
        }

        public TimeSpan GetAnalysisFrequency(AnalysisMode mode, SymbolTier tier)
        {
            return mode switch
            {
                AnalysisMode.FullAnalysis => tier switch
                {
                    SymbolTier.Tier1_Priority => TimeSpan.FromMinutes(30),      // Unchanged
                    SymbolTier.Tier2_Standard => TimeSpan.FromHours(1),         // 🔧 RIDOTTO da 2 a 1 ora
                    SymbolTier.Tier3_Monitor => TimeSpan.FromHours(2),          // 🔧 RIDOTTO da 4 a 2 ore
                    _ => TimeSpan.FromHours(2)
                },
                AnalysisMode.PreMarketWatch => TimeSpan.FromMinutes(30),        // 🔧 RIDOTTO da 45 a 30 minuti
                AnalysisMode.OffHoursMonitor => TimeSpan.Zero,                  // 🔧 IGNORATO - usa CalculateNextMarketOpenTime
                AnalysisMode.Skip => TimeSpan.Zero,                             // 🔧 IGNORATO - usa CalculateNextMarketOpenTime
                _ => TimeSpan.FromHours(2)
            };
        }

        public string GetModeDescription(AnalysisMode mode, string symbol)
        {
            var marketInfo = GetMarketStatus(symbol);

            return mode switch
            {
                AnalysisMode.FullAnalysis => $"🟢 TRADING LIVE - {marketInfo}",
                AnalysisMode.PreMarketWatch => $"🟡 SETUP MODE - {marketInfo}",
                AnalysisMode.OffHoursMonitor => $"🚫 MARKET CLOSED - No signals sent",
                AnalysisMode.Skip => $"❌ SKIP - {marketInfo}",
                _ => marketInfo
            };
        }

        public bool ShouldSendSignal(TradingSignal signal, AnalysisMode mode)
        {
            var threshold = GetConfidenceThreshold(mode);

            // 🎯 STRATEGIA PROFESSIONALE: Solo durante mercati aperti
            if (mode == AnalysisMode.FullAnalysis)
            {
                // ✅ Mercato APERTO = Invio tutti i segnali di qualità
                var result = signal.Confidence >= threshold;
                _logger.LogInformation($"🟢 LIVE MARKET: {signal.Symbol} {signal.Confidence}% >= {threshold}% = {(result ? "SEND" : "SKIP")}");
                return result;
            }

            // 🟡 PRE-MARKET: Solo per setup di preparazione (opzionale)
            if (mode == AnalysisMode.PreMarketWatch)
            {
                // Puoi abilitare/disabilitare i pre-market alerts
                var enablePreMarket = true; // 🔧 Set false per disabilitare completamente

                if (!enablePreMarket)
                {
                    _logger.LogInformation($"🟡 PRE-MARKET DISABLED: Skipping {signal.Symbol}");
                    return false;
                }

                var confidenceCheck = signal.Confidence >= threshold;
                var typeCheck = (signal.Type == SignalType.Buy || signal.Type == SignalType.Warning);
                var result = confidenceCheck && typeCheck;

                _logger.LogInformation($"🟡 PRE-MARKET SETUP: {signal.Symbol} = {(result ? "SEND" : "SKIP")} (opens soon)");
                return result;
            }

            // 🟠 POST-CLOSE (Window 0): candela completa — segnali più affidabili del giorno
            if (mode == AnalysisMode.OffHoursMonitor)
            {
                var result = signal.Confidence >= threshold;
                _logger.LogInformation($"🟠 POST-CLOSE: {signal.Symbol} {signal.Confidence}% >= {threshold}% = {(result ? "SEND ✅" : "SKIP")} (candela completa)");
                return result;
            }

            // 🚫 SKIP: Troppo lontano dall'apertura
            _logger.LogInformation($"❌ SKIP MODE: {signal.Symbol} - Too far from market open");
            return false;
        }

        /// <summary>
        /// Returns the UTC DateTime of the next of the 3 daily analysis windows for this symbol.
        /// Windows are designed for daily-candle swing trading:
        ///   Window 1 — open +120 min   (morning: momentum confirmation, ~20-30% volume)
        ///   Window 2 — session midpoint (afternoon: trend visible, candle well-formed)
        ///   Window 0 — close +30 min   (post-close: candle COMPLETE and definitive — most reliable)
        /// Window 0 fires after close so the candle data is final. Signals sent here are acted
        /// on the following morning at open — the classic swing trading workflow.
        /// If all windows for today have passed, returns Window 1 of the next trading session.
        /// </summary>
        public DateTime GetNextAnalysisWindow(string symbol)
        {
            var utcNow = DateTime.UtcNow;
            var (openUtc, closeUtc) = GetMarketSessionUtc(symbol, utcNow);

            var window1 = openUtc.AddMinutes(120);   // +2h: morning momentum
            var window2 = openUtc + TimeSpan.FromTicks((closeUtc - openUtc).Ticks / 2); // midpoint
            var window0 = closeUtc.AddMinutes(30);   // post-close: candle complete and definitive

            if (utcNow < window1) return window1;
            if (utcNow < window2) return window2;
            if (utcNow < window0) return window0;

            // All windows passed today — find next trading session's Window 1
            var nextRef = utcNow.AddDays(1);
            for (int i = 0; i < 5; i++)
            {
                var (nextOpen, _) = GetMarketSessionUtc(symbol, nextRef);
                if (nextOpen.AddMinutes(120) > utcNow)
                    return nextOpen.AddMinutes(120);
                nextRef = nextRef.AddDays(1);
            }

            return utcNow.AddHours(24); // fallback
        }

        /// <summary>
        /// Returns the UTC open and close of the trading session for the given symbol
        /// on the trading day that contains (or follows) referenceUtc.
        /// </summary>
        private (DateTime openUtc, DateTime closeUtc) GetMarketSessionUtc(string symbol, DateTime referenceUtc)
        {
            string tzId;
            TimeSpan openLocal, closeLocal;

            if (GetMarketFromSymbol(symbol) == MarketRegion.US)
            {
                tzId = "Eastern Standard Time";
                openLocal = new TimeSpan(9, 30, 0);
                closeLocal = new TimeSpan(16, 0, 0);
            }
            else
            {
                tzId = GetEuropeanTimeZone(symbol);
                (openLocal, closeLocal) = GetEuropeanMarketHours(symbol);
            }

            var tz = TimeZoneInfo.FindSystemTimeZoneById(tzId);
            var localRef = TimeZoneInfo.ConvertTimeFromUtc(referenceUtc, tz);

            // Advance to the nearest weekday (skips Sat/Sun)
            var tradingDate = localRef.Date;
            while (tradingDate.DayOfWeek == DayOfWeek.Saturday || tradingDate.DayOfWeek == DayOfWeek.Sunday)
                tradingDate = tradingDate.AddDays(1);

            var openLocalDt  = DateTime.SpecifyKind(tradingDate.Add(openLocal),  DateTimeKind.Unspecified);
            var closeLocalDt = DateTime.SpecifyKind(tradingDate.Add(closeLocal), DateTimeKind.Unspecified);

            return (
                TimeZoneInfo.ConvertTimeToUtc(openLocalDt,  tz),
                TimeZoneInfo.ConvertTimeToUtc(closeLocalDt, tz)
            );
        }

        public List<string> GetCurrentAnalysisModes()
        {
            var modes = new List<string>();
            var now = DateTime.UtcNow;

            // Esempi di simboli per ogni mercato
            var testSymbols = new Dictionary<string, string>
            {
                ["US"] = "AAPL",
                ["EU-Milan"] = "ENI.MI",
                ["EU-Frankfurt"] = "SAP.DE",
                ["EU-Amsterdam"] = "ASML.AS",
                ["EU-Paris"] = "LVMH.PA",
                ["EU-Swiss"] = "NESN.SW",
                ["EU-London"] = "SHEL.L"
            };

            foreach (var market in testSymbols)
            {
                var mode = GetAnalysisMode(market.Value);
                var description = GetModeDescription(mode, market.Value);
                modes.Add($"{market.Key}: {description}");
            }

            return modes;
        }

        // Metodo per debug/logging dell'analisi di mercato
        public void LogCurrentMarketStatus()
        {
            var modes = GetCurrentAnalysisModes();

            _logger.LogInformation("=== SMART MARKET ANALYSIS STATUS ===");
            foreach (var mode in modes)
            {
                _logger.LogInformation($"  {mode}");
            }
            _logger.LogInformation("=====================================");
        }
    }
}