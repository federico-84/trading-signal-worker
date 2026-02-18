// SimplifiedEnhancedSignalFilterService.cs - CON CACHE E LOGGING

using MongoDB.Bson;
using MongoDB.Driver;
using PortfolioSignalWorker.Models;
using Newtonsoft.Json.Linq;

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
                _logger.LogDebug($"🔍 Starting QUALITY analysis for {symbol}");

                // 0. Check if symbol is eligible (anti-spam)
                if (!await IsSymbolEligibleForSignal(symbol))
                {
                    _logger.LogDebug($"🚫 {symbol} not eligible - recent signal exists");
                    return null;
                }

                // 🟢 1. Ottieni dati storici CON CACHE
                var historicalData = await GetHistoricalDataWithCacheAsync(symbol, 50);
                _logger.LogInformation($"🔍 {symbol}: Retrieved {historicalData.Count} historical records");

                if (historicalData.Count < 20)
                {
                    _logger.LogWarning($"🔍 {symbol}: Insufficient data ({historicalData.Count} days), skipping");
                    return null;
                }

                // 2. Calcola indicatori avanzati
                _logger.LogDebug($"🔍 {symbol}: Calculating advanced indicators");
                var enhancedIndicator = await CalculateAdvancedIndicators(symbol, currentIndicator, historicalData);
                _logger.LogInformation($"🔍 {symbol}: Confluence score = {enhancedIndicator.ConfluenceScore}/100 (QUALITY rules)");

                // 3. Analizza confluence e genera segnale
                _logger.LogDebug($"🔍 {symbol}: Generating quality-based signal");
                var signal = await GenerateQualityBasedSignal(symbol, enhancedIndicator, historicalData);

                if (signal != null)
                {
                    _logger.LogInformation($"🎯 {symbol}: Generated {signal.Type} signal with {signal.Confidence}% confidence (QUALITY)");
                }
                else
                {
                    _logger.LogDebug($"🔍 {symbol}: No quality signal found (maintaining standards)");
                }

                return signal;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "🚨 Error in quality analysis for {symbol}", symbol);
                return null;
            }
        }

        #region CACHE METHODS - NUOVO

        // 🟢 METODO PRINCIPALE CON CACHE
        private async Task<List<StockIndicator>> GetHistoricalDataWithCacheAsync(string symbol, int periods = 50)
        {
            try
            {
                _logger.LogDebug($"[CACHE] 🔍 Checking cache for {symbol}");

                // STEP 1: Cerca in MongoDB PRIMA
                var mongoData = await _indicatorCollection
                    .Find(x => x.Symbol == symbol)
                    .SortByDescending(x => x.CreatedAt)
                    .Limit(periods)
                    .ToListAsync();

                // STEP 2: Cache HIT?
                if (mongoData.Count >= 20)
                {
                    var lastUpdate = mongoData.First().CreatedAt;
                    var hoursSinceUpdate = (DateTime.UtcNow - lastUpdate).TotalHours;

                    // Cache valida per 4 ore
                    if (hoursSinceUpdate < 4)
                    {
                        _logger.LogInformation($"[CACHE] 📦 HIT for {symbol}: {mongoData.Count} days, {hoursSinceUpdate:F1}h old");
                        return mongoData;
                    }
                    else
                    {
                        _logger.LogInformation($"[CACHE] ⏰ STALE for {symbol}: {hoursSinceUpdate:F1}h old, refreshing...");
                    }
                }
                else
                {
                    _logger.LogInformation($"[CACHE] 📭 MISS for {symbol}: only {mongoData.Count} days in cache");
                }

                // STEP 3: Cache miss/stale → Yahoo
                _logger.LogInformation($"[CACHE] ☁️ Fetching from Yahoo: {symbol}");

                var yahooData = await _yahooFinance.GetHistoricalDataAsync(symbol, periods);
                var indicators = await ConvertYahooDataToIndicatorsAsync(symbol, yahooData);

                if (indicators.Count == 0)
                {
                    _logger.LogWarning($"[CACHE] ⚠️ {symbol}: Yahoo returned 0 indicators!");
                    // Fallback: usa cache vecchia se esiste
                    if (mongoData?.Any() == true)
                    {
                        _logger.LogWarning($"[CACHE] ↩️ Using stale cache for {symbol}");
                        return mongoData;
                    }
                    return new List<StockIndicator>();
                }

                // STEP 4: Salva in MongoDB (incrementale)
                await SaveNewIndicatorsAsync(symbol, indicators);

                return indicators.Take(periods).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[CACHE] 🔴 Error getting historical data for {symbol}");

                // Fallback: usa cache anche se vecchia
                var fallbackData = await _indicatorCollection
                    .Find(x => x.Symbol == symbol)
                    .SortByDescending(x => x.CreatedAt)
                    .Limit(periods)
                    .ToListAsync();

                if (fallbackData?.Any() == true)
                {
                    _logger.LogWarning($"[CACHE] ⚠️ Using stale cache for {symbol} due to error");
                    return fallbackData;
                }

                return new List<StockIndicator>();
            }
        }

        // 🟢 CONVERTI DATI YAHOO IN INDICATORS
        private async Task<List<StockIndicator>> ConvertYahooDataToIndicatorsAsync(string symbol, JObject yahooData)
        {
            try
            {
                var closes = yahooData["c"]?.ToObject<List<double>>() ?? new List<double>();
                var volumes = yahooData["v"]?.ToObject<List<long>>() ?? new List<long>();
                var highs = yahooData["h"]?.ToObject<List<double>>() ?? new List<double>();
                var lows = yahooData["l"]?.ToObject<List<double>>() ?? new List<double>();
                var opens = yahooData["o"]?.ToObject<List<double>>() ?? new List<double>();
                var timestamps = yahooData["t"]?.ToObject<List<long>>() ?? new List<long>();

                _logger.LogDebug($"[CONVERT] 🔄 Converting {closes.Count} days for {symbol}");

                if (closes.Count == 0)
                {
                    _logger.LogWarning($"[CONVERT] ⚠️ {symbol}: No price data from Yahoo!");
                    return new List<StockIndicator>();
                }

                var indicators = new List<StockIndicator>();

                // Inizia da indice 26 (serve per MACD)
                for (int i = 26; i < closes.Count; i++)
                {
                    var pricesUpToI = closes.Take(i + 1).ToList();
                    var rsi = _yahooFinance.CalculateRSI(pricesUpToI);
                    var (macd, signal, histogram) = _yahooFinance.CalculateMACD(pricesUpToI);

                    indicators.Add(new StockIndicator
                    {
                        Symbol = symbol,
                        RSI = Math.Round(rsi, 2),
                        MACD = Math.Round(macd, 4),
                        MACD_Signal = Math.Round(signal, 4),
                        MACD_Histogram = Math.Round(histogram, 4),
                        Price = closes[i],
                        Volume = volumes[i],
                        DayHigh = highs[i],
                        DayLow = lows[i],
                        Open = opens[i],
                        PreviousClose = i > 0 ? closes[i - 1] : closes[i],
                        Change = i > 0 ? closes[i] - closes[i - 1] : 0,
                        ChangePercent = i > 0 && closes[i - 1] != 0 ? ((closes[i] - closes[i - 1]) / closes[i - 1]) * 100 : 0,
                        CreatedAt = DateTimeOffset.FromUnixTimeSeconds(timestamps[i]).UtcDateTime
                    });
                }

                _logger.LogInformation($"[CONVERT] ✅ Converted {indicators.Count} indicators for {symbol}");

                return indicators;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[CONVERT] 🔴 Error converting Yahoo data for {symbol}");
                return new List<StockIndicator>();
            }
        }

        // 🟢 SALVA SOLO NUOVI RECORDS
        private async Task SaveNewIndicatorsAsync(string symbol, List<StockIndicator> indicators)
        {
            try
            {
                if (!indicators.Any())
                {
                    _logger.LogDebug($"[SAVE] ℹ️ No indicators to save for {symbol}");
                    return;
                }

                // Trova ultimo salvato
                var lastSaved = await _indicatorCollection
                    .Find(x => x.Symbol == symbol)
                    .SortByDescending(x => x.CreatedAt)
                    .FirstOrDefaultAsync();

                var lastSavedDate = lastSaved?.CreatedAt.Date ?? DateTime.MinValue;

                _logger.LogDebug($"[SAVE] 💾 Last saved for {symbol}: {lastSavedDate:yyyy-MM-dd}");

                // Salva solo nuovi
                var newRecords = indicators
                    .Where(x => x.CreatedAt.Date > lastSavedDate)
                    .ToList();

                if (newRecords.Any())
                {
                    await _indicatorCollection.InsertManyAsync(newRecords);
                    _logger.LogInformation($"[SAVE] ✅ Saved {newRecords.Count} NEW records for {symbol}");
                }
                else
                {
                    _logger.LogDebug($"[SAVE] ℹ️ No new records to save for {symbol}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"[SAVE] ⚠️ Error saving indicators for {symbol}");
            }
        }

        #endregion

        private async Task<TradingSignal?> GenerateQualityBasedSignal(
            string symbol,
            EnhancedIndicator enhanced,
            List<StockIndicator> historical)
        {
            // Controlla duplicati recenti
            if (await HasRecentSignalAsync(symbol, TimeSpan.FromHours(6)))
            {
                return null;
            }

            // 🔍 LOG DETTAGLIATO
            _logger.LogInformation($"🔍 QUALITY CHECK {symbol}:");
            _logger.LogInformation($"   📊 Confluence: {enhanced.ConfluenceScore}/100");
            _logger.LogInformation($"   📈 Trend: {enhanced.TrendDirection} (Strength: {enhanced.TrendStrength:F1})");
            _logger.LogInformation($"   📉 RSI: {enhanced.RSI:F1}");
            _logger.LogInformation($"   📊 MACD: {enhanced.MACD_Histogram:F3} (CrossUp: {enhanced.MACD_Histogram_CrossUp})");
            _logger.LogInformation($"   📦 Volume: {enhanced.VolumeRatio:F2}x avg (Breakout: {enhanced.IsVolumeBreakout})");
            _logger.LogInformation($"   🎯 Support: {enhanced.SupportLevel:F2} (Dist: {enhanced.DistanceFromSupport:F1}%)");
            _logger.LogInformation($"   🎯 Resistance: {enhanced.ResistanceLevel:F2} (Dist: {enhanced.DistanceFromResistance:F1}%)");

            var isStrongBuy = IsStrongBuySetup(enhanced);
            _logger.LogInformation($"   🚀 Strong Buy (75+): {(isStrongBuy ? "✅ YES" : "❌ NO")}");

            // 🚀 STRONG BUY
            if (IsStrongBuySetup(enhanced))
            {
                return CreateEnhancedSignal(symbol, enhanced, SignalType.Buy,
                    Math.Min(95, enhanced.ConfluenceScore + 5),
                    "STRONG BUY: Excellent confluence with confirmed breakout");
            }

            // 📈 MEDIUM BUY
            if (IsMediumBuySetup(enhanced))
            {
                return CreateEnhancedSignal(symbol, enhanced, SignalType.Buy,
                    Math.Min(85, enhanced.ConfluenceScore),
                    "MEDIUM BUY: Good technical setup with volume confirmation");
            }

            // ⚠️ WARNING
            if (IsWarningSetup(enhanced))
            {
                return CreateEnhancedSignal(symbol, enhanced, SignalType.Warning,
                    Math.Min(75, enhanced.ConfluenceScore),
                    "WARNING: Extreme oversold near strong support");
            }

            return null;
        }

        #region Signal Conditions - VERSIONE QUALITÀ

        private bool IsStrongBuySetup(EnhancedIndicator enhanced)
        {
            return enhanced.ConfluenceScore >= 75 &&
                   enhanced.TrendDirection == TrendDirection.Bullish &&
                   enhanced.RSI >= 20 && enhanced.RSI <= 40 &&
                   enhanced.MACD_Histogram > 0 &&
                   enhanced.VolumeRatio > 1.5 &&
                   enhanced.DistanceFromSupport <= 5 &&
                   enhanced.DistanceFromResistance > 8 &&
                   !enhanced.RSI_Divergence;
        }

        private bool IsMediumBuySetup(EnhancedIndicator enhanced)
        {
            return enhanced.ConfluenceScore >= 60 &&
                   enhanced.TrendDirection != TrendDirection.Bearish &&
                   enhanced.RSI >= 20 && enhanced.RSI <= 45 &&
                   (enhanced.MACD_Histogram > 0 || enhanced.MACD_Histogram_CrossUp) &&
                   enhanced.VolumeRatio > 1.2;
        }

        private bool IsWarningSetup(EnhancedIndicator enhanced)
        {
            return enhanced.ConfluenceScore >= 50 &&
                   (enhanced.RSI <= 25 ||
                    (enhanced.TrendDirection == TrendDirection.Bearish &&
                     enhanced.RSI <= 30 &&
                     enhanced.VolumeRatio > 1.5)) &&
                   enhanced.DistanceFromSupport <= 3;
        }

        #endregion

        #region Advanced Indicators Calculation

        private async Task<EnhancedIndicator> CalculateAdvancedIndicators(
            string symbol,
            StockIndicator current,
            List<StockIndicator> historical)
        {
            var enhanced = new EnhancedIndicator(current);

            var prices = historical.Select(h => h.Price).Reverse().ToList();
            prices.Add(current.Price);

            var volumes = historical.Select(h => h.Volume).Reverse().ToList();
            volumes.Add(current.Volume);

            enhanced.EMA20 = CalculateEMA(prices, 20);
            enhanced.EMA50 = CalculateEMA(prices, 50);
            enhanced.TrendDirection = ClassifyTrend(enhanced.EMA20, enhanced.EMA50, current.Price);
            enhanced.TrendStrength = CalculateTrendStrength(prices.TakeLast(20).ToList());

            var (support, resistance) = CalculateKeyLevels(prices, current.Price);
            enhanced.SupportLevel = support;
            enhanced.ResistanceLevel = resistance;
            enhanced.DistanceFromSupport = support > 0 ? ((current.Price - support) / support) * 100 : 0;
            enhanced.DistanceFromResistance = resistance > 0 ? ((resistance - current.Price) / current.Price) * 100 : 0;

            var avgVolume = volumes.Count >= 20 ? volumes.TakeLast(20).Average() : volumes.Average();
            enhanced.VolumeRatio = avgVolume > 0 ? current.Volume / avgVolume : 1;
            enhanced.IsVolumeBreakout = enhanced.VolumeRatio > 1.5;

            enhanced.RSI_Trend = CalculateRSITrend(historical);
            enhanced.RSI_Divergence = DetectRSIDivergence(historical, prices.TakeLast(historical.Count).ToList());

            enhanced.MACD_Strength = CalculateMACDStrength(historical);
            enhanced.MACD_Trend = ClassifyMACDTrend(historical);

            enhanced.Volatility = CalculateVolatility(prices.TakeLast(14).ToList());
            enhanced.ConfluenceScore = CalculateQualityConfluenceScore(enhanced);

            return enhanced;
        }

        private int CalculateQualityConfluenceScore(EnhancedIndicator enhanced)
        {
            int score = 0;

            score += enhanced.TrendDirection switch
            {
                TrendDirection.Bullish => 25,
                TrendDirection.Sideways => 10,
                TrendDirection.Bearish => 0,
                _ => 5
            };

            if (enhanced.RSI >= 20 && enhanced.RSI <= 40) score += 20;
            else if (enhanced.RSI >= 15 && enhanced.RSI <= 50) score += 15;
            else if (enhanced.RSI >= 10 && enhanced.RSI <= 60) score += 10;

            if (enhanced.MACD_Histogram > 0 && enhanced.MACD_Trend == "BULLISH") score += 20;
            else if (enhanced.MACD_Histogram > 0) score += 15;
            else if (enhanced.MACD_Histogram_CrossUp) score += 10;
            else if (enhanced.MACD_Histogram > -0.05) score += 5;

            if (enhanced.IsVolumeBreakout && enhanced.VolumeRatio > 2.0) score += 15;
            else if (enhanced.VolumeRatio > 1.5) score += 12;
            else if (enhanced.VolumeRatio > 1.2) score += 8;
            else if (enhanced.VolumeRatio > 1.0) score += 4;

            if (enhanced.DistanceFromSupport <= 3 && enhanced.DistanceFromResistance > 10) score += 20;
            else if (enhanced.DistanceFromSupport <= 5 && enhanced.DistanceFromResistance > 8) score += 15;
            else if (enhanced.DistanceFromSupport <= 8 && enhanced.DistanceFromResistance > 5) score += 10;
            else if (enhanced.DistanceFromSupport <= 10) score += 5;

            return Math.Min(100, score);
        }

        #endregion

        #region Technical Calculations

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
            return Math.Min(10, change);
        }

        private (double support, double resistance) CalculateKeyLevels(List<double> prices, double currentPrice)
        {
            if (prices.Count < 20)
                return (currentPrice * 0.95, currentPrice * 1.05);

            var recentLows = new List<double>();
            var recentHighs = new List<double>();

            for (int i = 2; i < prices.Count - 2; i++)
            {
                if (prices[i] < prices[i - 1] && prices[i] < prices[i - 2] &&
                    prices[i] < prices[i + 1] && prices[i] < prices[i + 2])
                {
                    recentLows.Add(prices[i]);
                }

                if (prices[i] > prices[i - 1] && prices[i] > prices[i - 2] &&
                    prices[i] > prices[i + 1] && prices[i] > prices[i + 2])
                {
                    recentHighs.Add(prices[i]);
                }
            }

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

            var midPoint = historical.Count / 2;

            var firstHalfPriceHigh = prices.Take(midPoint).Max();
            var secondHalfPriceHigh = prices.Skip(midPoint).Max();

            var firstHalfRSIHigh = historical.Take(midPoint).Max(h => h.RSI);
            var secondHalfRSIHigh = historical.Skip(midPoint).Max(h => h.RSI);

            return secondHalfPriceHigh > firstHalfPriceHigh &&
                   secondHalfRSIHigh < firstHalfRSIHigh;
        }

        private double CalculateMACDStrength(List<StockIndicator> historical)
        {
            if (historical.Count < 5) return 5;

            var recent = historical.TakeLast(5).ToList();
            var momentum = recent.Last().MACD_Histogram - recent.First().MACD_Histogram;

            return Math.Min(10, Math.Max(1, 5 + momentum * 10));
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

            return Math.Sqrt(variance) * 100;
        }

        #endregion

        #region Utility Methods

        private TradingSignal CreateEnhancedSignal(string symbol, EnhancedIndicator enhanced, SignalType type,
            double confidence, string baseReason)
        {
            var reasons = new List<string> { baseReason };

            if (enhanced.TrendDirection == TrendDirection.Bullish)
                reasons.Add("Strong bullish trend");

            if (enhanced.IsVolumeBreakout)
                reasons.Add($"Volume breakout ({enhanced.VolumeRatio:F1}x)");
            else if (enhanced.VolumeRatio > 1.2)
                reasons.Add($"Volume confirmed ({enhanced.VolumeRatio:F1}x)");

            if (enhanced.DistanceFromSupport <= 5)
                reasons.Add("Near key support");

            if (enhanced.MACD_Trend == "BULLISH")
                reasons.Add("MACD momentum");

            reasons.Add($"Quality score: {enhanced.ConfluenceScore}/100");

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
                SupportLevel = enhanced.SupportLevel,
                ResistanceLevel = enhanced.ResistanceLevel,
                VolumeStrength = Math.Min(10, enhanced.VolumeRatio * 3),
                TrendStrength = enhanced.TrendStrength,
                MarketCondition = $"{enhanced.TrendDirection} (Q:{enhanced.ConfluenceScore}/100)"
            };
        }

        private async Task<bool> IsSymbolEligibleForSignal(string symbol)
        {
            var recentSignalCount = await CountRecentSignalsForSymbol(symbol, TimeSpan.FromHours(24));

            if (recentSignalCount >= 2)
            {
                _logger.LogDebug($"Symbol {symbol} already has {recentSignalCount} signals today");
                return false;
            }

            return true;
        }

        private async Task<int> CountRecentSignalsForSymbol(string symbol, TimeSpan timeWindow)
        {
            var cutoffTime = DateTime.UtcNow.Subtract(timeWindow);
            var filter = Builders<TradingSignal>.Filter.And(
                Builders<TradingSignal>.Filter.Eq(x => x.Symbol, symbol),
                Builders<TradingSignal>.Filter.Gte(x => x.CreatedAt, cutoffTime),
                Builders<TradingSignal>.Filter.Eq(x => x.Sent, true)
            );

            return (int)await _signalCollection.CountDocumentsAsync(filter);
        }

        // 🟢 DEPRECATO - Usa GetHistoricalDataWithCacheAsync
        private async Task<List<StockIndicator>> GetHistoricalDataAsync(string symbol, int periods)
        {
            return await GetHistoricalDataWithCacheAsync(symbol, periods);
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