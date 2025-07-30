using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace PortfolioSignalWorker.Models
{
    public class SymbolCandidate
    {
        [BsonId]
        public ObjectId Id { get; set; }

        public string Symbol { get; set; }
        public string CompanyName { get; set; }
        public string Sector { get; set; }
        public double Score { get; set; }
        public string ScoreReason { get; set; }
        public DateTime AnalyzedAt { get; set; } = DateTime.UtcNow;
        public bool AddedToWatchlist { get; set; } = false;
    }
}
