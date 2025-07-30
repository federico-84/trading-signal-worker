using MongoDB.Bson;
using MongoDB.Driver;
using PortfolioSignalWorker.Models;

namespace PortfolioSignalWorker.Services
{
    public class SignalFilterService
    {
        private readonly IMongoCollection<StockIndicator> _indicatorCollection;
        private readonly IMongoCollection<TradingSignal> _signalCollection;
        private readonly ILogger<SignalFilterService> _logger;

        public SignalFilterService(IMongoDatabase database, ILogger<SignalFilterService> logger)
        {
            _indicatorCollection = database.GetCollection<StockIndicator>("Indicators");
            _signalCollection = database.GetCollection<TradingSignal>("TradingSignals");
            _logger = logger;
        }

        public async Task<TradingSignal?> AnalyzeSignalAsync(string symbol, StockIndicator currentIndicator)
        {
            // 1. Ottieni storico ultimi 20 periodi
            var historicalData = await GetHistoricalDataAsync(symbol, 20);

            // MODIFICA: Ridotto requisito minimo da 5 a 1 per permettere l'avvio
            if (historicalData.Count < 1)
            {
                _logger.LogWarning($"Nessun dato storico per {symbol}. Primo avvio - genero comunque segnale base");

                // Per il primo avvio, genera segnali basici solo su indicatori correnti
                return await GenerateBasicSignalAsync(symbol, currentIndicator);
            }

            _logger.LogInformation($"Analizzando {symbol} con {historicalData.Count} record storici");

            // 2. Calcola medie mobili e conferme
            var enhancedIndicator = await EnhanceIndicatorAsync(currentIndicator, historicalData);

            // 3. Applica filtri anti-rumore
            var signal = await GenerateFilteredSignalAsync(symbol, enhancedIndicator, historicalData);

            return signal;
        }

        // NUOVO: Segnali basici per quando non abbiamo storico
        private async Task<TradingSignal?> GenerateBasicSignalAsync(string symbol, StockIndicator current)
        {
            // Controlla segnali duplicati recenti
            if (await HasRecentSignalAsync(symbol, TimeSpan.FromHours(2)))
            {
                _logger.LogDebug($"Segnale recente già inviato per {symbol}");
                return null;
            }

            // Segnale BASIC BUY: Solo RSI oversold + MACD cross
            if (current.RSI < 30 && current.MACD_Histogram_CrossUp)
            {
                return new TradingSignal
                {
                    Symbol = symbol,
                    Type = SignalType.Buy,
                    Confidence = 60, // Confidence ridotta senza storico
                    Reason = "BASIC BUY: RSI oversold + MACD cross (no historical data)",
                    RSI = current.RSI,
                    MACD_Histogram = current.MACD_Histogram,
                    Price = current.Price,
                    SignalHash = GenerateSignalHash(symbol, "BASIC_BUY", current.RSI)
                };
            }

            // Segnale WARNING: RSI estremamente oversold
            if (current.RSI < 25)
            {
                return new TradingSignal
                {
                    Symbol = symbol,
                    Type = SignalType.Warning,
                    Confidence = 50, // Confidence ridotta
                    Reason = "WARNING: RSI molto oversold (no historical data)",
                    RSI = current.RSI,
                    MACD_Histogram = current.MACD_Histogram,
                    Price = current.Price,
                    SignalHash = GenerateSignalHash(symbol, "WARNING", current.RSI)
                };
            }

            return null; // Nessun segnale
        }

        private async Task<List<StockIndicator>> GetHistoricalDataAsync(string symbol, int periods)
        {
            var filter = Builders<StockIndicator>.Filter.Eq(x => x.Symbol, symbol);
            var sort = Builders<StockIndicator>.Sort.Descending(x => x.CreatedAt);

            var results = await _indicatorCollection
                .Find(filter)
                .Sort(sort)
                .Limit(periods)
                .ToListAsync();

            _logger.LogDebug($"Retrieved {results.Count} historical records for {symbol}");
            return results;
        }

        private async Task<StockIndicator> EnhanceIndicatorAsync(StockIndicator current, List<StockIndicator> historical)
        {
            // RSI Media Mobile 5 e 14 periodi (solo se abbiamo abbastanza dati)
            if (historical.Count >= 5)
            {
                current.RSI_SMA_5 = historical.Take(5).Average(x => x.RSI);
            }
            else
            {
                current.RSI_SMA_5 = current.RSI; // Fallback al valore corrente
            }

            if (historical.Count >= 14)
            {
                current.RSI_SMA_14 = historical.Take(14).Average(x => x.RSI);
            }
            else if (historical.Count > 0)
            {
                current.RSI_SMA_14 = historical.Average(x => x.RSI); // Media di quello che abbiamo
            }
            else
            {
                current.RSI_SMA_14 = current.RSI; // Fallback
            }

            // RSI Confermato: < 30 per almeno 2 periodi consecutivi (adattivo)
            if (historical.Count >= 2)
            {
                var last2RSI = historical.Take(2).Select(x => x.RSI).ToList();
                current.RSI_Confirmed = current.RSI < 30 && last2RSI.All(rsi => rsi < 30);
            }
            else if (historical.Count >= 1)
            {
                // Con un solo periodo, richiedi RSI ancora più basso
                current.RSI_Confirmed = current.RSI < 25 && historical.First().RSI < 30;
            }
            else
            {
                // Senza storico, richiedi RSI molto basso
                current.RSI_Confirmed = current.RSI < 20;
            }

            // MACD Cross confermato: cross up per almeno 1 periodo (adattivo)
            if (historical.Count >= 1)
            {
                var previousMACDCross = historical.First().MACD_Histogram_CrossUp;
                current.MACD_Confirmed = current.MACD_Histogram_CrossUp || previousMACDCross;
            }
            else
            {
                // Senza storico, usa solo cross corrente
                current.MACD_Confirmed = current.MACD_Histogram_CrossUp;
            }

            // Volume Spike: volume > 150% della media (adattivo)
            if (historical.Count >= 20)
            {
                var avgVolume = historical.Take(20).Average(x => x.Volume);
                current.VolumeSpike = current.Volume > (avgVolume * 1.5);
            }
            else if (historical.Count >= 5)
            {
                var avgVolume = historical.Average(x => x.Volume);
                current.VolumeSpike = current.Volume > (avgVolume * 1.3); // Soglia ridotta
            }
            else
            {
                // Senza storico, considera volume spike se molto alto
                current.VolumeSpike = current.Volume > 10_000_000; // Soglia assoluta
            }

            return current;
        }

        private async Task<TradingSignal?> GenerateFilteredSignalAsync(
            string symbol,
            StockIndicator enhanced,
            List<StockIndicator> historical)
        {
            // FILTRO 1: Evita segnali duplicati recenti (ultimi 2 ore)
            if (await HasRecentSignalAsync(symbol, TimeSpan.FromHours(2)))
            {
                _logger.LogDebug($"Segnale recente già inviato per {symbol}");
                return null;
            }

            // FILTRO 2: STRONG BUY - Tutti gli indicatori allineati (ma adattato per poco storico)
            if (IsStrongBuySignal(enhanced, historical.Count))
            {
                var confidence = CalculateConfidence(historical.Count, 95);
                return new TradingSignal
                {
                    Symbol = symbol,
                    Type = SignalType.Buy,
                    Confidence = confidence,
                    Reason = $"STRONG BUY: RSI oversold confermato + MACD cross + Volume ({historical.Count} periods)",
                    RSI = enhanced.RSI,
                    MACD_Histogram = enhanced.MACD_Histogram,
                    Price = enhanced.Price,
                    SignalHash = GenerateSignalHash(symbol, "STRONG_BUY", enhanced.RSI)
                };
            }

            // FILTRO 3: MEDIUM BUY - Confluence di 2 indicatori
            if (IsMediumBuySignal(enhanced, historical.Count))
            {
                var confidence = CalculateConfidence(historical.Count, 75);
                return new TradingSignal
                {
                    Symbol = symbol,
                    Type = SignalType.Buy,
                    Confidence = confidence,
                    Reason = $"BUY: RSI oversold + MACD cross ({historical.Count} periods)",
                    RSI = enhanced.RSI,
                    MACD_Histogram = enhanced.MACD_Histogram,
                    Price = enhanced.Price,
                    SignalHash = GenerateSignalHash(symbol, "MEDIUM_BUY", enhanced.RSI)
                };
            }

            // FILTRO 4: WARNING - RSI estremo ma altri indicatori deboli
            if (IsWarningSignal(enhanced, historical))
            {
                var confidence = CalculateConfidence(historical.Count, 60);
                return new TradingSignal
                {
                    Symbol = symbol,
                    Type = SignalType.Warning,
                    Confidence = confidence,
                    Reason = $"WARNING: RSI molto oversold ({historical.Count} periods)",
                    RSI = enhanced.RSI,
                    MACD_Histogram = enhanced.MACD_Histogram,
                    Price = enhanced.Price,
                    SignalHash = GenerateSignalHash(symbol, "WARNING", enhanced.RSI)
                };
            }

            return null; // Nessun segnale valido
        }

        // NUOVO: Calcola confidence basata sui dati storici disponibili
        private int CalculateConfidence(int historicalCount, int baseConfidence)
        {
            if (historicalCount >= 20) return baseConfidence;         // Full confidence
            if (historicalCount >= 10) return baseConfidence - 10;   // -10%
            if (historicalCount >= 5) return baseConfidence - 20;    // -20%
            if (historicalCount >= 2) return baseConfidence - 30;    // -30%
            return baseConfidence - 40;                               // -40% per 0-1 record
        }

        private bool IsStrongBuySignal(StockIndicator enhanced, int historicalCount)
        {
            var baseCondition = enhanced.RSI < 30 && enhanced.MACD_Confirmed;

            if (historicalCount >= 10)
            {
                // Con abbastanza storico, richiedi tutto
                return baseCondition && enhanced.RSI_Confirmed && enhanced.VolumeSpike && enhanced.RSI_SMA_5 < 35;
            }
            else
            {
                // Con poco storico, requisiti più semplici
                return baseCondition && enhanced.RSI_Confirmed;
            }
        }

        private bool IsMediumBuySignal(StockIndicator enhanced, int historicalCount)
        {
            return enhanced.RSI < 30 &&
                   enhanced.RSI_Confirmed &&
                   enhanced.MACD_Histogram_CrossUp;
        }

        private bool IsWarningSignal(StockIndicator enhanced, List<StockIndicator> historical)
        {
            var isExtremeOversold = enhanced.RSI < 25;
            var hasMACD = enhanced.MACD_Histogram_CrossUp;

            bool trendingDown = false;
            if (historical.Count >= 3)
            {
                trendingDown = historical.Take(3).All(x => x.RSI < enhanced.RSI + 5);
            }

            return isExtremeOversold && !hasMACD;
        }

        private async Task<bool> HasRecentSignalAsync(string symbol, TimeSpan timeWindow)
        {
            var cutoffTime = DateTime.UtcNow.Subtract(timeWindow);
            var filter = Builders<TradingSignal>.Filter.And(
                Builders<TradingSignal>.Filter.Eq(x => x.Symbol, symbol),
                Builders<TradingSignal>.Filter.Gte(x => x.CreatedAt, cutoffTime),
                Builders<TradingSignal>.Filter.Eq(x => x.Sent, true)
            );

            var count = await _signalCollection.CountDocumentsAsync(filter);
            return count > 0;
        }

        private string GenerateSignalHash(string symbol, string signalType, double rsi)
        {
            var hashInput = $"{symbol}_{signalType}_{Math.Round(rsi, 0)}_{DateTime.UtcNow:yyyyMMddHH}";
            return hashInput.GetHashCode().ToString();
        }

        public async Task MarkSignalAsSentAsync(ObjectId signalId)
        {
            var update = Builders<TradingSignal>.Update
                .Set(x => x.Sent, true)
                .Set(x => x.SentAt, DateTime.UtcNow);

            await _signalCollection.UpdateOneAsync(x => x.Id == signalId, update);
        }
    }
}