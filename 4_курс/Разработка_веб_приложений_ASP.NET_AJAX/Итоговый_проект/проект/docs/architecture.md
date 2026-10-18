# МузЛото — проектируемая архитектура

Это проектная документация, а не описание завершённого приложения.

## Бизнес-объекты

AppUser, Game, GameSession, Booking, Payment, BookingStatusHistory, GameImage, EventStaffAssignment, ExternalSubmission. Связи 1:M: Game → GameSession → Booking; Booking → Payment, BookingStatusHistory. M:N: AppUser ↔ GameSession через EventStaffAssignment с составным первичным ключом.

## Правила

Заявки поступают из существующей формы FormDesigner. Места резервируются только при подтверждении оплаты, внутри транзакции с повторной проверкой остатка. При завершении игры неподтверждённые заявки удаляются; оплаченная история сохраняется.

## Предполагаемая реализация

Razor Pages для управления, AJAX для изменения статуса и счетчиков, REST API через DTO/JWT с версиями v1/v2, SignalR для групп по сеансам, EF Core для PostgreSQL, Identity и resource-based authorization для ограничения прав.
