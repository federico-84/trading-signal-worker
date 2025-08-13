using MongoDB.Bson;
using MongoDB.Driver;
using PortfolioSignalWorker.Models;

namespace PortfolioSignalWorker.Services
{
    public class SimplifiedEnhancedSignalFilterService
    {
        private readonly IMongoCollection<StockIndicator> _indicatorCollection;
        private readonly IMongoCollection<TradingSignal> _signalCollection;
        private readonly YahooFinanceService _yahooFinance;
        private readonly ILogger<SimplifiedEnhancedSignalFilterService> _logger;

        public SimplifiedEnhancedSignalFilterService(
            IMongoDatabase database,
            YahooFinanceService yahooFinance,
            ILogger<SimplifiedEnhancedSignalFilterService> logger)
        {
            _indicatorCollection = database.GetCollection<StockIndicator>("Indicators");
            _signalCollection = database.GetCollection<TradingSignal>("TradingSignals");
            _yahooFinance = yahooFinance;
            _logger = logger;
        }

        public async Task<TradingSignal?> AnalyzeEnhancedSignalAsync(string symbol, StockIndicator currentIndicator)
        {
            try
            {
                _logger.LogDebug($"🔍 Starting enhanced analysis for {symbol}");

                // 1. Ottieni dati storici
                var historicalData = await GetHistoricalDataAsync(symbol, 50);
                _logger.LogInformation($"🔍 {symbol}: Retrieved {historicalData.Count} historical records");

                if (historicalData.Count < 20)
                {
                    _logger.LogWarning($"🔍 {symbol}: Insufficient data ({historicalData.Count} days), using basic signal");
                    return await GenerateBasicSignal(symbol, currentIndicator);
                }

                // 2. Calcola indicatori avanzati
                _logger.LogDebug($"🔍 {symbol}: Calculating advanced indicators");
                var enhancedIndicator = await CalculateAdvancedIndicators(symbol, currentIndicator, historicalData);
                _logger.LogInformation($"🔍 {symbol}: Confluence score = {enhancedIndicator.ConfluenceScore}/100");

                // 3. Analizza confluence e genera segnale
                _logger.LogDebug($"🔍 {symbol}: Generating confluence-based signal");
                var signal = await GenerateConfluenceBasedSignal(symbol, enhancedIndicator, historicalData);

                if (signal != null)
                {
                    _logger.LogInformation($"🎯 {symbol}: Generated {signal.Type} signal with {signal.Confidence}% confidence");
                }
                else
                {
                    _logger.LogDebug($"🔍 {symbol}: No valid signal found");
                }

                return signal;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "🚨 Error in enhanced analysis for {symbol}", symbol);
                return await GenerateBasicSignal(symbol, currentIndicator);
            }
        }

        private async Task<EnhancedIndicator> CalculateAdvancedIndicators(
            string symbol,
            StockIndicator current,
            List<StockIndicator> historical)
        {
            var enhanced = new EnhancedIndicator(current);

            // Ottieni dati di prezzo per calcoli avanzati
            var prices = historical.Select(h => h.Price).Reverse().ToList();
            prices.Add(current.Price);

            var volumes = historical.Select(h => h.Volume).Reverse().ToList();
            volumes.Add(current.Volume);

            // 1. TREND ANALYSIS - EMA multipli
            enhanced.EMA20 = CalculateEMA(prices, 20);
            enhanced.EMA50 = CalculateEMA(prices, 50);

            // 2. TREND CLASSIFICATION
            enhanced.TrendDirection = ClassifyTrend(enhanced.EMA20, enhanced.EMA50, current.Price);
            enhanced.TrendStrength = CalculateTrendStrength(prices.TakeLast(20).ToList());

            // 3. SUPPORT & RESISTANCE
            var (support, resistance) = CalculateKeyLevels(prices, current.Price);
            enhanced.SupportLevel = support;
            enhanced.ResistanceLevel = resistance;
            enhanced.DistanceFromSupport = support > 0 ? ((current.Price - support) / support) * 100 : 0;
            enhanced.DistanceFromResistance = resistance > 0 ? ((resistance - current.Price) / current.Price) * 100 : 0;

            // 4. VOLUME ANALYSIS
            var avgVolume = volumes.Count >= 20 ? volumes.TakeLast(20).Average() : volumes.Average();
            enhanced.VolumeRatio = avgVolume > 0 ? current.Volume / avgVolume : 1;
            enhanced.IsVolumeBreakout = enhanced.VolumeRatio > 1.5;

            // 5. RSI ENHANCEMENTS
            enhanced.RSI_Trend = CalculateRSITrend(historical);
            enhanced.RSI_Divergence = DetectRSIDivergence(historical, prices.TakeLast(historical.Count).ToList());

            // 6. MACD ENHANCEMENTS  
            enhanced.MACD_Strength = CalculateMACDStrength(historical);
            enhanced.MACD_Trend = ClassifyMACDTrend(historical);

            // 7. VOLATILITY
            enhanced.Volatility = CalculateVolatility(prices.TakeLast(14).ToList());

            // 8. CONFLUENCE SCORE
            enhanced.ConfluenceScore = CalculateConfluenceScore(enhanced);

            return enhanced;
        }

        private async Task<TradingSignal?> GenerateConfluenceBasedSignal(
            string symbol,
            EnhancedIndicator enhanced,
            List<StockIndicator> historical)
        {
            // Controlla duplicati recenti
            if (await HasRecentSignalAsync(symbol, TimeSpan.FromHours(2)))
            {
                return null;
            }

            // 🚀 STRONG BUY - Confluence perfetta (Score 85+)
            if (IsStrongBuySetup(enhanced))
            {
                return CreateEnhancedSignal(symbol, enhanced, SignalType.Buy,
                    Math.Min(95, enhanced.ConfluenceScore),
                    "STRONG BUY: Perfect confluence detected");
            }

            // 📈 MEDIUM BUY - Buona confluence (Score 70+)
            if (IsMediumBuySetup(enhanced))
            {
                return CreateEnhancedSignal(symbol, enhanced, SignalType.Buy,
                    Math.Min(85, enhanced.ConfluenceScore),
                    "MEDIUM BUY: Good technical setup");
            }

            // ⚠️ WARNING - Condizioni interessanti ma rischiose
            if (IsWarningSetup(enhanced))
            {
                return CreateEnhancedSignal(symbol, enhanced, SignalType.Warning,
                    Math.Min(75, enhanced.ConfluenceScore),
                    "WARNING: Oversold with risk");
            }

            // 📉 SELL - Deterioramento tecnico
            if (IsSellSetup(enhanced))
            {
                return CreateEnhancedSignal(symbol, enhanced, SignalType.Sell,
                    Math.Min(80, enhanced.ConfluenceScore),
                    "SELL: Technical breakdown");
            }

            return null;
        }

        #region Signal Conditions (Simplified but Powerful)

        private bool IsStrongBuySetup(EnhancedIndicator enhanced)
        {
            return enhanced.ConfluenceScore >= 85 &&
                   enhanced.TrendDirection != TrendDirection.Bearish &&
                   enhanced.RSI >= 25 && enhanced.RSI <= 45 && // Sweet spot oversold
                   enhanced.MACD_Histogram > 0 &&
                   enhanced.IsVolumeBreakout &&
                   enhanced.DistanceFromSupport <= 5 && // Near support
                   enhanced.DistanceFromResistance > 8 && // Far from resistance
                   !enhanced.RSI_Divergence; // No negative divergence
        }

        private bool IsMediumBuySetup(EnhancedIndicator enhanced)
        {
            return enhanced.ConfluenceScore >= 70 &&
                   enhanced.TrendDirection == TrendDirection.Bullish &&
                   enhanced.RSI >= 20 && enhanced.RSI <= 50 &&
                   (enhanced.MACD_Histogram > 0 || enhanced.MACD_Histogram_CrossUp) &&
                   enhanced.VolumeRatio > 1.2; // Above average volume
        }

        private bool IsWarningSetup(EnhancedIndicator enhanced)
        {
            return enhanced.RSI <= 25 || // Extremely oversold
                   (enhanced.TrendDirection == TrendDirection.Bearish && enhanced.RSI <= 35) ||
                   (enhanced.DistanceFromSupport <= 2 && enhanced.VolumeRatio > 1.3); // Near key support with volume
        }

        private bool IsSellSetup(EnhancedIndicator enhanced)
        {
            return enhanced.TrendDirection == TrendDirection.Bearish &&
                   enhanced.RSI > 70 &&
                   enhanced.MACD_Histogram < 0 &&
                   enhanced.DistanceFromResistance <= 3 &&
                   enhanced.RSI_Divergence; // Negative divergence
        }

        #endregion

        #region Technical Calculations (Core Logic)

        private double CalculateEMA(List<double> prices, int period)
        {
            if (prices.Count < period) return prices.LastOrDefault();

            var multiplier = 2.0 / (period + 1);
            var ema = prices.Take(period).Average();

            for (int i = period; i < prices.Count; i++)
            {
                ema = (prices[i] * multiplier) + (ema * (1 - multiplier));
            }

            return ema;
        }

        private TrendDirection ClassifyTrend(double ema20, double ema50, double price)
        {
            if (price > ema20 && ema20 > ema50)
                return TrendDirection.Bullish;

            if (price < ema20 && ema20 < ema50)
                return TrendDirection.Bearish;

            return TrendDirection.Sideways;
        }

        private double CalculateTrendStrength(List<double> recentPrices)
        {
            if (recentPrices.Count < 10) return 5;

            var oldPrice = recentPrices.First();
            var newPrice = recentPrices.Last();

            var change = Math.Abs((newPrice - oldPrice) / oldPrice) * 100;
            return Math.Min(10, change); // Scale 0-10
        }

        private (double support, double resistance) CalculateKeyLevels(List<double> prices, double currentPrice)
        {
            if (prices.Count < 20)
                return (currentPrice * 0.95, currentPrice * 1.05);

            // Simple but effective: trova minimi e massimi locali
            var recentLows = new List<double>();
            var recentHighs = new List<double>();

            for (int i = 2; i < prices.Count - 2; i++)
            {
                // Swing low
                if (prices[i] < prices[i - 1] && prices[i] < prices[i - 2] &&
                    prices[i] < prices[i + 1] && prices[i] < prices[i + 2])
                {
                    recentLows.Add(prices[i]);
                }

                // Swing high
                if (prices[i] > prices[i - 1] && prices[i] > prices[i - 2] &&
                    prices[i] > prices[i + 1] && prices[i] > prices[i + 2])
                {
                    recentHighs.Add(prices[i]);
                }
            }

            // Trova supporto e resistenza più significativi
            var support = recentLows.Where(l => l < currentPrice * 0.98)
                                   .OrderByDescending(l => l)
                                   .FirstOrDefault();

            var resistance = recentHighs.Where(h => h > currentPrice * 1.02)
                                       .OrderBy(h => h)
                                       .FirstOrDefault();

            return (support > 0 ? support : currentPrice * 0.95,
                    resistance > 0 ? resistance : currentPrice * 1.05);
        }

        private string CalculateRSITrend(List<StockIndicator> historical)
        {
            if (historical.Count < 10) return "UNKNOWN";

            var recent5 = historical.TakeLast(5).Average(h => h.RSI);
            var previous5 = historical.Skip(historical.Count - 10).Take(5).Average(h => h.RSI);

            if (recent5 > previous5 + 3) return "RISING";
            if (recent5 < previous5 - 3) return "FALLING";
            return "STABLE";
        }

        private bool DetectRSIDivergence(List<StockIndicator> historical, List<double> prices)
        {
            if (historical.Count < 15 || prices.Count < 15) return false;

            // Cerca divergenza bearish negli ultimi 15 periodi
            var midPoint = historical.Count / 2;

            var firstHalfPriceHigh = prices.Take(midPoint).Max();
            var secondHalfPriceHigh = prices.Skip(midPoint).Max();

            var firstHalfRSIHigh = historical.Take(midPoint).Max(h => h.RSI);
            var secondHalfRSIHigh = historical.Skip(midPoint).Max(h => h.RSI);

            // Divergenza: prezzo fa massimi più alti, RSI fa massimi più bassi
            return secondHalfPriceHigh > firstHalfPriceHigh &&
                   secondHalfRSIHigh < firstHalfRSIHigh;
        }

        private double CalculateMACDStrength(List<StockIndicator> historical)
        {
            if (historical.Count < 5) return 5;

            var recent = historical.TakeLast(5).ToList();
            var momentum = recent.Last().MACD_Histogram - recent.First().MACD_Histogram;

            return Math.Min(10, Math.Max(1, 5 + momentum * 10)); // Scale 1-10
        }

        private string ClassifyMACDTrend(List<StockIndicator> historical)
        {
            if (historical.Count < 8) return "UNKNOWN";

            var recent = historical.TakeLast(8).Select(h => h.MACD_Histogram).ToList();

            var increasing = 0;
            var decreasing = 0;

            for (int i = 1; i < recent.Count; i++)
            {
                if (recent[i] > recent[i - 1]) increasing++;
                else if (recent[i] < recent[i - 1]) decreasing++;
            }

            if (increasing > decreasing * 1.5) return "BULLISH";
            if (decreasing > increasing * 1.5) return "BEARISH";
            return "SIDEWAYS";
        }

        private double CalculateVolatility(List<double> prices)
        {
            if (prices.Count < 2) return 0;

            var returns = new List<double>();
            for (int i = 1; i < prices.Count; i++)
            {
                returns.Add((prices[i] - prices[i - 1]) / prices[i - 1]);
            }

            var mean = returns.Average();
            var variance = returns.Sum(r => Math.Pow(r - mean, 2)) / returns.Count;

            return Math.Sqrt(variance) * 100; // As percentage
        }

        private int CalculateConfluenceScore(EnhancedIndicator enhanced)
        {
            int score = 0;

            // 1. Trend (0-25 points)
            score += enhanced.TrendDirection switch
            {
                TrendDirection.Bullish => 25,
                TrendDirection.Sideways => 10,
                TrendDirection.Bearish => 0,
                _ => 10
            };

            // 2. RSI positioning (0-20 points)
            if (enhanced.RSI >= 25 && enhanced.RSI <= 45) score += 20; // Sweet spot
            else if (enhanced.RSI >= 20 && enhanced.RSI <= 50) score += 15;
            else if (enhanced.RSI >= 15 && enhanced.RSI <= 60) score += 10;

            // 3. MACD (0-20 points)
            if (enhanced.MACD_Histogram > 0 && enhanced.MACD_Trend == "BULLISH") score += 20;
            else if (enhanced.MACD_Histogram > 0) score += 15;
            else if (enhanced.MACD_Histogram_CrossUp) score += 10;

            // 4. Volume (0-15 points)
            if (enhanced.IsVolumeBreakout && enhanced.VolumeRatio > 2.0) score += 15;
            else if (enhanced.VolumeRatio > 1.5) score += 10;
            else if (enhanced.VolumeRatio > 1.2) score += 5;

            // 5. Support/Resistance positioning (0-20 points)
            if (enhanced.DistanceFromSupport <= 3 && enhanced.DistanceFromResistance > 10) score += 20;
            else if (enhanced.DistanceFromSupport <= 5 && enhanced.DistanceFromResistance > 8) score += 15;
            else if (enhanced.DistanceFromSupport <= 8) score += 10;

            return Math.Min(100, score);
        }

        #endregion

        #region Utility Methods

        private TradingSignal CreateEnhancedSignal(string symbol, EnhancedIndicator enhanced, SignalType type,
            double confidence, string baseReason)
        {
            var reasons = new List<string> { baseReason };

            // Aggiungi dettagli alla spiegazione
            if (enhanced.TrendDirection == TrendDirection.Bullish)
                reasons.Add("Bullish trend");

            if (enhanced.IsVolumeBreakout)
                reasons.Add($"Volume spike ({enhanced.VolumeRatio:F1}x)");

            if (enhanced.DistanceFromSupport <= 5)
                reasons.Add("Near support");

            if (enhanced.MACD_Trend == "BULLISH")
                reasons.Add("MACD bullish");

            reasons.Add($"Confluence: {enhanced.ConfluenceScore}/100");

            return new TradingSignal
            {
                Symbol = symbol,
                Type = type,
                Confidence = confidence,
                Reason = string.Join(" | ", reasons),
                RSI = enhanced.RSI,
                MACD_Histogram = enhanced.MACD_Histogram,
                Price = enhanced.Price,
                Volume = enhanced.Volume,
                SignalHash = GenerateSignalHash(symbol, type.ToString(), enhanced.RSI),

                // Enhanced fields
                SupportLevel = enhanced.SupportLevel,
                ResistanceLevel = enhanced.ResistanceLevel,
                VolumeStrength = Math.Min(10, enhanced.VolumeRatio * 3),
                TrendStrength = enhanced.TrendStrength,
                MarketCondition = $"{enhanced.TrendDirection} ({enhanced.ConfluenceScore}/100)"
            };
        }

        private async Task<TradingSignal?> GenerateBasicSignal(string symbol, StockIndicator current)
        {
            // Fallback per quando non abbiamo abbastanza dati
            if (await HasRecentSignalAsync(symbol, TimeSpan.FromHours(2)))
                return null;

            if (current.RSI < 25 && current.MACD_Histogram_CrossUp)
            {
                return new TradingSignal
                {
                    Symbol = symbol,
                    Type = SignalType.Buy,
                    Confidence = 65,
                    Reason = "BASIC BUY: RSI oversold + MACD cross (limited data)",
                    RSI = current.RSI,
                    MACD_Histogram = current.MACD_Histogram,
                    Price = current.Price,
                    Volume = current.Volume,
                    SignalHash = GenerateSignalHash(symbol, "BASIC_BUY", current.RSI)
                };
            }

            return null;
        }

        private async Task<List<StockIndicator>> GetHistoricalDataAsync(string symbol, int periods)
        {
            var filter = Builders<StockIndicator>.Filter.Eq(x => x.Symbol, symbol);
            var sort = Builders<StockIndicator>.Sort.Descending(x => x.CreatedAt);

            return await _indicatorCollection
                .Find(filter)
                .Sort(sort)
                .Limit(periods)
                .ToListAsync();
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

        #endregion
    }

    #region Enhanced Indicator Model

    public class EnhancedIndicator : StockIndicator
    {
        public EnhancedIndicator(StockIndicator baseIndicator)
        {
            // Copia tutti i campi base
            Symbol = baseIndicator.Symbol;
            RSI = baseIndicator.RSI;
            MACD = baseIndicator.MACD;
            MACD_Signal = baseIndicator.MACD_Signal;
            MACD_Histogram = baseIndicator.MACD_Histogram;
            MACD_Histogram_CrossUp = baseIndicator.MACD_Histogram_CrossUp;
            Price = baseIndicator.Price;
            Volume = baseIndicator.Volume;
            PreviousClose = baseIndicator.PreviousClose;
            Change = baseIndicator.Change;
            ChangePercent = baseIndicator.ChangePercent;
            DayHigh = baseIndicator.DayHigh;
            DayLow = baseIndicator.DayLow;
            Open = baseIndicator.Open;
            PricePosition = baseIndicator.PricePosition;
            DailyVolatility = baseIndicator.DailyVolatility;
            CreatedAt = baseIndicator.CreatedAt;
        }

        // Enhanced fields
        public double EMA20 { get; set; }
        public double EMA50 { get; set; }
        public TrendDirection TrendDirection { get; set; }
        public double TrendStrength { get; set; }

        public double SupportLevel { get; set; }
        public double ResistanceLevel { get; set; }
        public double DistanceFromSupport { get; set; }
        public double DistanceFromResistance { get; set; }

        public double VolumeRatio { get; set; }
        public bool IsVolumeBreakout { get; set; }

        public string RSI_Trend { get; set; }
        public bool RSI_Divergence { get; set; }
        public double MACD_Strength { get; set; }
        public string MACD_Trend { get; set; }

        public double Volatility { get; set; }
        public int ConfluenceScore { get; set; }
    }

    public enum TrendDirection
    {
        Bearish,
        Sideways,
        Bullish
    }

    #endregion
}