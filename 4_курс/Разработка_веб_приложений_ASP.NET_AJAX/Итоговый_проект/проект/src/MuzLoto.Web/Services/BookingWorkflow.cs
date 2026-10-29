using System.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;
using MuzLoto.Web.Hubs;
using Npgsql;

namespace MuzLoto.Web.Services;

/// <param name="ExternalSubmissionId">Idempotency key: FormDesigner submission ID or the API Idempotency-Key.</param>
/// <param name="OwnerUserId">Account that created the booking via the API; null for FormDesigner.</param>
public sealed record IncomingBooking(
    string ExternalSubmissionId, Guid GameSessionId, string TeamName,
    string CustomerName, string Phone, string? Telegram, int Quantity,
    string Provider = BookingSources.FormDesigner, string? OwnerUserId = null);

public static class BookingSources
{
    public const string FormDesigner = "FormDesigner";
    public const string Api = "Api";
}

public sealed class BookingWorkflow(
    AppDbContext db, IHubContext<BookingHub> hub, ILogger<BookingWorkflow> logger,
    IConfiguration config)
{
    /// <summary>Optional global cap; by default a booking may take up to the remaining seats.</summary>
    private int? MaxPerBooking => config.GetValue<int?>("Booking:MaxTicketsPerBooking");

    public Task<(Guid Id, bool Duplicate)> ReceiveAsync(IncomingBooking input, CancellationToken ct = default) =>
        WithRetry(() => ReceiveCoreAsync(input, ct), ct);

    private async Task<(Guid Id, bool Duplicate)> ReceiveCoreAsync(IncomingBooking input, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.ExternalSubmissionId) || input.ExternalSubmissionId.Length > 250 ||
            string.IsNullOrWhiteSpace(input.TeamName) || input.TeamName.Length > 150 ||
            string.IsNullOrWhiteSpace(input.CustomerName) || input.CustomerName.Length > 150 ||
            string.IsNullOrWhiteSpace(input.Phone) || input.Phone.Length > 40 ||
            input.Quantity < 1 || input.Quantity > (MaxPerBooking ?? int.MaxValue) ||
            input.Provider is not (BookingSources.FormDesigner or BookingSources.Api))
            throw new BusinessException("Неверные данные заявки.", 400);
        if (input.Telegram?.Length > 100) throw new BusinessException("Слишком длинный Telegram.", 400);

        var provider = input.Provider;
        Booking? booking = null;
        Guid sessionId;
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var previous = await db.ExternalSubmissions.AsNoTracking().FirstOrDefaultAsync(
                x => x.Provider == provider && x.ExternalSubmissionId == input.ExternalSubmissionId, ct);
            if (previous is not null) return (previous.BookingId ?? Guid.Empty, true);

            var session = await db.GameSessions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == input.GameSessionId, ct)
                ?? throw new BusinessException("Мероприятие не найдено.", 404);
            if (session.CompletedAt.HasValue || !session.SalesOpen)
                throw new BusinessException("Продажа на это мероприятие закрыта.");
            // Pending requests never reserve seats, so only paid bookings reduce what can be requested.
            var reserved = await db.Bookings
                .Where(x => x.GameSessionId == session.Id && x.Status == BookingStatus.Confirmed)
                .SumAsync(x => (int?)x.Quantity, ct) ?? 0;
            if (input.Quantity > session.Capacity - reserved)
                throw new BusinessException($"Свободных билетов меньше, чем запрошено: осталось {Math.Max(0, session.Capacity - reserved)}.");

            booking = new Booking
            {
                GameSessionId = session.Id, TeamName = input.TeamName.Trim(),
                CustomerName = input.CustomerName.Trim(), Phone = input.Phone.Trim(),
                Telegram = input.Telegram?.Trim(), Quantity = input.Quantity,
                TicketPriceSnapshot = session.TicketPrice, OwnerUserId = input.OwnerUserId
            };
            db.Bookings.Add(booking);
            db.ExternalSubmissions.Add(new ExternalSubmission
            {
                Provider = provider, ExternalSubmissionId = input.ExternalSubmissionId,
                BookingId = booking.Id
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            sessionId = session.Id;
        }
        catch (Exception ex) when (DbErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            var existing = await db.ExternalSubmissions.AsNoTracking().SingleOrDefaultAsync(
                x => x.Provider == provider && x.ExternalSubmissionId == input.ExternalSubmissionId, ct);
            if (existing is not null) return (existing.BookingId ?? Guid.Empty, true);
            throw new BusinessException("Повторите запрос позже, данные обновляются.");
        }
        catch (Exception ex) when (DbErrors.IsSerializationFailure(ex))
        {
            throw new ConcurrencyConflictException("Данные мероприятия изменились. Повторите запрос.");
        }
        await NotifyAsync(sessionId, "BookingCreated", booking!.Id);
        return (booking.Id, false);
    }

    public Task ConfirmAsync(Guid bookingId, string userId, CancellationToken ct = default) =>
        WithRetry(() => ConfirmCoreAsync(bookingId, userId, ct), ct);

    private async Task ConfirmCoreAsync(Guid bookingId, string userId, CancellationToken ct = default)
    {
        Guid sessionId;
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var booking = await db.Bookings.Include(x => x.GameSession)
                .SingleOrDefaultAsync(x => x.Id == bookingId, ct)
                ?? throw new BusinessException("Заявка не найдена.", 404);
            if (booking.Status == BookingStatus.Confirmed) return; // idempotent repeat
            if (booking.Status != BookingStatus.Pending)
                throw new BusinessException("Можно подтверждать только ожидающие заявки.");
            var session = booking.GameSession;
            if (session.CompletedAt.HasValue || !session.SalesOpen)
                throw new BusinessException("Продажа на это мероприятие закрыта.");
            var reserved = await db.Bookings
                .Where(x => x.GameSessionId == booking.GameSessionId && x.Status == BookingStatus.Confirmed)
                .SumAsync(x => (int?)x.Quantity, ct) ?? 0;
            if (!CapacityCalculator.CanConfirm(session.Capacity, reserved, booking.Quantity))
                throw new BusinessException("Недостаточно свободных билетов. Оплату нельзя подтвердить.");
            var now = DateTimeOffset.UtcNow;
            booking.Status = BookingStatus.Confirmed;
            booking.ConfirmedAt = now;
            db.Payments.Add(new Payment
            {
                BookingId = booking.Id, Amount = booking.Quantity * booking.TicketPriceSnapshot,
                ConfirmedByUserId = userId, ConfirmedAt = now
            });
            db.BookingStatusHistory.Add(new BookingStatusHistory
            {
                BookingId = booking.Id, OldStatus = BookingStatus.Pending,
                NewStatus = BookingStatus.Confirmed, ChangedByUserId = userId,
                ChangedAt = now, Reason = "Ручное подтверждение оплаты"
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            sessionId = session.Id;
        }
        catch (Exception ex) when (DbErrors.IsSerializationFailure(ex))
        {
            throw new ConcurrencyConflictException("Места изменились одновременно. Обновите страницу и попробуйте ещё раз.");
        }
        await NotifyAsync(sessionId, "BookingConfirmed", bookingId);
        await NotifyAsync(sessionId, "TicketsAvailabilityChanged", bookingId);
    }

    public Task CancelAsync(Guid bookingId, string userId, CancellationToken ct = default) =>
        WithRetry(() => CancelCoreAsync(bookingId, userId, ct), ct);

    private async Task CancelCoreAsync(Guid bookingId, string userId, CancellationToken ct = default)
    {
        Guid sessionId;
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var booking = await db.Bookings.Include(x => x.Payment)
                .SingleOrDefaultAsync(x => x.Id == bookingId, ct)
                ?? throw new BusinessException("Заявка не найдена.", 404);
            if (booking.Status == BookingStatus.Cancelled) return;
            var oldStatus = booking.Status;
            var now = DateTimeOffset.UtcNow;
            if (oldStatus == BookingStatus.Confirmed)
            {
                var payment = booking.Payment
                    ?? throw new BusinessException("Подтверждённый платёж не найден: проверьте заказ.");
                var cancellations = db.Refunds.Where(x => x.PaymentId == payment.Id && x.Kind == RefundKind.Cancellation);
                if (await cancellations.AnyAsync(x => x.Status == RefundStatus.Completed, ct))
                    throw new BusinessException("По этому платежу уже зафиксирован возврат.");
                if (await cancellations.AnyAsync(x => x.Status == RefundStatus.Pending, ct))
                    throw new BusinessException("Возврат уже ожидает выполнения.");
                // Partial refunds for earlier reductions are owed separately; refund only the rest.
                var alreadyOwed = await db.Refunds.Where(x => x.PaymentId == payment.Id &&
                        x.Kind == RefundKind.QuantityDecrease && x.Status != RefundStatus.Cancelled)
                    .SumAsync(x => (decimal?)x.Amount, ct) ?? 0;
                var amount = payment.Amount - alreadyOwed;
                if (amount > 0)
                    db.Refunds.Add(new Refund
                    {
                        PaymentId = payment.Id, Amount = amount, Kind = RefundKind.Cancellation,
                        RequestedAt = now, RequestedByUserId = userId
                    });
                // A surcharge that was never paid is simply dropped.
                await db.QuantityChanges.Where(x => x.BookingId == booking.Id && x.Status == QuantityChangeStatus.AwaitingPayment)
                    .ForEachAsync(x => { x.Status = QuantityChangeStatus.Cancelled; x.CompletedAt = now; x.CompletedByUserId = userId; }, ct);
            }
            booking.Status = BookingStatus.Cancelled;
            booking.CancelledAt = now;
            db.BookingStatusHistory.Add(new BookingStatusHistory
            {
                BookingId = booking.Id, OldStatus = oldStatus, NewStatus = BookingStatus.Cancelled,
                ChangedByUserId = userId, ChangedAt = now,
                Reason = oldStatus == BookingStatus.Confirmed
                    ? "Отмена заказа; возврат денег поставлен в очередь, но не выполнен"
                    : "Отмена неоплаченной заявки"
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            sessionId = booking.GameSessionId;
        }
        catch (Exception ex) when (DbErrors.IsSerializationFailure(ex))
        {
            throw new ConcurrencyConflictException("Состояние заказа изменилось. Обновите страницу и повторите попытку.");
        }
        await NotifyAsync(sessionId, "BookingUpdated", bookingId);
        await NotifyAsync(sessionId, "RefundRequested", bookingId);
        await NotifyAsync(sessionId, "TicketsAvailabilityChanged", bookingId);
    }

    /// <summary>Edit an unpaid booking. Pending requests hold no seats, so only the remaining-seat limit applies.</summary>
    public Task UpdatePendingAsync(Guid bookingId, string teamName, string customerName,
        string? telegram, int quantity, CancellationToken ct = default) =>
        WithRetry(() => UpdatePendingCoreAsync(bookingId, teamName, customerName, telegram, quantity, ct), ct);

    private async Task UpdatePendingCoreAsync(Guid bookingId, string teamName, string customerName,
        string? telegram, int quantity, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(teamName) || teamName.Length > 150 ||
            string.IsNullOrWhiteSpace(customerName) || customerName.Length > 150 ||
            telegram?.Length > 100 || quantity < 1 || quantity > (MaxPerBooking ?? int.MaxValue))
            throw new BusinessException("Неверные данные заявки.", 400);
        Guid sessionId;
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var booking = await db.Bookings.Include(x => x.GameSession)
                .SingleOrDefaultAsync(x => x.Id == bookingId, ct)
                ?? throw new BusinessException("Заявка не найдена.", 404);
            if (booking.Status != BookingStatus.Pending)
                throw new BusinessException("Изменить можно только неоплаченную заявку.");
            var session = booking.GameSession;
            if (session.CompletedAt.HasValue || !session.SalesOpen)
                throw new BusinessException("Продажа на это мероприятие закрыта.");
            var reserved = await db.Bookings
                .Where(x => x.GameSessionId == session.Id && x.Status == BookingStatus.Confirmed)
                .SumAsync(x => (int?)x.Quantity, ct) ?? 0;
            if (quantity > session.Capacity - reserved)
                throw new BusinessException($"Свободных билетов меньше, чем запрошено: осталось {Math.Max(0, session.Capacity - reserved)}.");
            booking.TeamName = teamName.Trim();
            booking.CustomerName = customerName.Trim();
            booking.Telegram = telegram;
            booking.Quantity = quantity;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            sessionId = session.Id;
        }
        catch (Exception ex) when (DbErrors.IsSerializationFailure(ex))
        {
            throw new ConcurrencyConflictException("Данные заявки изменились одновременно. Повторите сохранение.");
        }
        await NotifyAsync(sessionId, "BookingUpdated", bookingId);
    }

    /// <summary>
    /// Changes the ticket count of a paid order. A decrease frees seats at once and queues a refund of the difference;
    /// an increase waits for a surcharge and takes seats only when staff confirm it (ConfirmSurchargeAsync).
    /// Setting zero is a cancellation and goes through CancelAsync.
    /// </summary>
    public Task<QuantityChange?> ChangePaidQuantityAsync(Guid bookingId, int newQuantity, string userId,
        CancellationToken ct = default) =>
        WithRetry(() => ChangePaidQuantityCoreAsync(bookingId, newQuantity, userId, ct), ct);

    private async Task<QuantityChange?> ChangePaidQuantityCoreAsync(Guid bookingId, int newQuantity, string userId,
        CancellationToken ct = default)
    {
        if (newQuantity < 1 || newQuantity > (MaxPerBooking ?? int.MaxValue))
            throw new BusinessException("Количество билетов должно быть не меньше 1. Для отмены используйте «Отменить».", 400);
        QuantityChange change;
        Guid sessionId;
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var booking = await db.Bookings.Include(x => x.Payment).Include(x => x.GameSession)
                .SingleOrDefaultAsync(x => x.Id == bookingId, ct)
                ?? throw new BusinessException("Заявка не найдена.", 404);
            if (booking.Status != BookingStatus.Confirmed || booking.Payment is null)
                throw new BusinessException("Изменение с доплатой или возвратом возможно только для оплаченного заказа.");
            if (booking.GameSession.CompletedAt.HasValue || booking.GameSession.ArchivedAt.HasValue)
                throw new BusinessException("Игра завершена: количество в заказе не меняется.");
            if (await db.QuantityChanges.AnyAsync(x => x.BookingId == bookingId && x.Status == QuantityChangeStatus.AwaitingPayment, ct))
                throw new BusinessException("По заказу уже ожидается доплата. Подтвердите или отмените её.");
            if (newQuantity == booking.Quantity) return null;

            var now = DateTimeOffset.UtcNow;
            var delta = newQuantity - booking.Quantity;
            change = new QuantityChange
            {
                BookingId = booking.Id, OldQuantity = booking.Quantity, NewQuantity = newQuantity,
                Amount = Math.Abs(delta) * booking.TicketPriceSnapshot,
                RequestedAt = now, RequestedByUserId = userId
            };
            if (delta > 0)
            {
                // Check early so staff do not ask for money that cannot buy seats; re-checked on confirmation.
                await EnsureSeatsAsync(booking.GameSession, delta, ct);
                change.Status = QuantityChangeStatus.AwaitingPayment;
            }
            else
            {
                var refund = new Refund
                {
                    PaymentId = booking.Payment.Id, Amount = change.Amount, Kind = RefundKind.QuantityDecrease,
                    RequestedAt = now, RequestedByUserId = userId
                };
                db.Refunds.Add(refund);
                booking.Quantity = newQuantity;
                change.Refund = refund;
                change.Status = QuantityChangeStatus.Applied;
                change.CompletedAt = now;
                change.CompletedByUserId = userId;
            }
            db.QuantityChanges.Add(change);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            sessionId = booking.GameSessionId;
        }
        catch (Exception ex) when (DbErrors.IsSerializationFailure(ex))
        {
            throw new ConcurrencyConflictException("Заказ изменился одновременно. Обновите страницу и повторите.");
        }
        await NotifyAsync(sessionId, "BookingUpdated", bookingId);
        if (!change.IsIncrease)
        {
            await NotifyAsync(sessionId, "RefundRequested", bookingId);
            await NotifyAsync(sessionId, "TicketsAvailabilityChanged", bookingId);
        }
        return change;
    }

    /// <summary>Staff confirm the surcharge arrived: seats are re-checked under the lock, then taken and the payment grows.</summary>
    public Task ConfirmSurchargeAsync(Guid changeId, string userId, CancellationToken ct = default) =>
        WithRetry(() => ConfirmSurchargeCoreAsync(changeId, userId, ct), ct);

    private async Task ConfirmSurchargeCoreAsync(Guid changeId, string userId, CancellationToken ct = default)
    {
        Guid sessionId, bookingId;
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var change = await db.QuantityChanges.Include(x => x.Booking).ThenInclude(x => x.Payment)
                .Include(x => x.Booking).ThenInclude(x => x.GameSession)
                .SingleOrDefaultAsync(x => x.Id == changeId, ct)
                ?? throw new BusinessException("Изменение не найдено.", 404);
            if (change.Status == QuantityChangeStatus.Applied) return; // idempotent repeat
            if (change.Status != QuantityChangeStatus.AwaitingPayment)
                throw new BusinessException("Это изменение больше не ожидает доплаты.");
            var booking = change.Booking;
            if (booking.Status != BookingStatus.Confirmed || booking.Payment is null || booking.Quantity != change.OldQuantity)
                throw new BusinessException("Заказ изменился. Отмените доплату и оформите изменение заново.");
            if (booking.GameSession.CompletedAt.HasValue || booking.GameSession.ArchivedAt.HasValue)
                throw new BusinessException("Игра завершена: доплату подтвердить нельзя.");
            await EnsureSeatsAsync(booking.GameSession, change.NewQuantity - change.OldQuantity, ct);

            var now = DateTimeOffset.UtcNow;
            booking.Quantity = change.NewQuantity;
            booking.Payment.Amount += change.Amount;
            change.Status = QuantityChangeStatus.Applied;
            change.CompletedAt = now;
            change.CompletedByUserId = userId;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            (sessionId, bookingId) = (booking.GameSessionId, booking.Id);
        }
        catch (Exception ex) when (DbErrors.IsSerializationFailure(ex))
        {
            throw new ConcurrencyConflictException("Места изменились одновременно. Повторите подтверждение.");
        }
        await NotifyAsync(sessionId, "BookingUpdated", bookingId);
        await NotifyAsync(sessionId, "TicketsAvailabilityChanged", bookingId);
    }

    public async Task CancelSurchargeAsync(Guid changeId, string userId, CancellationToken ct = default)
    {
        var change = await db.QuantityChanges.Include(x => x.Booking).SingleOrDefaultAsync(x => x.Id == changeId, ct)
            ?? throw new BusinessException("Изменение не найдено.", 404);
        if (change.Status == QuantityChangeStatus.Cancelled) return;
        if (change.Status != QuantityChangeStatus.AwaitingPayment)
            throw new BusinessException("Доплата уже подтверждена.");
        change.Status = QuantityChangeStatus.Cancelled;
        change.CompletedAt = DateTimeOffset.UtcNow;
        change.CompletedByUserId = userId;
        await db.SaveChangesAsync(ct);
        await NotifyAsync(change.Booking.GameSessionId, "BookingUpdated", change.BookingId);
    }

    private async Task EnsureSeatsAsync(GameSession session, int extraSeats, CancellationToken ct)
    {
        var reserved = await db.Bookings
            .Where(x => x.GameSessionId == session.Id && x.Status == BookingStatus.Confirmed)
            .SumAsync(x => (int?)x.Quantity, ct) ?? 0;
        if (!CapacityCalculator.CanConfirm(session.Capacity, reserved, extraSeats))
            throw new BusinessException($"Свободных мест недостаточно: осталось {Math.Max(0, session.Capacity - reserved)}.");
    }

    /// <summary>Restore a cancelled booking within one hour, retaining any previously recorded payment.</summary>
    public Task RestoreCancelledAsync(Guid bookingId, string userId, CancellationToken ct = default) =>
        WithRetry(() => RestoreCancelledCoreAsync(bookingId, userId, ct), ct);

    private async Task RestoreCancelledCoreAsync(Guid bookingId, string userId, CancellationToken ct = default)
    {
        Guid sessionId;
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var booking = await db.Bookings.Include(x => x.GameSession)
                .SingleOrDefaultAsync(x => x.Id == bookingId, ct)
                ?? throw new BusinessException("Заявка не найдена.", 404);
            if (booking.Status != BookingStatus.Cancelled || !booking.CancelledAt.HasValue)
                throw new BusinessException("Восстановить можно только отменённую заявку.");
            var now = DateTimeOffset.UtcNow;
            if (booking.CancelledAt.Value > now || now - booking.CancelledAt.Value > TimeSpan.FromHours(1))
                throw new BusinessException("Истёк часовой срок восстановления заявки.");
            var session = booking.GameSession;
            if (session.CompletedAt.HasValue || !session.SalesOpen)
                throw new BusinessException("Игра завершена или продажи закрыты. Восстановление недоступно.");
            var cancellation = await db.BookingStatusHistory
                .Where(x => x.BookingId == bookingId && x.NewStatus == BookingStatus.Cancelled)
                .OrderByDescending(x => x.ChangedAt).ThenByDescending(x => x.Id)
                .FirstOrDefaultAsync(ct)
                ?? throw new BusinessException("Не удалось определить исходный статус заявки.");
            var previousStatus = cancellation.OldStatus;
            if (previousStatus is not (BookingStatus.Pending or BookingStatus.Confirmed))
                throw new BusinessException("Исходный статус заявки некорректен.");

            Refund? pendingRefundToCancel = null;
            if (previousStatus == BookingStatus.Confirmed)
            {
                // Never restore after money actually left: a new payment is required.
                var payment = await db.Payments.SingleOrDefaultAsync(x => x.BookingId == bookingId, ct)
                    ?? throw new BusinessException("Платёж не найден. Требуется проверка сотрудником.");
                if (await db.Refunds.AnyAsync(x => x.PaymentId == payment.Id &&
                    x.Kind == RefundKind.Cancellation && x.Status == RefundStatus.Completed, ct))
                    throw new BusinessException("Деньги уже возвращены клиенту. Для нового бронирования нужна новая оплата.");
                pendingRefundToCancel = await db.Refunds.SingleOrDefaultAsync(x =>
                    x.PaymentId == payment.Id && x.Kind == RefundKind.Cancellation && x.Status == RefundStatus.Pending, ct);
                var reserved = await db.Bookings
                    .Where(x => x.GameSessionId == session.Id && x.Status == BookingStatus.Confirmed)
                    .SumAsync(x => (int?)x.Quantity, ct) ?? 0;
                if (!CapacityCalculator.CanConfirm(session.Capacity, reserved, booking.Quantity))
                    throw new BusinessException("Недостаточно свободных билетов для восстановления.");
            }

            if (pendingRefundToCancel is not null)
            {
                // Restore cancels a request that was never paid out.
                pendingRefundToCancel.Status = RefundStatus.Cancelled;
                pendingRefundToCancel.CancelledAt = now;
                pendingRefundToCancel.CancelledByUserId = userId;
            }
            booking.Status = previousStatus;
            booking.CancelledAt = null;
            db.BookingStatusHistory.Add(new BookingStatusHistory
            {
                BookingId = booking.Id, OldStatus = BookingStatus.Cancelled,
                NewStatus = previousStatus, ChangedByUserId = userId, ChangedAt = now,
                Reason = "Восстановление сотрудником в течение одного часа"
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            sessionId = session.Id;
        }
        catch (Exception ex) when (DbErrors.IsSerializationFailure(ex))
        {
            throw new ConcurrencyConflictException("Состав мест изменился одновременно. Повторите запрос.");
        }
        await NotifyAsync(sessionId, "BookingUpdated", bookingId);
        await NotifyAsync(sessionId, "RefundCancelled", bookingId);
        await NotifyAsync(sessionId, "TicketsAvailabilityChanged", bookingId);
    }

    /// <summary>
    /// Record a refund only AFTER an employee has actually transferred the money.
    /// Booking cancellation itself never marks funds returned.
    /// </summary>
    public Task ConfirmRefundAsync(Guid refundId, string userId,
        string? transferReference = null, CancellationToken ct = default) =>
        WithRetry(() => ConfirmRefundCoreAsync(refundId, userId, transferReference, ct), ct);

    private async Task ConfirmRefundCoreAsync(Guid refundId, string userId,
        string? transferReference = null, CancellationToken ct = default)
    {
        if (transferReference?.Length > 200)
            throw new BusinessException("Номер операции или комментарий слишком длинный.", 400);
        Guid sessionId;
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var refund = await db.Refunds.Include(x => x.Payment)
                .ThenInclude(x => x.Booking).SingleOrDefaultAsync(x => x.Id == refundId, ct)
                ?? throw new BusinessException("Запрос на возврат не найден.", 404);
            if (refund.Status == RefundStatus.Completed) return; // idempotent re-submit
            if (refund.Status != RefundStatus.Pending)
                throw new BusinessException("Этот запрос больше не ожидает возврата.");
            if (refund.Kind == RefundKind.Cancellation && refund.Payment.Booking.Status != BookingStatus.Cancelled)
                throw new BusinessException("Бронирование уже восстановлено. Возврат запрещён.");
            if (refund.Payment.Status != PaymentStatus.Confirmed || refund.Amount <= 0)
                throw new BusinessException("Данные суммы возврата некорректны.");
            var completed = db.Refunds.Where(x => x.PaymentId == refund.PaymentId && x.Status == RefundStatus.Completed);
            if (refund.Kind == RefundKind.Cancellation && await completed.AnyAsync(x => x.Kind == RefundKind.Cancellation, ct))
                throw new BusinessException("Деньги по платежу уже возвращены.");
            if ((await completed.SumAsync(x => (decimal?)x.Amount, ct) ?? 0) + refund.Amount > refund.Payment.Amount)
                throw new BusinessException("Сумма возвратов превысила бы полученную оплату.");

            refund.Status = RefundStatus.Completed;
            refund.CompletedAt = DateTimeOffset.UtcNow;
            refund.CompletedByUserId = userId;
            refund.TransferReference = transferReference?.Trim();
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            sessionId = refund.Payment.Booking.GameSessionId;
        }
        catch (Exception ex) when (DbErrors.IsSerializationFailure(ex))
        {
            throw new ConcurrencyConflictException("Статус возврата изменился одновременно. Обновите страницу.");
        }
        await NotifyAsync(sessionId, "RefundCompleted", refundId);
    }

    /// <summary>Undo an accidentally completed event; deleted pending bookings are not recreated.</summary>
    public Task ReopenSessionAsync(Guid sessionId, CancellationToken ct = default) =>
        WithRetry(() => ReopenSessionCoreAsync(sessionId, ct), ct);

    private async Task ReopenSessionCoreAsync(Guid sessionId, CancellationToken ct = default)
    {
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var session = await db.GameSessions.SingleOrDefaultAsync(x => x.Id == sessionId, ct)
                ?? throw new BusinessException("Игра не найдена.", 404);
            if (!session.CompletedAt.HasValue) return;
            session.CompletedAt = null;
            // Deliberately keep SalesOpen=false: the admin must explicitly reopen sales
            // in Edit, avoiding accidental acceptance of new external bookings.
            session.SalesOpen = false;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (Exception ex) when (DbErrors.IsSerializationFailure(ex))
        {
            throw new ConcurrencyConflictException("Состояние игры изменилось. Повторите запрос.");
        }
        await NotifyAsync(sessionId, "GameReopened", sessionId);
    }

    public Task CompleteSessionAsync(Guid sessionId, CancellationToken ct = default) =>
        WithRetry(() => CompleteSessionCoreAsync(sessionId, ct), ct);

    private async Task CompleteSessionCoreAsync(Guid sessionId, CancellationToken ct = default)
    {
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var session = await db.GameSessions.SingleOrDefaultAsync(x => x.Id == sessionId, ct)
                ?? throw new BusinessException("Мероприятие не найдено.", 404);
            if (session.CompletedAt.HasValue) return;
            // ExternalSubmission records keep their external IDs with a null BookingId to
            // prevent duplicate redelivery after the pending customer record is deleted.
            var pending = await db.Bookings.Where(x => x.GameSessionId == sessionId &&
                x.Status == BookingStatus.Pending).ToListAsync(ct);
            db.Bookings.RemoveRange(pending);
            session.SalesOpen = false;
            session.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (Exception ex) when (DbErrors.IsSerializationFailure(ex))
        {
            throw new ConcurrencyConflictException("Данные игры изменились одновременно. Обновите страницу и повторите попытку.");
        }
        await NotifyAsync(sessionId, "GameCompleted", sessionId);
    }

    private async Task NotifyAsync(Guid sessionId, string method, object payload)
    {
        try { await hub.Clients.Group(BookingHub.GroupFor(sessionId)).SendCoreAsync(method, new[] { payload }); }
        catch (Exception ex) { logger.LogWarning(ex, "SignalR notification {Method} failed after database commit", method); }
    }

    private const int MaxAttempts = 5;

    /// <summary>
    /// SERIALIZABLE transactions may be aborted by PostgreSQL when they race (40001). Re-running the whole
    /// operation re-reads fresh data, so the outcome is still correct (e.g. "no seats left" instead of overselling).
    /// </summary>
    private async Task<T> WithRetry<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return await operation(); }
            catch (ConcurrencyConflictException) when (attempt < MaxAttempts)
            {
                db.ChangeTracker.Clear();
                await Task.Delay(Random.Shared.Next(10, 50) * attempt, ct);
            }
        }
    }

    private Task WithRetry(Func<Task> operation, CancellationToken ct) =>
        WithRetry(async () => { await operation(); return true; }, ct);
}
