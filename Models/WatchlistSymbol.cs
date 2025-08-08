using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace PortfolioSignalWorker.Models
{
    public class WatchlistSymbol
    {
        [BsonId]
        public ObjectId Id { get; set; }
        public string Market { get; set; } = "US";  // NEW: "US" or "EU"
        public string Symbol { get; set; }
        public string CompanyName { get; set; }
        public string Sector { get; set; }
        public string Industry { get; set; }
        public string Exchange { get; set; }

        // Tier Management
        public SymbolTier Tier { get; set; }
        public TimeSpan MonitoringFrequency { get; set; }
        public DateTime LastAnalyzed { get; set; }
        public DateTime NextAnalysis { get; set; }

        // Selection Criteria Scores
        public double LiquidityScore { get; set; }      // Based on avg daily volume
        public double VolatilityScore { get; set; }     // Price volatility (sweet spot)
        public double TrendScore { get; set; }          // Recent price trend
        public double SignalQualityScore { get; set; }  // Historical signal accuracy
        public double OverallScore { get; set; }        // Weighted composite score

        // Performance Tracking  
        public int SignalsGenerated { get; set; } = 0;
        public int SuccessfulSignals { get; set; } = 0;
        public double SuccessRate { get; set; } = 0.0;
        public double AvgReturn { get; set; } = 0.0;

        // Market Data Cache
        public double CurrentPrice { get; set; }
        public long AverageDailyVolume { get; set; }
        public double MarketCap { get; set; }
        public double Beta { get; set; }

        public bool IsCore { get; set; } = false;           // Core symbols never rotate
        public bool CanRotate { get; set; } = true;        // Rotation eligibility
        public int DaysInWatchlist { get; set; } = 0;      // Track tenure
        public int MinHistoryDays { get; set; } = 14;      // Min days before rotation eligible
        public bool IsActive { get; set; } = true;         // Symbol is actively monitored
        public DateTime AddedDate { get; set; } = DateTime.UtcNow;
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
        public string Notes { get; set; } = "";

        // 🚀 NUOVI CAMPI per volatilità dinamica
        public VolatilityLevel VolatilityLevel { get; set; } = VolatilityLevel.Standard;
        public double AverageVolatilityPercent { get; set; } = 0.0; // Media volatilità giornaliera
        public DateTime LastVolatilityUpdate { get; set; } = DateTime.UtcNow;
        public int ConsecutiveHighVolDays { get; set; } = 0; // Giorni consecutivi alta volatilità
        public bool IsBreakoutCandidate { get; set; } = false; // Flag per candidati breakout
    }
    public enum VolatilityLevel
    {
        Low = 1,        // <2% movimento giornaliero medio (KO, PG, WMT)
        Standard = 2,   // 2-5% movimento giornaliero (AAPL, MSFT)
        High = 3,       // 5-10% movimento giornaliero (TSLA, NVDA)  
        Explosive = 4   // >10% movimento giornaliero (SHOP, COIN, meme stocks)
    }
    public enum SymbolTier
    {
        Tier1_Priority = 1,    // Every 30 min - Top performers
        Tier2_Standard = 2,    // Every 2 hours - Good candidates  
        Tier3_Monitor = 3      // Every 4 hours - Potential candidates
    }
}
