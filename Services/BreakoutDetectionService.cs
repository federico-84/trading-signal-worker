using MongoDB.Driver;
using Newtonsoft.Json.Linq;
using PortfolioSignalWorker.Models;

namespace PortfolioSignalWorker.Services
{
    public class BreakoutDetectionService
    {
        private readonly YahooFinanceService _yahooFinance;
        private readonly ILogger<BreakoutDetectionService> _logger;
        private readonly IMongoCollection<StockIndicator> _indicatorCollection;

        public BreakoutDetectionService(
            YahooFinanceService yahooFinance,
            IMongoDatabase database,
            ILogger<BreakoutDetectionService> logger)
        {
            _yahooFinance = yahooFinance;
            _indicatorCollection = database.GetCollection<StockIndicator>("Indicators");
            _logger = logger;
        }

        public async Task<BreakoutSignal?> AnalyzeBreakoutPotentialAsync(string symbol)
        {
            try
            {
                _logger.LogDebug($"Analyzing breakout potential for {symbol}");

                // 1. Get extended historical data for pattern analysis
                var historicalData = await _yahooFinance.GetHistoricalDataAsync(symbol, 50);
                var currentQuote = await _yahooFinance.GetQuoteAsync(symbol);

                // 2. Analyze consolidation patterns
                var consolidation = AnalyzeConsolidation(historicalData);

                // 3. Detect compression patterns  
                var compression = DetectCompression(historicalData);

                // 4. Volume analysis for accumulation
                var volumePattern = AnalyzeVolumePattern(historicalData);

                // 5. Support/Resistance strength
                var keyLevels = CalculateKeyLevels(historicalData);

                // 6. Current positioning analysis
                var positioning = AnalyzeCurrentPositioning(currentQuote, keyLevels);

                // 7. Generate breakout signal
                return GenerateBreakoutSignal(symbol, consolidation, compression, volumePattern, keyLevels, positioning);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error analyzing breakout potential for {symbol}", symbol);
                return null;
            }
        }

        private ConsolidationPattern AnalyzeConsolidation(JObject historicalData)
        {
            var closes = historicalData["c"]?.ToObject<List<double>>() ?? new List<double>();
            var highs = historicalData["h"]?.ToObject<List<double>>() ?? new List<double>();
            var lows = historicalData["l"]?.ToObject<List<double>>() ?? new List<double>();

            if (closes.Count < 20)
                return new ConsolidationPattern { IsValid = false };

            // Take last 20 periods for analysis
            var recentCloses = closes.Take(20).ToList();
            var recentHighs = highs.Take(20).ToList();
            var recentLows = lows.Take(20).ToList();

            // Calculate volatility compression
            var priceRange = recentHighs.Max() - recentLows.Min();
            var avgClose = recentCloses.Average();
            var volatilityPercent = (priceRange / avgClose) * 100;

            // Detect consolidation pattern
            var pattern = new ConsolidationPattern
            {
                IsValid = true,
                DurationDays = 20,
                VolatilityPercent = volatilityPercent,
                HighLevel = recentHighs.Max(),
                LowLevel = recentLows.Min(),
                IsCompressing = volatilityPercent < 15, // Less than 15% range = compression
                ConsolidationType = ClassifyConsolidationType(recentHighs, recentLows, recentCloses)
            };

            return pattern;
        }

        private CompressionPattern DetectCompression(JObject historicalData)
        {
            var closes = historicalData["c"]?.ToObject<List<double>>() ?? new List<double>();
            var highs = historicalData["h"]?.ToObject<List<double>>() ?? new List<double>();
            var lows = historicalData["l"]?.ToObject<List<double>>() ?? new List<double>();

            if (closes.Count < 10)
                return new CompressionPattern { IsDetected = false };

            // Calculate Bollinger Bands squeeze equivalent
            var recent10 = closes.Take(10).ToList();
            var sma = recent10.Average();
            var stdDev = Math.Sqrt(recent10.Select(x => Math.Pow(x - sma, 2)).Average());

            // Compare current vs historical volatility
            var older10 = closes.Skip(10).Take(10).ToList();
            var olderStdDev = older10.Count > 0 ?
                Math.Sqrt(older10.Select(x => Math.Pow(x - older10.Average(), 2)).Average()) : stdDev;

            var compressionRatio = olderStdDev > 0 ? stdDev / olderStdDev : 1.0;

            return new CompressionPattern
            {
                IsDetected = compressionRatio < 0.7, // 30% compression
                CompressionRatio = compressionRatio,
                CurrentVolatility = stdDev / sma * 100,
                HistoricalVolatility = olderStdDev / (older10.Any() ? older10.Average() : sma) * 100,
                CompressionStrength = Math.Max(0, (1 - compressionRatio) * 100)
            };
        }

        private VolumePattern AnalyzeVolumePattern(JObject historicalData)
        {
            var volumes = historicalData["v"]?.ToObject<List<long>>() ?? new List<long>();
            var closes = historicalData["c"]?.ToObject<List<double>>() ?? new List<double>();

            if (volumes.Count < 20)
                return new VolumePattern { IsValid = false };

            var recent5Vol = volumes.Take(5).ToList();
            var historical15Vol = volumes.Skip(5).Take(15).ToList();

            var avgRecentVol = recent5Vol.Average();
            var avgHistoricalVol = historical15Vol.Average();

            var volumeIncrease = avgHistoricalVol > 0 ? avgRecentVol / avgHistoricalVol : 1.0;

            // Detect accumulation pattern
            var recent5Closes = closes.Take(5).ToList();
            var priceStability = recent5Closes.Max() / recent5Closes.Min();

            return new VolumePattern
            {
                IsValid = true,
                VolumeIncreaseRatio = volumeIncrease,
                IsAccumulating = volumeIncrease > 1.2 && priceStability < 1.1, // Higher vol + stable price
                AverageVolume = avgHistoricalVol,
                CurrentVolumeStrength = Math.Min(10, volumeIncrease * 5),
                AccumulationScore = (volumeIncrease > 1.2 && priceStability < 1.1) ? 8 : 4
            };
        }

        private KeyLevels CalculateKeyLevels(JObject historicalData)
        {
            var highs = historicalData["h"]?.ToObject<List<double>>() ?? new List<double>();
            var lows = historicalData["l"]?.ToObject<List<double>>() ?? new List<double>();
            var closes = historicalData["c"]?.ToObject<List<double>>() ?? new List<double>();

            if (highs.Count < 30)
                return new KeyLevels();

            var recent30Highs = highs.Take(30).ToList();
            var recent30Lows = lows.Take(30).ToList();
            var currentPrice = closes.First();

            // Find significant resistance levels
            var resistanceLevels = recent30Highs
                .Where(h => h > currentPrice * 1.01) // Above current price
                .GroupBy(h => Math.Round(h, 1))
                .Where(g => g.Count() >= 2) // Touched at least twice
                .OrderBy(g => g.Key)
                .Select(g => g.Key)
                .Take(3)
                .ToList();

            // Find significant support levels  
            var supportLevels = recent30Lows
                .Where(l => l < currentPrice * 0.99) // Below current price
                .GroupBy(l => Math.Round(l, 1))
                .Where(g => g.Count() >= 2) // Touched at least twice
                .OrderByDescending(g => g.Key)
                .Select(g => g.Key)
                .Take(3)
                .ToList();

            return new KeyLevels
            {
                PrimaryResistance = resistanceLevels.FirstOrDefault(),
                SecondaryResistance = resistanceLevels.Skip(1).FirstOrDefault(),
                PrimarySupport = supportLevels.FirstOrDefault(),
                SecondarySupport = supportLevels.Skip(1).FirstOrDefault(),
                CurrentPrice = currentPrice,
                DistanceToResistance = resistanceLevels.Any() ?
                    ((resistanceLevels.First() - currentPrice) / currentPrice * 100) : 0
            };
        }

        private PositioningAnalysis AnalyzeCurrentPositioning(JObject currentQuote, KeyLevels keyLevels)
        {
            var currentPrice = currentQuote["c"]?.Value<double>() ?? 0;
            var volume = currentQuote["v"]?.Value<long>() ?? 0;
            var high = currentQuote["h"]?.Value<double>() ?? 0;
            var low = currentQuote["l"]?.Value<double>() ?? 0;

            var positionInRange = high > low ? ((currentPrice - low) / (high - low)) * 100 : 50;

            return new PositioningAnalysis
            {
                CurrentPrice = currentPrice,
                PositionInDayRange = positionInRange,
                DistanceToResistance = keyLevels.PrimaryResistance > 0 ?
                    ((keyLevels.PrimaryResistance - currentPrice) / currentPrice * 100) : 100,
                DistanceToSupport = keyLevels.PrimarySupport > 0 ?
                    ((currentPrice - keyLevels.PrimarySupport) / currentPrice * 100) : 100,
                VolumeStrength = volume > 1000000 ?
                    Math.Min(10, Math.Log10(volume) - 5) : 3,
                IsNearResistance = keyLevels.PrimaryResistance > 0 &&
                    Math.Abs(currentPrice - keyLevels.PrimaryResistance) / currentPrice < 0.03 // Within 3%
            };
        }

        private BreakoutSignal? GenerateBreakoutSignal(
            string symbol,
            ConsolidationPattern consolidation,
            CompressionPattern compression,
            VolumePattern volumePattern,
            KeyLevels keyLevels,
            PositioningAnalysis positioning)
        {
            var signal = new BreakoutSignal
            {
                Symbol = symbol,
                AnalyzedAt = DateTime.UtcNow,
                CurrentPrice = positioning.CurrentPrice
            };

            var scores = new List<double>();
            var reasons = new List<string>();

            // Score consolidation (0-25 points)
            if (consolidation.IsValid)
            {
                if (consolidation.IsCompressing && consolidation.VolatilityPercent < 10)
                {
                    scores.Add(25);
                    reasons.Add($"Tight consolidation ({consolidation.VolatilityPercent:F1}% range)");
                }
                else if (consolidation.IsCompressing)
                {
                    scores.Add(15);
                    reasons.Add("Price compression detected");
                }
                else
                {
                    scores.Add(5);
                    reasons.Add("Loose consolidation");
                }
            }

            // Score compression (0-25 points)
            if (compression.IsDetected)
            {
                var compressionScore = Math.Min(25, compression.CompressionStrength * 0.8);
                scores.Add(compressionScore);
                reasons.Add($"Volatility squeeze ({compression.CompressionStrength:F0}% compression)");
            }

            // Score volume pattern (0-25 points)
            if (volumePattern.IsValid)
            {
                if (volumePattern.IsAccumulating)
                {
                    scores.Add(25);
                    reasons.Add($"Accumulation detected ({volumePattern.VolumeIncreaseRatio:F1}x volume)");
                }
                else if (volumePattern.VolumeIncreaseRatio > 1.1)
                {
                    scores.Add(15);
                    reasons.Add("Volume increasing");
                }
                else
                {
                    scores.Add(5);
                    reasons.Add("Normal volume");
                }
            }

            // Score positioning (0-25 points)
            if (positioning.IsNearResistance && positioning.DistanceToResistance < 5)
            {
                scores.Add(25);
                reasons.Add($"Near key resistance ({positioning.DistanceToResistance:F1}% away)");
            }
            else if (positioning.DistanceToResistance < 10)
            {
                scores.Add(15);
                reasons.Add("Approaching resistance");
            }
            else
            {
                scores.Add(5);
                reasons.Add("Away from key levels");
            }

            signal.BreakoutScore = scores.Sum();
            signal.MaxPossibleScore = 100;
            signal.BreakoutProbability = signal.BreakoutScore;

            // Classification
            signal.BreakoutType = signal.BreakoutScore switch
            {
                >= 80 => BreakoutType.Imminent,
                >= 60 => BreakoutType.Probable,
                >= 40 => BreakoutType.Possible,
                _ => BreakoutType.Unlikely
            };

            signal.Reasons = reasons;
            signal.Consolidation = consolidation;
            signal.Compression = compression;
            signal.VolumePattern = volumePattern;
            signal.KeyLevels = keyLevels;
            signal.Positioning = positioning;

            // Only return signals with decent probability
            if (signal.BreakoutScore >= 40)
            {
                _logger.LogInformation($"Breakout signal generated for {symbol}: {signal.BreakoutType} ({signal.BreakoutScore}/100)");
                return signal;
            }

            return null;
        }

        private string ClassifyConsolidationType(List<double> highs, List<double> lows, List<double> closes)
        {
            var highsSlope = CalculateSlope(highs.Take(10).ToList());
            var lowsSlope = CalculateSlope(lows.Take(10).ToList());

            return (highsSlope, lowsSlope) switch
            {
                var (h, l) when Math.Abs(h) < 0.001 && Math.Abs(l) < 0.001 => "Rectangle",
                var (h, l) when h < 0 && l > 0 => "Triangle",
                var (h, l) when h > 0 && l > 0 => "Rising Wedge",
                var (h, l) when h < 0 && l < 0 => "Falling Wedge",
                _ => "Irregular"
            };
        }

        private double CalculateSlope(List<double> values)
        {
            if (values.Count < 2) return 0;

            var n = values.Count;
            var sumX = n * (n - 1) / 2.0;
            var sumY = values.Sum();
            var sumXY = values.Select((y, x) => x * y).Sum();
            var sumXX = n * (n - 1) * (2 * n - 1) / 6.0;

            return (n * sumXY - sumX * sumY) / (n * sumXX - sumX * sumX);
        }
    }

    // Supporting classes for breakout analysis
    public class BreakoutSignal
    {
        public string Symbol { get; set; }
        public DateTime AnalyzedAt { get; set; }
        public double CurrentPrice { get; set; }
        public double BreakoutScore { get; set; }
        public double MaxPossibleScore { get; set; }
        public double BreakoutProbability { get; set; }
        public BreakoutType BreakoutType { get; set; }
        public List<string> Reasons { get; set; } = new();

        public ConsolidationPattern Consolidation { get; set; }
        public CompressionPattern Compression { get; set; }
        public VolumePattern VolumePattern { get; set; }
        public KeyLevels KeyLevels { get; set; }
        public PositioningAnalysis Positioning { get; set; }
    }

    public class ConsolidationPattern
    {
        public bool IsValid { get; set; }
        public int DurationDays { get; set; }
        public double VolatilityPercent { get; set; }
        public double HighLevel { get; set; }
        public double LowLevel { get; set; }
        public bool IsCompressing { get; set; }
        public string ConsolidationType { get; set; }
    }

    public class CompressionPattern
    {
        public bool IsDetected { get; set; }
        public double CompressionRatio { get; set; }
        public double CurrentVolatility { get; set; }
        public double HistoricalVolatility { get; set; }
        public double CompressionStrength { get; set; }
    }

    public class VolumePattern
    {
        public bool IsValid { get; set; }
        public double VolumeIncreaseRatio { get; set; }
        public bool IsAccumulating { get; set; }
        public double AverageVolume { get; set; }
        public double CurrentVolumeStrength { get; set; }
        public double AccumulationScore { get; set; }
    }

    public class KeyLevels
    {
        public double PrimaryResistance { get; set; }
        public double SecondaryResistance { get; set; }
        public double PrimarySupport { get; set; }
        public double SecondarySupport { get; set; }
        public double CurrentPrice { get; set; }
        public double DistanceToResistance { get; set; }
    }

    public class PositioningAnalysis
    {
        public double CurrentPrice { get; set; }
        public double PositionInDayRange { get; set; }
        public double DistanceToResistance { get; set; }
        public double DistanceToSupport { get; set; }
        public double VolumeStrength { get; set; }
        public bool IsNearResistance { get; set; }
    }

    public enum BreakoutType
    {
        Unlikely,
        Possible,
        Probable,
        Imminent
    }
}