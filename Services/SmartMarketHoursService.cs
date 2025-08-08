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

        public TimeSpan GetDynamicMonitoringFrequency(WatchlistSymbol symbol, AnalysisMode mode)
        {
            var baseFrequency = mode switch
            {
                AnalysisMode.FullAnalysis => symbol.VolatilityLevel switch
                {
                    VolatilityLevel.Explosive => TimeSpan.FromMinutes(5),   // 🚀 Esplosivi ogni 5 min!
                    VolatilityLevel.High => TimeSpan.FromMinutes(15),       // 📈 Volatili ogni 15 min
                    VolatilityLevel.Standard => TimeSpan.FromMinutes(30),   // 📊 Standard ogni 30 min
                    VolatilityLevel.Low => TimeSpan.FromHours(2),           // 😴 Lenti ogni 2 ore
                    _ => TimeSpan.FromMinutes(30)
                },

                AnalysisMode.PreMarketWatch => symbol.VolatilityLevel switch
                {
                    VolatilityLevel.Explosive => TimeSpan.FromMinutes(10),  // Pre-market esplosivi
                    VolatilityLevel.High => TimeSpan.FromMinutes(30),
                    _ => TimeSpan.FromHours(1)
                },

                AnalysisMode.OffHoursMonitor => symbol.VolatilityLevel switch
                {
                    VolatilityLevel.Explosive => TimeSpan.FromHours(2),     // Anche off-hours per esplosivi
                    VolatilityLevel.High => TimeSpan.FromHours(4),
                    _ => TimeSpan.FromHours(8)
                },

                _ => TimeSpan.FromHours(4)
            };

            // 🔥 BOOST per breakout candidates
            if (symbol.IsBreakoutCandidate)
            {
                baseFrequency = TimeSpan.FromTicks(baseFrequency.Ticks / 2); // 2x più frequente
                _logger.LogDebug($"🚀 BREAKOUT CANDIDATE boost: {symbol.Symbol} frequency = {baseFrequency}");
            }

            return baseFrequency;
        }

        // Metodo helper per ottenere il delay dinamico (dal Worker)
        public int GetDynamicProcessingDelay(WatchlistSymbol symbol, AnalysisMode mode)
        {
            return (symbol.VolatilityLevel, mode) switch
            {
                (VolatilityLevel.Explosive, AnalysisMode.FullAnalysis) => 200,     // Esplosivi super rapidi
                (VolatilityLevel.High, AnalysisMode.FullAnalysis) => 400,         // Volatili rapidi  
                (VolatilityLevel.Standard, AnalysisMode.FullAnalysis) => 600,     // Standard normale
                (VolatilityLevel.Low, _) => 1000,                                 // Lenti più lenti

                (VolatilityLevel.Explosive, AnalysisMode.PreMarketWatch) => 300,  // Pre-market esplosivi
                (_, AnalysisMode.PreMarketWatch) => 800,

                (_, AnalysisMode.OffHoursMonitor) => 1200,                        // Off-hours più lenti

                _ => 600 // Default
            };
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
            return mode switch
            {
                AnalysisMode.FullAnalysis => 60.0,     // Soglia normale durante mercato
                AnalysisMode.PreMarketWatch => 75.0,   // Soglia più alta per pre-market
                AnalysisMode.OffHoursMonitor => 85.0,  // Solo segnali molto forti off-hours
                AnalysisMode.Skip => double.MaxValue,  // Non inviare mai
                _ => 60.0
            };
        }

        public TimeSpan GetAnalysisFrequency(AnalysisMode mode, SymbolTier tier)
        {
            return mode switch
            {
                AnalysisMode.FullAnalysis => tier switch
                {
                    SymbolTier.Tier1_Priority => TimeSpan.FromMinutes(30),
                    SymbolTier.Tier2_Standard => TimeSpan.FromHours(2),
                    SymbolTier.Tier3_Monitor => TimeSpan.FromHours(4),
                    _ => TimeSpan.FromHours(4)
                },
                AnalysisMode.PreMarketWatch => TimeSpan.FromMinutes(45),  // Più frequente in pre-market
                AnalysisMode.OffHoursMonitor => TimeSpan.FromHours(6),    // Meno frequente off-hours
                AnalysisMode.Skip => TimeSpan.FromDays(1),                // Skip fino al giorno dopo
                _ => TimeSpan.FromHours(4)
            };
        }

        public string GetModeDescription(AnalysisMode mode, string symbol)
        {
            var marketInfo = GetMarketStatus(symbol);

            return mode switch
            {
                AnalysisMode.FullAnalysis => $"🟢 LIVE - {marketInfo}",
                AnalysisMode.PreMarketWatch => $"🟡 PRE-MARKET - {marketInfo}",
                AnalysisMode.OffHoursMonitor => $"🟠 OFF-HOURS - {marketInfo}",
                AnalysisMode.Skip => $"❌ SKIP - {marketInfo}",
                _ => marketInfo
            };
        }

        public bool ShouldSendSignal(TradingSignal signal, AnalysisMode mode)
        {
            var threshold = GetConfidenceThreshold(mode);

            // Durante mercato aperto, invia tutti i segnali sopra soglia
            if (mode == AnalysisMode.FullAnalysis)
                return signal.Confidence >= threshold;

            // Pre-market: solo Buy e Warning forti
            if (mode == AnalysisMode.PreMarketWatch)
                return signal.Confidence >= threshold &&
                       (signal.Type == SignalType.Buy || signal.Type == SignalType.Warning);

            // Off-hours: solo segnali molto forti e specifici patterns
            if (mode == AnalysisMode.OffHoursMonitor)
                return signal.Confidence >= threshold &&
                       (signal.Type == SignalType.Buy || signal.Type == SignalType.Warning) &&
                       (signal.RSI < 25 || signal.RSI > 75); // Solo condizioni estreme

            return false;
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