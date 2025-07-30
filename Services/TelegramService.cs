namespace PortfolioSignalWorker.Services;

public class TelegramService
{
    private readonly HttpClient _http;
    private readonly string _botToken;
    private readonly string _chatId;

    public TelegramService(IConfiguration config)
    {
        _botToken = config["Telegram:BotToken"];
        _chatId = config["Telegram:ChatId"];
        _http = new HttpClient();
    }

    public async Task SendMessageAsync(string message)
    {
        var url = $"https://api.telegram.org/bot{_botToken}/sendMessage?chat_id={_chatId}&text={Uri.EscapeDataString(message)}";
        await _http.GetAsync(url);
    }
}