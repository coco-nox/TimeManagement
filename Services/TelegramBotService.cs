namespace TimeManagement.Services;

/// <summary>
/// Long-polls Telegram for the bot's updates (button taps, /start links,
/// replies) for as long as FlightPath runs, handing each to
/// <see cref="TelegramUpdateHandler"/>. Polling - FlightPath asking Telegram,
/// never the other way round - is what makes this work on a computer the
/// phone can't reach. Telegram keeps updates for 24 hours, so taps made
/// while FlightPath was off are picked up when it next starts (and the
/// handler refuses any for sessions that have since ended).
///
/// Does nothing until a bot token is set in User Secrets (Telegram:BotToken);
/// it checks again every minute, so adding one doesn't need a restart.
/// </summary>
public sealed class TelegramBotService(
    IServiceScopeFactory scopeFactory,
    ILogger<TelegramBotService> logger) : BackgroundService
{
    private const int LongPollSeconds = 50;

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ILogger<TelegramBotService> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        long offset = 0;
        var warnedMissingToken = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var telegram = scope.ServiceProvider.GetRequiredService<ITelegramClient>();

                if (!telegram.IsConfigured)
                {
                    if (!warnedMissingToken)
                    {
                        _logger.LogInformation("Telegram reminders are off - no Telegram:BotToken in User Secrets.");
                        warnedMissingToken = true;
                    }
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                    continue;
                }
                warnedMissingToken = false;

                var updates = await telegram.GetUpdatesAsync(offset, LongPollSeconds, stoppingToken);
                if (updates == null)
                {
                    // Bad token, network down, Telegram hiccup - back off a little.
                    await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
                    continue;
                }

                foreach (var update in updates)
                {
                    // Advance first, so one update that keeps failing can't
                    // block every update after it.
                    offset = Math.Max(offset, update.GetProperty("update_id").GetInt64() + 1);

                    using var updateScope = _scopeFactory.CreateScope();
                    var handler = updateScope.ServiceProvider.GetRequiredService<TelegramUpdateHandler>();
                    try
                    {
                        await handler.HandleAsync(update, DateTime.Now, stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, "Failed to handle a Telegram update; skipping it.");
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Telegram polling failed; retrying shortly.");
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
        }
    }
}
