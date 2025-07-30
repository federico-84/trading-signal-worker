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
        public bool Sent { get; set; } = false;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? SentAt { get; set; }

        // Prevenzione spam
        public string SignalHash { get; set; }  // Hash per evitare duplicati
    }

    public enum SignalType
    {
        Buy,
        Sell,
        Hold,
        Warning
    }
}
