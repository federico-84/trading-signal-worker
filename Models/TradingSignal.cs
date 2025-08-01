using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace PortfolioSignalWorker.Models
{
    public class TradingSignal
    {
        [BsonId]
        public ObjectId Id { get; set; }

        public string Symbol { get; set; }
        public SignalType Type { get; set; }
        public double Confidence { get; set; }
        public string Reason { get; set; }
        public double RSI { get; set; }
        public double MACD_Histogram { get; set; }
        public double Price { get; set; }
        public long Volume { get; set; }                    // AGGIUNTO: Volume corrente
        public bool Sent { get; set; } = false;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? SentAt { get; set; }

        // Prevenzione spam
        public string SignalHash { get; set; }

        // ===== NUOVI CAMPI PER RISK MANAGEMENT =====

        // Stop Loss & Take Profit
        public double? StopLoss { get; set; }           // Prezzo di stop loss
        public double? TakeProfit { get; set; }         // Prezzo di take profit
        public double? StopLossPercent { get; set; }    // % di stop loss dal prezzo di entrata
        public double? TakeProfitPercent { get; set; }  // % di take profit dal prezzo di entrata

        // Risk/Reward Analysis
        public double? RiskRewardRatio { get; set; }    // Rapporto Risk/Reward (es. 1:3)
        public double? MaxRiskAmount { get; set; }      // Importo massimo a rischio
        public double? PotentialGainAmount { get; set; } // Guadagno potenziale

        // Position Sizing
        public int? SuggestedShares { get; set; }       // Numero azioni suggerite
        public double? PositionValue { get; set; }      // Valore posizione suggerita
        public double? MaxPositionSize { get; set; }    // % max del portafoglio per questa posizione

        // Technical Analysis Context
        public double? SupportLevel { get; set; }       // Livello di supporto tecnico
        public double? ResistanceLevel { get; set; }    // Livello di resistenza tecnico
        public double? ATR { get; set; }               // Average True Range per volatilità
        public string? EntryStrategy { get; set; }      // Strategia di entrata suggerita
        public string? ExitStrategy { get; set; }       // Strategia di uscita suggerita

        // Market Context
        public string? MarketCondition { get; set; }    // "Bullish", "Bearish", "Sideways"
        public double? VolumeStrength { get; set; }     // Forza del volume (1-10)
        public double? TrendStrength { get; set; }      // Forza del trend (1-10)
    }

    public enum SignalType
    {
        Buy,
        Sell,
        Hold,
        Warning
    }

    // ===== NUOVE CLASSI DI SUPPORTO =====

    public class RiskParameters
    {
        public double DefaultStopLossPercent { get; set; } = 5.0;    // 5% default stop loss
        public double DefaultTakeProfitPercent { get; set; } = 15.0; // 15% default take profit
        public double MaxPositionSizePercent { get; set; } = 5.0;    // 5% max del portafoglio
        public double PortfolioValue { get; set; } = 10000;          // Valore portafoglio base
        public double MinRiskRewardRatio { get; set; } = 2.0;        // Rapporto minimo R/R
        public bool UseATRForStopLoss { get; set; } = true;          // Usa ATR per stop loss dinamico
        public double ATRMultiplier { get; set; } = 2.0;             // Moltiplicatore ATR
    }

    public class LevelCalculationResult
    {
        public double StopLoss { get; set; }
        public double TakeProfit { get; set; }
        public double StopLossPercent { get; set; }
        public double TakeProfitPercent { get; set; }
        public double RiskRewardRatio { get; set; }
        public string CalculationMethod { get; set; }
        public double SupportLevel { get; set; }
        public double ResistanceLevel { get; set; }
        public string Reasoning { get; set; }
    }
}