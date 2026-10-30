using Microsoft.EntityFrameworkCore;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;

namespace MuzLoto.Web.Services.Messaging;

/// <summary>
/// Delivers queued messages one by one with a pause between them (messengers block accounts that send in bursts).
/// A messenger that is not connected keeps its messages waiting; errors are retried with growing delays.
/// </summary>
public sealed class MessageDispatcher(IServiceScopeFactory scopes, IConfiguration config, ILogger<MessageDispatcher> logger) : BackgroundService
{
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2)];
    private const int MaxAttempts = 5;
    /// <summary>A message waiting for a messenger that is never connected gives up after this long.</summary>
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromDays(3);

    private TimeSpan Pause => TimeSpan.FromSeconds(config.GetValue("Messaging:PauseSeconds", 8));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Messaging:Enabled", true)) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Message dispatch failed"); }
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        }
    }

    /// <summary>One pass over due messages; public for tests.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var channels = scope.ServiceProvider.GetServices<IMessengerChannel>().ToDictionary(x => x.Channel);
        var started = DateTimeOffset.UtcNow;
        var ready = new Dictionary<MessageChannel, bool>();
        var sent = 0;
        // Batches until nothing is due: every handled message is either finished or rescheduled into the future.
        while (await db.OutgoingMessages.Include(x => x.Booking)
                   .Where(x => x.Status == OutgoingMessageStatus.Pending && x.NextAttemptAt <= started)
                   .OrderBy(x => x.CreatedAt).Take(20).ToListAsync(ct) is { Count: > 0 } due)
        foreach (var message in due)
        {
            var now = DateTimeOffset.UtcNow;
            if (!channels.TryGetValue(message.Channel, out var channel))
            {
                message.NextAttemptAt = now.AddMinutes(5);   // no such messenger in this build: keep it out of the loop
                await db.SaveChangesAsync(ct);
                continue;
            }
            if (!ready.TryGetValue(message.Channel, out var isReady)) ready[message.Channel] = isReady = await channel.IsReadyAsync(ct);
            if (!isReady)
            {
                // Not an attempt: the message simply waits until the admin connects the messenger.
                if (now - message.CreatedAt > GiveUpAfter)
                {
                    message.Status = OutgoingMessageStatus.Failed;
                    message.LastError = "Мессенджер так и не был подключён — сообщение не отправлено.";
                }
                else
                {
                    message.NextAttemptAt = now.AddMinutes(5);
                    message.LastError = "Ждёт подключения мессенджера.";
                }
                await db.SaveChangesAsync(ct);
                continue;
            }
            if (sent > 0) await Task.Delay(Pause, ct);
            SendOutcome outcome;
            try { outcome = await channel.SendAsync(message.Booking, message.Text, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Sending {Channel} message {Id} failed", message.Channel, message.Id);
                outcome = SendOutcome.Failed("Ошибка отправки, повторим позже.");
            }
            Apply(message, outcome, DateTimeOffset.UtcNow);
            if (outcome.Status == OutgoingMessageStatus.Sent) sent++;
            await db.SaveChangesAsync(ct);
        }
        return sent;
    }

    public static void Apply(OutgoingMessage message, SendOutcome outcome, DateTimeOffset now)
    {
        message.Attempts++;
        message.LastError = outcome.Error;
        switch (outcome.Status)
        {
            case OutgoingMessageStatus.Sent:
                message.Status = OutgoingMessageStatus.Sent;
                message.SentAt = now;
                break;
            case OutgoingMessageStatus.Skipped:
                message.Status = OutgoingMessageStatus.Skipped;
                break;
            default:
                if (message.Attempts >= MaxAttempts) { message.Status = OutgoingMessageStatus.Failed; break; }
                message.Status = OutgoingMessageStatus.Pending;   // try again later
                message.NextAttemptAt = now + (outcome.RetryAfter ?? Backoff[Math.Min(message.Attempts - 1, Backoff.Length - 1)]);
                break;
        }
    }
}
