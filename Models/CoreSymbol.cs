using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace PortfolioSignalWorker.Models
{
    public class CoreSymbol
    {
        [BsonId]
        public ObjectId Id { get; set; }

        public string Symbol { get; set; }
        public string Market { get; set; } // "US" or "EU"
        public int Priority { get; set; } = 1; // 1=highest priority
        public bool IsActive { get; set; } = true;
        public string Notes { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}