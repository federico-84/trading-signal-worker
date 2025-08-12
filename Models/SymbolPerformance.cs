namespace PortfolioSignalWorker.Models
{
    public class SymbolPerformance
    {
        public string Symbol { get; set; }
        public int SignalCount { get; set; }
        public double AverageMove { get; set; }
        public double SuccessRate { get; set; }
        public double BestMove { get; set; }
        public double WorstMove { get; set; }
        public DateTime LastSignal { get; set; }
    }
}
