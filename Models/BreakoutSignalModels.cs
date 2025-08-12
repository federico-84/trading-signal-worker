// 🚀 NUOVO FILE: Models/BreakoutSignalModels.cs

using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace PortfolioSignalWorker.Models
{
    public class BreakoutSignalDocument
    {
        [BsonId]
        public ObjectId Id { get; set; }

        public string Symbol { get; set; }
        public DateTime AnalyzedAt { get; set; }
        public double CurrentPrice { get; set; }
        public double BreakoutScore { get; set; }
        public double MaxPossibleScore { get; set; }
        public double BreakoutProbability { get; set; }
        public string BreakoutType { get; set; } // Imminent, Probable, Possible, Unlikely
        public List<string> Reasons { get; set; } = new();

        // Pattern Analysis Results
        public ConsolidationPatternDocument Consolidation { get; set; }
        public CompressionPatternDocument Compression { get; set; }
        public VolumePatternDocument VolumePattern { get; set; }
        public KeyLevelsDocument KeyLevels { get; set; }
        public PositioningAnalysisDocument Positioning { get; set; }

        // Signal Tracking
        public bool SignalSent { get; set; } = false;
        public DateTime? SignalSentAt { get; set; }
        public ObjectId? TradingSignalId { get; set; } // Link al TradingSignal generato

        // Performance Tracking
        public double? ActualMove24h { get; set; }
        public double? ActualMove7d { get; set; }
        public bool? BreakoutConfirmed { get; set; }
        public DateTime? BreakoutConfirmedAt { get; set; }
        public string Notes { get; set; } = "";
    }

    public class ConsolidationPatternDocument
    {
        public bool IsValid { get; set; }
        public int DurationDays { get; set; }
        public double VolatilityPercent { get; set; }
        public double HighLevel { get; set; }
        public double LowLevel { get; set; }
        public bool IsCompressing { get; set; }
        public string ConsolidationType { get; set; } // Rectangle, Triangle, Wedge, etc.
    }

    public class CompressionPatternDocument
    {
        public bool IsDetected { get; set; }
        public double CompressionRatio { get; set; }
        public double CurrentVolatility { get; set; }
        public double HistoricalVolatility { get; set; }
        public double CompressionStrength { get; set; }
    }

    public class VolumePatternDocument
    {
        public bool IsValid { get; set; }
        public double VolumeIncreaseRatio { get; set; }
        public bool IsAccumulating { get; set; }
        public double AverageVolume { get; set; }
        public double CurrentVolumeStrength { get; set; }
        public double AccumulationScore { get; set; }
    }

    public class KeyLevelsDocument
    {
        public double PrimaryResistance { get; set; }
        public double SecondaryResistance { get; set; }
        public double PrimarySupport { get; set; }
        public double SecondarySupport { get; set; }
        public double CurrentPrice { get; set; }
        public double DistanceToResistance { get; set; }
    }

    public class PositioningAnalysisDocument
    {
        public double CurrentPrice { get; set; }
        public double PositionInDayRange { get; set; }
        public double DistanceToResistance { get; set; }
        public double DistanceToSupport { get; set; }
        public double VolumeStrength { get; set; }
        public bool IsNearResistance { get; set; }
    } 
    

    // 🚀 NUOVO: Settings per breakout detection (da appsettings.json)
    public class BreakoutDetectionSettings
    {
        public bool EnableBreakoutSignals { get; set; } = true;
        public int MinBreakoutScoreFullAnalysis { get; set; } = 60;
        public int MinBreakoutScorePreMarket { get; set; } = 70;
        public int MinBreakoutScoreOffHours { get; set; } = 80;
        public int ConsolidationMinDays { get; set; } = 5;
        public double ConsolidationMaxVolatility { get; set; } = 15.0;
        public double CompressionMinRatio { get; set; } = 0.7;
        public double VolumeAccumulationMinRatio { get; set; } = 1.2;
        public double ResistanceProximityPercent { get; set; } = 5.0;
        public bool EnableConsolidationDetection { get; set; } = true;
        public bool EnableCompressionDetection { get; set; } = true;
        public bool EnableVolumeAccumulation { get; set; } = true;
        public bool EnableKeyLevelsAnalysis { get; set; } = true;
        public int MaxHistoricalDaysForAnalysis { get; set; } = 50;
        public int MinDataPointsRequired { get; set; } = 20;
    }
} 