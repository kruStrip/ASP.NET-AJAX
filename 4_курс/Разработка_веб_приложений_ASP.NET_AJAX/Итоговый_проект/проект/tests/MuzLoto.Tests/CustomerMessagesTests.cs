using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;
using MuzLoto.Web.Services;
using MuzLoto.Web.Services.Messaging;
using Xunit;

namespace MuzLoto.Tests;

/// <summary>«Ваша игра подтверждена» in Telegram and WhatsApp: queued with the payment, delivered by the dispatcher.</summary>
[Trait("Category", "Integration")]
[Collection(AppCollection.Name)]
public sealed class CustomerMessagesTests(AppFactory app)
{
    private sealed class FakeChannel(MessageChannel channel, bool ready, Func<Booking, SendOutcome> send) : IMessengerChannel
    {
        public List<(Booking Booking, string Text)> Sent { get; } = new();
        public MessageChannel Channel => channel;
        public Task<bool> IsReadyAsync(CancellationToken ct) => Task.FromResult(ready);
        public Task<SendOutcome> SendAsync(Booking booking, string text, CancellationToken ct)
        {
            Sent.Add((booking, text));
            return Task.FromResult(send(booking));
        }
    }

    private async Task<Booking> ConfirmedBookingAsync(string? telegram = "@team_msg")
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = new GameSession
        {
            Game = new Game { Title = "Квиз Сумерки " + Guid.NewGuid().ToString("N")[..4] },
            StartsAt = new DateTimeOffset(2031, 11, 5, 10, 0, 0, TimeSpan.Zero), TimeZoneId = ScheduleTime.DefaultTimeZone,
            Venue = "Киса Бар, Светланская 21", DurationMinutes = 120, AgeLimit = 18, TicketPrice = 700m, Capacity = 40
        };
        var booking = new Booking
        {
            GameSession = session, TeamName = "МЯУ " + Guid.NewGuid().ToString("N")[..4], CustomerName = "Вика",
            Phone = "+79140000000", Telegram = telegram, Quantity = 3, TicketPriceSnapshot = 700m
        };
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        var admin = await app.CreateUserAsync("Admin");
        await scope.ServiceProvider.GetRequiredService<BookingWorkflow>().ConfirmAsync(booking.Id, admin.Id);
        return booking;
    }

    private async Task<List<OutgoingMessage>> MessagesAsync(Guid bookingId)
    {
        using var scope = app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().OutgoingMessages.AsNoTracking()
            .Where(x => x.BookingId == bookingId).OrderBy(x => x.Channel).ToListAsync();
    }

    /// <summary>A dispatcher over the real database with fake messengers instead of Telegram and WhatsApp.</summary>
    private MessageDispatcher Dispatcher(params IMessengerChannel[] channels)
    {
        var services = new ServiceCollection();
        var connection = app.Services.GetRequiredService<IConfiguration>().GetConnectionString("Default");
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connection));
        foreach (var channel in channels) services.AddSingleton(channel);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Messaging:PauseSeconds"] = "0" }).Build();
        return new MessageDispatcher(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), config, NullLogger<MessageDispatcher>.Instance);
    }

    [Fact]
    public async Task ConfirmingPayment_QueuesOneMessagePerMessenger_WithTheAgreedText()
    {
        var booking = await ConfirmedBookingAsync();
        var messages = await MessagesAsync(booking.Id);
        Assert.Equal([MessageChannel.Telegram, MessageChannel.WhatsApp], messages.Select(x => x.Channel));
        Assert.All(messages, m => Assert.Equal(OutgoingMessageStatus.Pending, m.Status));
        var text = messages[0].Text;
        Assert.StartsWith("Здравствуйте, ваша игра подтверждена!", text);
        foreach (var line in new[] { "Квиз Сумерки", "Дата: 05.11.2031", "Время: 20:00", "Локация: Киса Бар, Светланская 21",
                     "Продолжительность: 2 часа", "Возрастное ограничение: 18+", "Команда: " + booking.TeamName, "Кол-во билетов: 3",
                     "Итого: 2", "Ваш личный кабинет: https://", "/bookings/mine" })
            Assert.Contains(line, text);

        using (var scope = app.Services.CreateScope())   // a repeated confirmation does not queue the messages twice
            await scope.ServiceProvider.GetRequiredService<BookingWorkflow>().ConfirmAsync(booking.Id, (await app.CreateUserAsync("Admin")).Id);
        Assert.Equal(2, (await MessagesAsync(booking.Id)).Count);
    }

    [Fact]
    public async Task Dispatcher_SendsSkipsRetriesAndWaitsForAnUnconnectedMessenger()
    {
        var booking = await ConfirmedBookingAsync();
        var telegram = new FakeChannel(MessageChannel.Telegram, ready: true, b => b.Id == booking.Id ? SendOutcome.Sent : SendOutcome.Skipped("other"));
        var whatsApp = new FakeChannel(MessageChannel.WhatsApp, ready: false, _ => SendOutcome.Sent);
        await Dispatcher(telegram, whatsApp).RunOnceAsync(CancellationToken.None);

        var messages = await MessagesAsync(booking.Id);
        Assert.Equal(OutgoingMessageStatus.Sent, messages[0].Status);                 // Telegram delivered
        Assert.NotNull(messages[0].SentAt);
        Assert.Contains(telegram.Sent, x => x.Booking.Id == booking.Id && x.Text.Contains(booking.TeamName));
        Assert.Equal(OutgoingMessageStatus.Pending, messages[1].Status);              // WhatsApp not linked yet: waits
        Assert.Equal("Ждёт подключения мессенджера.", messages[1].LastError);
        Assert.Equal(0, messages[1].Attempts);
        Assert.Empty(whatsApp.Sent);

        // Sent messages are never sent again.
        telegram.Sent.Clear();
        await Dispatcher(telegram, whatsApp).RunOnceAsync(CancellationToken.None);
        Assert.DoesNotContain(telegram.Sent, x => x.Booking.Id == booking.Id);
    }

    [Fact]
    public void FailedSend_IsRetriedLater_AndGivesUpAfterFiveAttempts_SkippedIsFinal()
    {
        var now = DateTimeOffset.UtcNow;
        var message = new OutgoingMessage { Text = "t" };
        MessageDispatcher.Apply(message, SendOutcome.Failed("сеть"), now);
        Assert.Equal((OutgoingMessageStatus.Pending, 1, "сеть"), (message.Status, message.Attempts, message.LastError));
        Assert.True(message.NextAttemptAt > now);
        MessageDispatcher.Apply(message, SendOutcome.Failed("flood", TimeSpan.FromHours(1)), now);
        Assert.Equal(now.AddHours(1), message.NextAttemptAt);                         // the messenger's own delay wins
        for (var i = 0; i < 3; i++) MessageDispatcher.Apply(message, SendOutcome.Failed("сеть"), now);
        Assert.Equal(OutgoingMessageStatus.Failed, message.Status);

        var noTelegram = new OutgoingMessage { Text = "t" };
        MessageDispatcher.Apply(noTelegram, SendOutcome.Skipped("У номера нет WhatsApp."), now);
        Assert.Equal(OutgoingMessageStatus.Skipped, noTelegram.Status);
    }

    [Fact]
    public async Task MessengersPage_IsForAdminsOnly_AndExplainsWhatIsMissing()
    {
        var manager = await app.SignedInAsync(await app.CreateUserAsync("Manager"));
        Assert.Equal(System.Net.HttpStatusCode.Redirect, (await manager.GetAsync("/messengers")).StatusCode);
        var admin = await app.SignedInAsync(await app.CreateUserAsync("Admin"));
        var html = System.Net.WebUtility.HtmlDecode(await admin.GetStringAsync("/messengers"));
        Assert.Contains("Telegram — личный аккаунт", html);
        Assert.Contains("TELEGRAM_API_ID", html);                 // not configured in tests: the page says what to add
        Assert.Contains("не настроен на сервере", html);          // WhatsApp gateway
    }
}
