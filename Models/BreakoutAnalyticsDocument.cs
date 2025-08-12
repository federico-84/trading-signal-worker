using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace PortfolioSignalWorker.Models
{
    public class BreakoutAnalyticsDocument
    {
        [BsonId]
        public ObjectId Id { get; set; }

        public DateTime AnalyzedFrom { get; set; }
        public DateTime AnalyzedTo { get; set; }
        public int TotalSignals { get; set; }
        public int SignalsSent { get; set; }
        public Dictionary<string, int> TypeDistribution { get; set; } = new();
        public double AverageScore { get; set; }
        public int ConfirmedBreakouts { get; set; }
        public List<SymbolPerformance> TopPerformingSymbols { get; set; } = new();
        public double SuccessRate => TotalSignals > 0 ? (double)ConfirmedBreakouts / TotalSignals * 100 : 0;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
