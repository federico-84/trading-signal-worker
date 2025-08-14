using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using static PortfolioSignalWorker.Models.TradingSignal;

namespace PortfolioSignalWorker.Models
{

    public class TakeProfitStrategyStatistics
    {
        public string StrategyName { get; set; }
        public int TotalTrades { get; set; }
        public int SuccessfulTrades { get; set; }
        public double SuccessRate => TotalTrades > 0 ? (double)SuccessfulTrades / TotalTrades * 100 : 0;

        public double AverageReturn { get; set; }
        public double AverageSuccessfulReturn { get; set; }
        public double AverageFailedReturn { get; set; }
        public double BestTrade { get; set; }
        public double WorstTrade { get; set; }

        public double AverageHoldingPeriod { get; set; }
        public double AveragePredictedProbability { get; set; }
        public double ActualVsPredictedAccuracy { get; set; }

        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

        public override string ToString()
        {
            return $"{StrategyName}: {SuccessRate:F1}% ({SuccessfulTrades}/{TotalTrades}) " +
                   $"Avg: {AverageReturn:F1}%, Best: {BestTrade:F1}%";
        }
    }

    public class ConfidenceRangePerformance
    {
        public double MinConfidence { get; set; }
        public double MaxConfidence { get; set; }
        public int SignalCount { get; set; }
        public double SuccessRate { get; set; }
        public double AverageReturn { get; set; }

        public string GetRangeDescription()
        {
            return $"{MinConfidence:F0}%-{MaxConfidence:F0}%";
        }
    }

    public class DataDrivenTakeProfitConfig
    {
        // Pesi per algoritmo di scoring
        public double ProbabilityWeight { get; set; } = 0.4;
        public double RiskRewardWeight { get; set; } = 0.3;
        public double ReasonablenessWeight { get; set; } = 0.2;
        public double ConfidenceWeight { get; set; } = 0.1;

        // Limiti operativi
        public double MinTakeProfitPercent { get; set; } = 3.0;
        public double MaxTakeProfitPercent { get; set; } = 30.0;
        public double MinRiskRewardRatio { get; set; } = 1.0;

        // Parametri per analisi storica
        public int HistoricalDataDays { get; set; } = 60;
        public int MinDataPointsRequired { get; set; } = 20;
        public int RecentIndicatorsCount { get; set; } = 30;

        // Performance tracking
        public bool EnablePerformanceTracking { get; set; } = true;
        public int PerformanceTrackingDays { get; set; } = 30;
    }
    // ===== RISULTATO PRINCIPALE =====
    public class DataDrivenTakeProfitResult
    {
        public string Symbol { get; set; }
        public double CurrentPrice { get; set; }
        public TakeProfitOption OptimalTakeProfit { get; set; }
        public List<TakeProfitOption> AlternativeTargets { get; set; } = new();

        // Analisi di supporto
        public HistoricalMovementAnalysis HistoricalAnalysis { get; set; }
        public VolatilityAnalysis VolatilityAnalysis { get; set; }
        public TechnicalLevelsAnalysis TechnicalLevels { get; set; }
        public SignalPerformanceAnalysis SignalPerformanceAnalysis { get; set; }

        public bool IsFallback { get; set; } = false;
        public DateTime CalculatedAt { get; set; } = DateTime.UtcNow;

        public string GetSummary()
        {
            return $"Optimal TP: {OptimalTakeProfit.Strategy} - " +
                   $"{OptimalTakeProfit.Percentage:F1}% ({OptimalTakeProfit.ProbabilityOfSuccess:F0}% probability)";
        }
    }

    // ===== OPZIONI DI TAKE PROFIT =====
    public class TakeProfitOption
    {
        public double Price { get; set; }
        public double Percentage { get; set; }
        public double RiskRewardRatio { get; set; }
        public string Strategy { get; set; }
        public double ProbabilityOfSuccess { get; set; }
        public string DataSource { get; set; }
        public string Reasoning { get; set; }

        public override string ToString()
        {
            return $"{Strategy}: ${Price:F2} ({Percentage:F1}%) - R/R: 1:{RiskRewardRatio:F1} - {ProbabilityOfSuccess:F0}%";
        }
    }

    // ===== ANALISI MOVIMENTI STORICI =====
    public class HistoricalMovementAnalysis
    {
        public bool IsDataSufficient { get; set; }
        public double AverageDailyMove { get; set; }
        public double MedianDailyMove { get; set; }
        public double Percentile75Move { get; set; }
        public double Percentile90Move { get; set; }
        public double AverageUpMove { get; set; }
        public double AverageDownMove { get; set; }
        public double AverageIntradayRange { get; set; }
        public double MaxIntradayRange { get; set; }
        public double TypicalMultiDayMove { get; set; }
        public double LargeMultiDayMove { get; set; }
        public bool BullishBias { get; set; }
        public int ConsecutiveUpDays { get; set; }
        public int DataPoints { get; set; }

        public string GetSummary()
        {
            return $"Avg Daily: {AverageDailyMove:F1}%, " +
                   $"P75: {Percentile75Move:F1}%, " +
                   $"Multi-day: {TypicalMultiDayMove:F1}%, " +
                   $"Bias: {(BullishBias ? "Bullish" : "Bearish")}";
        }
    }

    // ===== ANALISI VOLATILITÀ =====
    public class VolatilityAnalysis
    {
        public bool IsDataSufficient { get; set; }
        public double AveragePriceVolatility { get; set; }
        public double RSIVolatility { get; set; }
        public VolatilityRegime VolatilityRegime { get; set; }
        public (double min, double max) RecommendedTakeProfitRange { get; set; }
        public bool IsHighVolatilityPeriod { get; set; }
        public int DataPoints { get; set; }

        public string GetSummary()
        {
            return $"Regime: {VolatilityRegime}, " +
                   $"Price Vol: {AveragePriceVolatility:F1}%, " +
                   $"TP Range: {RecommendedTakeProfitRange.min:F1}%-{RecommendedTakeProfitRange.max:F1}%";
        }
    }

    public enum VolatilityRegime
    {
        Low,
        Normal,
        High,
        Extreme
    }

    // ===== ANALISI LIVELLI TECNICI =====
    public class TechnicalLevelsAnalysis
    {
        public bool IsDataSufficient { get; set; }
        public ResistanceLevel PrimaryResistance { get; set; }
        public ResistanceLevel SecondaryResistance { get; set; }
        public List<ResistanceLevel> ResistanceLevels { get; set; } = new();
        public List<SupportLevel> SupportLevels { get; set; } = new();
        public double VolumeWeightedResistance { get; set; }
        public double PivotResistance { get; set; }
        public double StrengthScore { get; set; } // 0-1
        public double RecommendedTarget { get; set; }

        public string GetSummary()
        {
            var primaryStr = PrimaryResistance != null ? $"${PrimaryResistance.Level:F2}" : "N/A";
            return $"Primary R: {primaryStr}, " +
                   $"Strength: {StrengthScore:F1}, " +
                   $"Levels: {ResistanceLevels.Count}";
        }
    }

    public class ResistanceLevel
    {
        public double Level { get; set; }
        public int TouchCount { get; set; } // Quante volte è stata testata
        public double Strength { get; set; } // 0-1
        public DateTime LastTouch { get; set; }
        public string Type { get; set; } // "Historical High", "Volume Profile", "Pivot"

        public override string ToString()
        {
            return $"{Type}: ${Level:F2} (Strength: {Strength:F1}, Touches: {TouchCount})";
        }
    }

    public class SupportLevel
    {
        public double Level { get; set; }
        public int TouchCount { get; set; }
        public double Strength { get; set; } // 0-1
        public DateTime LastTouch { get; set; }
        public string Type { get; set; }

        public override string ToString()
        {
            return $"{Type}: ${Level:F2} (Strength: {Strength:F1}, Touches: {TouchCount})";
        }
    }

    // ===== ANALISI PERFORMANCE SEGNALI =====
    public class SignalPerformanceAnalysis
    {
        public bool IsDataSufficient { get; set; }
        public string Symbol { get; set; }
        public SignalType SignalType { get; set; }
        public double ConfidenceRange { get; set; } // Range di confidence analizzato

        // Statistiche performance
        public int TotalSignals { get; set; }
        public int SuccessfulSignals { get; set; }
        public double SuccessRate { get; set; }
        public double AverageReturn { get; set; }
        public double AverageSuccessfulReturn { get; set; }
        public double AverageFailedReturn { get; set; }
        public double BestReturn { get; set; }
        public double WorstReturn { get; set; }

        // Timing statistics
        public double AverageHoldingPeriod { get; set; } // In giorni
        public double MedianHoldingPeriod { get; set; }

        // Performance per confidence range
        public List<ConfidenceRangePerformance> PerformanceByConfidence { get; set; } = new();

        public string GetSummary()
        {
            return $"Signals: {TotalSignals}, " +
                   $"Success Rate: {SuccessRate:F1}%, " +
                   $"Avg Return: {AverageReturn:F1}%, " +
                   $"Best: {BestReturn:F1}%";
        }
    }

    

    // ===== STORAGE PER PERFORMANCE TRACKING =====
    [BsonCollection("TakeProfitPerformance")]
    public class TakeProfitPerformanceRecord
    {
        [BsonId]
        public ObjectId Id { get; set; }

        public string Symbol { get; set; }
        public SignalType OriginalSignalType { get; set; }
        public double OriginalConfidence { get; set; }
        public double EntryPrice { get; set; }
        public double StopLoss { get; set; }

        // Take Profit Strategy utilizzata
        public string TakeProfitStrategy { get; set; }
        public double TakeProfitPrice { get; set; }
        public double TakeProfitPercentage { get; set; }
        public double PredictedProbability { get; set; }

        // Risultato effettivo - USA L'ENUM CORRETTO
        public bool IsCompleted { get; set; } = false;
        public SimplifiedTakeProfitResult? ActualResult { get; set; }  // 🔄 CORRETTO
        public double? ActualReturn { get; set; }
        public double? MaxDrawdown { get; set; }
        public int? HoldingPeriodDays { get; set; }
        public DateTime? CompletedAt { get; set; }

        // Metadati
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // Analisi post-mortem
        public string Notes { get; set; }
        public double StrategyAccuracy => ActualResult switch
        {
            SimplifiedTakeProfitResult.Hit => 1.0,
            SimplifiedTakeProfitResult.StoppedOut => 0.0,
            SimplifiedTakeProfitResult.PartialHit => 0.5,
            _ => 0.0
        };
    }

    public enum TakeProfitResult
    {
        Hit,           // Take profit raggiunto
        StoppedOut,    // Stop loss attivato
        PartialHit,    // Parzialmente raggiunto (per strategie multi-level)
        Expired,       // Signal scaduto senza azione
        ManualExit     // Uscita manuale
    }

  

    // ===== INTERFACCE =====
    public interface ITakeProfitStrategy
    {
        Task<TakeProfitOption> CalculateTakeProfit(
            string symbol,
            double currentPrice,
            double stopLoss,
            SignalType signalType,
            double confidence);

        string StrategyName { get; }
        string Description { get; }
    }

    // ===== ATTRIBUTO PER MONGODB =====
    public class BsonCollectionAttribute : Attribute
    {
        public string CollectionName { get; }

        public BsonCollectionAttribute(string collectionName)
        {
            CollectionName = collectionName;
        }
    }

}