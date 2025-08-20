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
                AnalysisMode.FullAnalysis => 50.0,      // 🔧 RIDOTTO da 60 a 50
                AnalysisMode.PreMarketWatch => 60.0,    // 🔧 RIDOTTO da 80 a 60
                AnalysisMode.OffHoursMonitor => 70.0,   // 🔧 ABILITATO: era double.MaxValue (mai)
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

            // 🚫 OFF-HOURS: Mercato chiuso = NO SEGNALI
            if (mode == AnalysisMode.OffHoursMonitor)
            {
                _logger.LogInformation($"🟠 OFF-HOURS: {signal.Symbol} - Market closed, skipping signal (professional strategy)");
                return false; // 🎯 SEMPRE false per off-hours
            }

            // 🚫 SKIP: Troppo lontano dall'apertura
            _logger.LogInformation($"❌ SKIP MODE: {signal.Symbol} - Too far from market open");
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