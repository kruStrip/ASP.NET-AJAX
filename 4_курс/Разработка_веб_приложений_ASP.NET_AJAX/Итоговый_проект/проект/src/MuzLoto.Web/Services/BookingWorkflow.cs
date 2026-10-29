using System.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;
using MuzLoto.Web.Hubs;
using Npgsql;

namespace MuzLoto.Web.Services;

public sealed record IncomingBooking(
    string ExternalSubmissionId, Guid GameSessionId, string TeamName,
    string CustomerName, string Phone, string? Telegram, int Quantity);

public sealed class BookingWorkflow(
    AppDbContext db, IHubContext<BookingHub> hub, ILogger<BookingWorkflow> logger,
    IConfiguration config)
{
    /// <summary>Optional global cap; by default a booking may take up to the remaining seats.</summary>
    private int? MaxPerBooking => config.GetValue<int?>("Booking:MaxTicketsPerBooking");

    public async Task<(Guid Id, bool Duplicate)> ReceiveAsync(IncomingBooking input, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.ExternalSubmissionId) || input.ExternalSubmissionId.Length > 250 ||
            string.IsNullOrWhiteSpace(input.TeamName) || input.TeamName.Length > 150 ||
            string.IsNullOrWhiteSpace(input.CustomerName) || input.CustomerName.Length > 150 ||
            string.IsNullOrWhiteSpace(input.Phone) || input.Phone.Length > 40 ||
            input.Quantity < 1 || input.Quantity > (MaxPerBooking ?? int.MaxValue))
            throw new BusinessException("Неверные данные заявки.", 400);
        if (input.Telegram?.Length > 100) throw new BusinessException("Слишком длинный Telegram.", 400);

        const string provider = "FormDesigner";
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
                TicketPriceSnapshot = session.TicketPrice
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
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            var existing = await db.ExternalSubmissions.AsNoTracking().SingleOrDefaultAsync(
                x => x.Provider == provider && x.ExternalSubmissionId == input.ExternalSubmissionId, ct);
            if (existing is not null) return (existing.BookingId ?? Guid.Empty, true);
            throw new BusinessException("Повторите запрос позже, данные обновляются.");
        }
        catch (Exception ex) when (IsSerializationFailure(ex))
        {
            throw new BusinessException("Данные мероприятия изменились. Повторите запрос.");
        }
        await NotifyAsync(sessionId, "BookingCreated", booking!.Id);
        return (booking.Id, false);
    }

    public async Task ConfirmAsync(Guid bookingId, string userId, CancellationToken ct = default)
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
        catch (Exception ex) when (IsSerializationFailure(ex))
        {
            throw new BusinessException("Места изменились одновременно. Обновите страницу и попробуйте ещё раз.");
        }
        await NotifyAsync(sessionId, "BookingConfirmed", bookingId);
        await NotifyAsync(sessionId, "TicketsAvailabilityChanged", bookingId);
    }

    public async Task CancelAsync(Guid bookingId, string userId, CancellationToken ct = default)
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
                if (await db.Refunds.AnyAsync(x => x.PaymentId == payment.Id &&
                    x.Status == RefundStatus.Completed, ct))
                    throw new BusinessException("По этому платежу уже зафиксирован возврат.");
                if (payment.Amount > 0)
                {
                    if (await db.Refunds.AnyAsync(x => x.PaymentId == payment.Id &&
                        x.Status == RefundStatus.Pending, ct))
                        throw new BusinessException("Возврат уже ожидает выполнения.");
                    db.Refunds.Add(new Refund
                    {
                        PaymentId = payment.Id, Amount = payment.Amount,
                        RequestedAt = now, RequestedByUserId = userId
                    });
                }
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
        catch (Exception ex) when (IsSerializationFailure(ex))
        {
            throw new BusinessException("Состояние заказа изменилось. Обновите страницу и повторите попытку.");
        }
        await NotifyAsync(sessionId, "BookingUpdated", bookingId);
        await NotifyAsync(sessionId, "RefundRequested", bookingId);
        await NotifyAsync(sessionId, "TicketsAvailabilityChanged", bookingId);
    }

    /// <summary>Edit an unpaid booking. Pending requests hold no seats, so only the remaining-seat limit applies.</summary>
    public async Task UpdatePendingAsync(Guid bookingId, string teamName, string customerName,
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
        catch (Exception ex) when (IsSerializationFailure(ex))
        {
            throw new BusinessException("Данные заявки изменились одновременно. Повторите сохранение.");
        }
        await NotifyAsync(sessionId, "BookingUpdated", bookingId);
    }

    /// <summary>Restore a cancelled booking within one hour, retaining any previously recorded payment.</summary>
    public async Task RestoreCancelledAsync(Guid bookingId, string userId, CancellationToken ct = default)
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
                    x.Status == RefundStatus.Completed, ct))
                    throw new BusinessException("Деньги уже возвращены клиенту. Для нового бронирования нужна новая оплата.");
                pendingRefundToCancel = await db.Refunds.SingleOrDefaultAsync(x =>
                    x.PaymentId == payment.Id && x.Status == RefundStatus.Pending, ct);
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
        catch (Exception ex) when (IsSerializationFailure(ex))
        {
            throw new BusinessException("Состав мест изменился одновременно. Повторите запрос.");
        }
        await NotifyAsync(sessionId, "BookingUpdated", bookingId);
        await NotifyAsync(sessionId, "RefundCancelled", bookingId);
        await NotifyAsync(sessionId, "TicketsAvailabilityChanged", bookingId);
    }

    /// <summary>
    /// Record a refund only AFTER an employee has actually transferred the money.
    /// Booking cancellation itself never marks funds returned.
    /// </summary>
    public async Task ConfirmRefundAsync(Guid refundId, string userId,
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
            if (refund.Payment.Booking.Status != BookingStatus.Cancelled)
                throw new BusinessException("Бронирование уже восстановлено. Возврат запрещён.");
            if (refund.Payment.Status != PaymentStatus.Confirmed ||
                refund.Amount <= 0 || refund.Amount != refund.Payment.Amount)
                throw new BusinessException("Данные суммы возврата некорректны.");
            if (await db.Refunds.AnyAsync(x => x.PaymentId == refund.PaymentId &&
                x.Status == RefundStatus.Completed, ct))
                throw new BusinessException("Деньги по платежу уже возвращены.");

            refund.Status = RefundStatus.Completed;
            refund.CompletedAt = DateTimeOffset.UtcNow;
            refund.CompletedByUserId = userId;
            refund.TransferReference = transferReference?.Trim();
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            sessionId = refund.Payment.Booking.GameSessionId;
        }
        catch (Exception ex) when (IsSerializationFailure(ex))
        {
            throw new BusinessException("Статус возврата изменился одновременно. Обновите страницу.");
        }
        await NotifyAsync(sessionId, "RefundCompleted", refundId);
    }

    /// <summary>Undo an accidentally completed event; deleted pending bookings are not recreated.</summary>
    public async Task ReopenSessionAsync(Guid sessionId, CancellationToken ct = default)
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
        catch (Exception ex) when (IsSerializationFailure(ex))
        {
            throw new BusinessException("Состояние игры изменилось. Повторите запрос.");
        }
        await NotifyAsync(sessionId, "GameReopened", sessionId);
    }

    public async Task CompleteSessionAsync(Guid sessionId, CancellationToken ct = default)
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
        catch (Exception ex) when (IsSerializationFailure(ex))
        {
            throw new BusinessException("Данные игры изменились одновременно. Обновите страницу и повторите попытку.");
        }
        await NotifyAsync(sessionId, "GameCompleted", sessionId);
    }

    private async Task NotifyAsync(Guid sessionId, string method, object payload)
    {
        try { await hub.Clients.Group($"session:{sessionId}").SendCoreAsync(method, new[] { payload }); }
        catch (Exception ex) { logger.LogWarning(ex, "SignalR notification {Method} failed after database commit", method); }
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
    private static bool IsSerializationFailure(Exception ex) =>
        ex is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure } ||
        ex is DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.SerializationFailure } };
}
