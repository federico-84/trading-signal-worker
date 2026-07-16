using MongoDB.Driver;
using PortfolioSignalWorker.Models;
using System.Text;

namespace PortfolioSignalWorker.Services
{
    public class SignalPerformanceReportService
    {
        private readonly IMongoCollection<TradingSignal> _signalCollection;
        private readonly YahooFinanceService _yahooFinance;
        private readonly TelegramService _telegram;
        private readonly ILogger<SignalPerformanceReportService> _logger;

        public SignalPerformanceReportService(
            IMongoDatabase database,
            YahooFinanceService yahooFinance,
            TelegramService telegram,
            ILogger<SignalPerformanceReportService> logger)
        {
            _signalCollection = database.GetCollection<TradingSignal>("TradingSignals");
            _yahooFinance = yahooFinance;
            _telegram = telegram;
            _logger = logger;
        }

        public async Task SendWeeklyReportAsync()
        {
            try
            {
                _logger.LogInformation("[REPORT] Generating weekly performance report...");

                var weekAgo = DateTime.UtcNow.AddDays(-7);
                var filter = Builders<TradingSignal>.Filter.And(
                    Builders<TradingSignal>.Filter.Gte(x => x.CreatedAt, weekAgo),
                    Builders<TradingSignal>.Filter.Eq(x => x.Sent, true),
                    Builders<TradingSignal>.Filter.Eq(x => x.Type, SignalType.Buy)
                );

                var signals = await _signalCollection.Find(filter)
                    .Sort(Builders<TradingSignal>.Sort.Ascending(x => x.CreatedAt))
                    .ToListAsync();

                if (!signals.Any())
                {
                    await _telegram.SendMessageAsync("📊 <b>Report Settimanale</b>\n\nNessun segnale BUY inviato negli ultimi 7 giorni.");
                    return;
                }

                var results = new List<SignalResult>();

                foreach (var signal in signals)
                {
                    var result = await EvaluateSignalAsync(signal);
                    if (result != null)
                        results.Add(result);
                }

                if (!results.Any())
                {
                    await _telegram.SendMessageAsync("📊 <b>Report Settimanale</b>\n\nDati storici non disponibili per valutare i segnali.");
                    return;
                }

                var message = BuildReportMessage(results, weekAgo);
                await _telegram.SendMessageAsync(message);

                _logger.LogInformation($"[REPORT] Weekly report sent: {results.Count} signals evaluated");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[REPORT] Error generating weekly report");
            }
        }

        private async Task<SignalResult?> EvaluateSignalAsync(TradingSignal signal)
        {
            try
            {
                // Fetch enough days to cover signal date + 5 trading days
                var daysSinceSignal = (int)(DateTime.UtcNow - signal.CreatedAt).TotalDays + 7;
                var data = await _yahooFinance.GetHistoricalDataAsync(signal.Symbol, Math.Max(daysSinceSignal, 15));

                var timestamps = data["t"]?.ToObject<List<long>>() ?? new List<long>();
                var closes = data["c"]?.ToObject<List<double>>() ?? new List<double>();

                if (timestamps.Count != closes.Count || closes.Count == 0)
                    return null;

                var signalTs = new DateTimeOffset(signal.CreatedAt).ToUnixTimeSeconds();

                // Find the first candle AFTER the signal date
                int entryIdx = -1;
                for (int i = 0; i < timestamps.Count; i++)
                {
                    if (timestamps[i] > signalTs)
                    {
                        entryIdx = i;
                        break;
                    }
                }

                if (entryIdx < 0)
                    return null;

                double? close1d = entryIdx < closes.Count ? closes[entryIdx] : null;
                double? close3d = entryIdx + 2 < closes.Count ? closes[entryIdx + 2] : null;
                double? close5d = entryIdx + 4 < closes.Count ? closes[entryIdx + 4] : null;

                double? bestClose = close5d ?? close3d ?? close1d;

                // Determine outcome
                SignalOutcome outcome;
                if (signal.StopLoss.HasValue && bestClose.HasValue && bestClose <= signal.StopLoss)
                    outcome = SignalOutcome.StopLossHit;
                else if (signal.TakeProfit.HasValue && bestClose.HasValue && bestClose >= signal.TakeProfit)
                    outcome = SignalOutcome.TakeProfitHit;
                else if (bestClose.HasValue && bestClose > signal.Price)
                    outcome = SignalOutcome.Positive;
                else if (bestClose.HasValue && bestClose < signal.Price * 0.97)
                    outcome = SignalOutcome.Negative;
                else
                    outcome = SignalOutcome.Neutral;

                return new SignalResult
                {
                    Symbol = signal.Symbol,
                    Isin = signal.Isin,
                    EntryPrice = signal.Price,
                    Close1d = close1d,
                    Close3d = close3d,
                    Close5d = close5d,
                    StopLoss = signal.StopLoss,
                    TakeProfit = signal.TakeProfit,
                    Confidence = signal.Confidence,
                    SignalDate = signal.CreatedAt,
                    Outcome = outcome
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"[REPORT] Could not evaluate {signal.Symbol}");
                return null;
            }
        }

        private string BuildReportMessage(List<SignalResult> results, DateTime weekStart)
        {
            var positive = results.Count(r => r.Outcome == SignalOutcome.Positive || r.Outcome == SignalOutcome.TakeProfitHit);
            var negative = results.Count(r => r.Outcome == SignalOutcome.Negative || r.Outcome == SignalOutcome.StopLossHit);
            var neutral = results.Count(r => r.Outcome == SignalOutcome.Neutral);
            var tpHit = results.Count(r => r.Outcome == SignalOutcome.TakeProfitHit);
            var slHit = results.Count(r => r.Outcome == SignalOutcome.StopLossHit);

            var withReturn = results.Where(r => r.BestReturn.HasValue).ToList();
            var avgReturn = withReturn.Any() ? withReturn.Average(r => r.BestReturn!.Value) : 0;

            var best = withReturn.OrderByDescending(r => r.BestReturn).FirstOrDefault();
            var worst = withReturn.OrderBy(r => r.BestReturn).FirstOrDefault();

            var winRate = results.Count > 0 ? (double)positive / results.Count * 100 : 0;

            var sb = new StringBuilder();
            sb.AppendLine($"📊 <b>REPORT SETTIMANALE</b>");
            sb.AppendLine($"<i>{weekStart:dd/MM} – {DateTime.Now:dd/MM/yyyy}</i>");
            sb.AppendLine();
            sb.AppendLine($"🎯 Segnali valutati: <b>{results.Count}</b>");
            sb.AppendLine($"✅ Positivi: <b>{positive}</b> | ❌ Negativi: <b>{negative}</b> | ⚪ Neutri: <b>{neutral}</b>");
            sb.AppendLine($"📈 Win rate: <b>{winRate:F0}%</b> | Rendimento medio: <b>{(avgReturn >= 0 ? "+" : "")}{avgReturn:F1}%</b>");

            if (tpHit > 0) sb.AppendLine($"🎯 Take Profit raggiunti: <b>{tpHit}</b>");
            if (slHit > 0) sb.AppendLine($"⛔ Stop Loss raggiunti: <b>{slHit}</b>");

            if (best != null)
                sb.AppendLine($"🏆 Migliore: <b>{best.Symbol} {(best.BestReturn >= 0 ? "+" : "")}{best.BestReturn:F1}%</b>");
            if (worst != null && worst.Symbol != best?.Symbol)
                sb.AppendLine($"💀 Peggiore: <b>{worst.Symbol} {(worst.BestReturn >= 0 ? "+" : "")}{worst.BestReturn:F1}%</b>");

            sb.AppendLine();
            sb.AppendLine("━━━━━━━━━━━━━━━━");

            foreach (var r in results.OrderByDescending(r => r.BestReturn))
            {
                var outcomeEmoji = r.Outcome switch
                {
                    SignalOutcome.TakeProfitHit => "🎯",
                    SignalOutcome.Positive => "✅",
                    SignalOutcome.Neutral => "⚪",
                    SignalOutcome.Negative => "❌",
                    SignalOutcome.StopLossHit => "⛔",
                    _ => "❓"
                };

                var returnStr = r.BestReturn.HasValue
                    ? $"{(r.BestReturn >= 0 ? "+" : "")}{r.BestReturn:F1}%"
                    : "n/d";

                var days = r.Close5d.HasValue ? "5gg" : r.Close3d.HasValue ? "3gg" : "1gg";

                sb.AppendLine($"{outcomeEmoji} <b>{r.Symbol}</b> €{r.EntryPrice:F2} → {returnStr} ({days}) [{r.SignalDate:dd/MM}]");
            }

            sb.AppendLine("━━━━━━━━━━━━━━━━");
            sb.AppendLine($"⏰ Generato il {DateTime.Now:dd/MM HH:mm}");

            return sb.ToString();
        }

        private enum SignalOutcome { Positive, Negative, Neutral, TakeProfitHit, StopLossHit }

        private class SignalResult
        {
            public string Symbol { get; set; }
            public string? Isin { get; set; }
            public double EntryPrice { get; set; }
            public double? Close1d { get; set; }
            public double? Close3d { get; set; }
            public double? Close5d { get; set; }
            public double? StopLoss { get; set; }
            public double? TakeProfit { get; set; }
            public double Confidence { get; set; }
            public DateTime SignalDate { get; set; }
            public SignalOutcome Outcome { get; set; }

            public double? BestReturn
            {
                get
                {
                    var best = Close5d ?? Close3d ?? Close1d;
                    if (!best.HasValue || EntryPrice == 0) return null;
                    return (best.Value - EntryPrice) / EntryPrice * 100;
                }
            }
        }
    }
}
