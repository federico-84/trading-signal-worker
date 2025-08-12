namespace PortfolioSignalWorker.Models
{
    public class BreakoutValidationStats
    {
        public int TotalBreakoutSignals { get; set; }
        public int ValidBreakoutSignals { get; set; }
        public int InvalidBreakoutSignals { get; set; }
        public double AverageConfidence { get; set; }
        public Dictionary<string, int> BreakoutTypeDistribution { get; set; } = new();
        public double BreakoutValidationRate => TotalBreakoutSignals > 0 ?
            (double)ValidBreakoutSignals / TotalBreakoutSignals * 100 : 0;
    }
}
