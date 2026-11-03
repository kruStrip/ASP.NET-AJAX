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

/// <summary>«Ваша игра подтверждена» in Telegram (and only there, decision of 2026-11-03): queued with the payment,
/// delivered by the dispatcher.</summary>
[Trait("Category", "Integration")]
[Collection(AppCollection.Name)]
public sealed class CustomerMessagesTests(AppFactory app)
{
    private sealed class FakeChannel(MessageChannel channel, bool ready, Func<Booking, SendOutcome> send) : IMessengerChannel
    {
        public List<OutgoingMessage> Sent { get; } = new();
        public MessageChannel Channel => channel;
        public Task<bool> IsReadyAsync(CancellationToken ct) => Task.FromResult(ready);
        public Task<SendOutcome> SendAsync(OutgoingMessage message, CancellationToken ct)
        {
            Sent.Add(message);
            return Task.FromResult(send(message.Booking));
        }
    }

    private static GameSession Session() => new()
    {
        Game = new Game { Title = "Квиз Сумерки " + Guid.NewGuid().ToString("N")[..4] },
        StartsAt = new DateTimeOffset(2031, 11, 5, 10, 0, 0, TimeSpan.Zero), TimeZoneId = ScheduleTime.DefaultTimeZone,
        Venue = "Киса Бар, Светланская 21", DurationMinutes = 120, AgeLimit = 18, TicketPrice = 700m, Capacity = 40
    };

    private async Task<Booking> ConfirmedBookingAsync(string? telegram = "@team_msg", string? receiptKey = null)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var booking = new Booking
        {
            GameSession = Session(), TeamName = "МЯУ " + Guid.NewGuid().ToString("N")[..4], CustomerName = "Вика",
            Phone = "+79140000000", Telegram = telegram, Quantity = 3, TicketPriceSnapshot = 700m,
            ReceiptKey = receiptKey
        };
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        var admin = await app.CreateUserAsync("Admin");
        await scope.ServiceProvider.GetRequiredService<BookingWorkflow>().ConfirmAsync(booking.Id, admin.Id);
        return booking;
    }

    /// <summary>A request from the site form: unpaid, with a receipt already uploaded.</summary>
    private async Task<Booking> PendingBookingWithReceiptAsync(string receiptKey)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var booking = new Booking
        {
            GameSession = Session(), TeamName = "МЯУ " + Guid.NewGuid().ToString("N")[..4], CustomerName = "Вика",
            Phone = "+79140000000", Telegram = "@team_msg", Quantity = 3, TicketPriceSnapshot = 700m,
            ReceiptKey = receiptKey
        };
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
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
        // Telegram only: WhatsApp copies were switched off by the decision of 2026-11-03.
        Assert.Equal([MessageChannel.Telegram], messages.Select(x => x.Channel));
        Assert.DoesNotContain(messages, x => x.Channel == MessageChannel.TelegramBot);  // staff bot never gets customer text
        Assert.All(messages, m => Assert.Equal(OutgoingMessageStatus.Pending, m.Status));
        var text = messages[0].Text;
        Assert.StartsWith("Здравствуйте, мы подтвердили вашу бронь!", text);
        foreach (var line in new[] { "Квиз Сумерки", "Дата: 05.11.2031", "Время: 20:00", "Локация: Киса Бар, Светланская 21",
                     "Продолжительность: 2 часа", "Возрастное ограничение: 18+", "Команда: " + booking.TeamName, "Кол-во билетов: 3",
                     "Итого: 2" })
            Assert.Contains(line, text);

        using (var scope = app.Services.CreateScope())   // a repeated confirmation does not queue the messages twice
            await scope.ServiceProvider.GetRequiredService<BookingWorkflow>().ConfirmAsync(booking.Id, (await app.CreateUserAsync("Admin")).Id);
        Assert.Single(await MessagesAsync(booking.Id));
    }

    [Fact]
    public async Task ConfirmingPayment_DuplicatesTheReceiptThroughASignedLink()
    {
        var receiptKey = $"receipts/{Guid.NewGuid():N}-20261103120000000.png";
        var booking = await ConfirmedBookingAsync(receiptKey: receiptKey);
        var messages = await MessagesAsync(booking.Id);

        Assert.Equal(2, messages.Count);   // the text and then the receipt, both in Telegram
        var copies = messages.Where(x => x.Kind == OutgoingMessageKind.ReceiptCopy).ToList();
        Assert.Equal([MessageChannel.Telegram], copies.Select(x => x.Channel));
        // A messenger cannot send text and a file together, so the text announces the file that follows.
        Assert.EndsWith("Также дублируем ваш чек.", Assert.Single(
            messages, x => x.Kind == OutgoingMessageKind.Confirmation).Text);
        Assert.All(copies, m => Assert.Equal(string.Empty, m.Text));   // Wazzup sends either text or a file
        Assert.All(copies, m => Assert.Contains(Path.GetFileName(receiptKey) + "?t=", m.ContentUri));
        Assert.All(copies, m => Assert.StartsWith("https://", m.ContentUri));
        Assert.All(copies, m => Assert.Equal(OutgoingMessageStatus.Pending, m.Status));
        // The receipt follows the text: the dispatcher orders by CreatedAt, then by kind.
        Assert.All(messages.Where(x => x.Kind == OutgoingMessageKind.Confirmation),
            m => Assert.True(m.CreatedAt < copies.Min(c => c.CreatedAt)));
        Assert.DoesNotContain(messages, x => x.Channel == MessageChannel.TelegramBot);
    }

    /// <summary>
    /// A customer uploads a receipt and staff confirm the payment before the bot has managed to send its
    /// notification (a slow or blocked Telegram call). The receipt notification must stay queued: the group topic
    /// «Подтвердить игру» still has to get it. Only instructions to pay are dropped by a confirmation.
    /// </summary>
    [Fact]
    public async Task ReceiptNotification_SurvivesAConfirmationThatComesBeforeTheBotSentIt()
    {
        var receiptKey = $"receipts/{Guid.NewGuid():N}-20261103120000000.png";
        var booking = await PendingBookingWithReceiptAsync(receiptKey);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.OutgoingMessages.Add(new OutgoingMessage
            {
                BookingId = booking.Id, Channel = MessageChannel.TelegramBot,
                Kind = OutgoingMessageKind.PaymentDetails, Text = "Реквизиты", NextAttemptAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<BookingWorkflow>().QueueReceiptNotificationAsync(booking.Id);
        }
        // The bot is not sending yet: both messages are still pending when the payment is confirmed.
        var admin = await app.CreateUserAsync("Admin");
        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<BookingWorkflow>().ConfirmAsync(booking.Id, admin.Id);

        var messages = await MessagesAsync(booking.Id);
        var notification = Assert.Single(messages, x => x.Kind == OutgoingMessageKind.ReceiptNotification);
        Assert.Equal(OutgoingMessageStatus.Pending, notification.Status);
        Assert.StartsWith("Новая оплата ждёт подтверждения", notification.Text);
        Assert.Contains($"/bookings/pending#booking-{booking.Id:N}", notification.Text);
        Assert.Equal(MessageChannel.TelegramBot, notification.Channel);
        // The payment instructions are gone: the amount is already paid.
        var instructions = Assert.Single(messages, x => x.Kind == OutgoingMessageKind.PaymentDetails);
        Assert.Equal(OutgoingMessageStatus.Skipped, instructions.Status);

        var bot = new FakeChannel(MessageChannel.TelegramBot, ready: true, _ => SendOutcome.Sent);
        await Dispatcher(bot).RunOnceAsync(CancellationToken.None);
        Assert.Contains(bot.Sent, x => x.Kind == OutgoingMessageKind.ReceiptNotification && x.Booking.ReceiptKey == receiptKey);
        Assert.Equal(OutgoingMessageStatus.Sent, Assert.Single(
            await MessagesAsync(booking.Id), x => x.Kind == OutgoingMessageKind.ReceiptNotification).Status);
    }

    [Fact]
    public async Task FailedConfirmation_AsksManagersToContactTheCustomer_WithLinksAndTheReceipt()
    {
        var receiptKey = $"receipts/{Guid.NewGuid():N}-20261103120000000.png";
        var booking = await ConfirmedBookingAsync(receiptKey: receiptKey);
        var telegram = new FakeChannel(MessageChannel.Telegram, ready: true, _ => SendOutcome.Skipped("у клиента нет Telegram"));
        var bot = new FakeChannel(MessageChannel.TelegramBot, ready: true, _ => SendOutcome.Sent);
        await Dispatcher(telegram, bot).RunOnceAsync(CancellationToken.None);

        var request = Assert.Single((await MessagesAsync(booking.Id)).Where(x => x.Kind == OutgoingMessageKind.ContactRequest));
        Assert.Equal(MessageChannel.TelegramBot, request.Channel);
        Assert.StartsWith("Связаться с клиентом", request.Text);         // not «Новый клиент»
        foreach (var line in new[] { "https://t.me/team_msg", "https://wa.me/79140000000", "Телефон: 79140000000",
                     "Для отправки информации клиенту:", "__________________________",
                     "Вы записаны на игру:", "Дата: 05.11.2031", "Время: 20:00", "Локация: Киса Бар, Светланская 21",
                     "Продолжительность: 2 часа", "Возрастное ограничение: 18+", "Имя: Вика",
                     "Команда: " + booking.TeamName, "Кол-во билетов: 3", "Итого: 2" })
            Assert.Contains(line, request.Text);

        // The bot attaches the customer's receipt to this message (TelegramBotChannel opens it by booking).
        await Dispatcher(telegram, bot).RunOnceAsync(CancellationToken.None);
        Assert.Contains(bot.Sent, x => x.Kind == OutgoingMessageKind.ContactRequest && x.Booking.ReceiptKey == receiptKey);
        Assert.Equal(OutgoingMessageStatus.Sent, Assert.Single(
            (await MessagesAsync(booking.Id)).Where(x => x.Kind == OutgoingMessageKind.ContactRequest)).Status);

        await Dispatcher(telegram, bot).RunOnceAsync(CancellationToken.None);   // once per booking
        Assert.Single((await MessagesAsync(booking.Id)).Where(x => x.Kind == OutgoingMessageKind.ContactRequest));
    }

    [Fact]
    public async Task DeliveredConfirmation_DoesNotAskManagersToContactTheCustomer()
    {
        var booking = await ConfirmedBookingAsync();
        var telegram = new FakeChannel(MessageChannel.Telegram, ready: true, _ => SendOutcome.Sent);
        var bot = new FakeChannel(MessageChannel.TelegramBot, ready: true, _ => SendOutcome.Sent);
        await Dispatcher(telegram, bot).RunOnceAsync(CancellationToken.None);

        Assert.DoesNotContain(await MessagesAsync(booking.Id), x => x.Kind == OutgoingMessageKind.ContactRequest);
        Assert.Empty(bot.Sent);
    }

    [Fact]
    public async Task Dispatcher_SendsSkipsRetriesAndWaitsForAnUnconnectedMessenger()
    {
        var booking = await ConfirmedBookingAsync();
        var telegram = new FakeChannel(MessageChannel.Telegram, ready: true, b => b.Id == booking.Id ? SendOutcome.Sent : SendOutcome.Skipped("other"));
        var whatsApp = new FakeChannel(MessageChannel.WhatsApp, ready: false, _ => SendOutcome.Sent);
        using (var scope = app.Services.CreateScope())   // a WhatsApp copy queued before the 2026-11-03 decision
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.OutgoingMessages.Add(new OutgoingMessage
            {
                BookingId = booking.Id, Channel = MessageChannel.WhatsApp, Kind = OutgoingMessageKind.Confirmation,
                Text = "старое сообщение", NextAttemptAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
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
    public async Task MessengersPage_IsRemoved_Entirely()
    {
        // The page was retired (decision of 2026-11-03): channels live in the Wazzup cabinet,
        // message statuses are shown in the «Подтверждённые игры» card.
        var admin = await app.SignedInAsync(await app.CreateUserAsync("Admin"));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await admin.GetAsync("/messengers")).StatusCode);
    }
}
