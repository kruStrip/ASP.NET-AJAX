# МузЛото — информационная система мероприятий

**Текущее состояние: этап 8b.** ASP.NET Core 8, PostgreSQL, Razor Pages, Identity (cookie + JWT), REST API v1/v2, AJAX, SignalR.
Сборка, unit- и интеграционные тесты на PostgreSQL проходят в GitHub Actions. Подробный статус — [docs/STATUS.md](docs/STATUS.md),
изменения ТЗ — [docs/DECISIONS.md](docs/DECISIONS.md).

## Что реализовано

- Регистрация (`/account/register`, роль User), вход, выход (POST), смена пароля, блокировка на 15 минут после 5 ошибок.
- Роли Admin / Manager / User; сотрудники и назначения менеджеров на игры (`/staff`).
- Игры и сеансы, заявки «Нужно подтвердить», подтверждение оплаты (SERIALIZABLE, без перепродажи), отмена и восстановление в течение часа.
- Завершение игры: неоплаченные заявки удаляются, оплаченные сохраняются. Ручные возвраты и чистая выручка.
- «Мои заявки» (`/bookings/mine`) — только заявки текущего пользователя; изменение неоплаченной заявки по политике `CanEditBooking`.
- Демо-данные в Development: игры, сеансы, заявки, платежи, возвраты (вымышленные).
- REST API с JWT (`POST /api/auth/login`): мероприятия `api/v1|v2/events` (полный CRUD), заявки `api/v1/bookings` (пользователь — только свои), отчёт `api/v1/reports/summary`; Swagger v1/v2 на `/swagger` в Development, все запросы — в [requests/MuzLoto-API.http](requests/MuzLoto-API.http).
- AJAX-действия с частичным обновлением, SignalR-уведомления по группам игр с переподключением, счётчик сотрудников онлайн, мгновенный отзыв доступа к группе.

## Учётные данные для локальной разработки

| Роль | E-mail | Пароль |
|---|---|---|
| Admin (seed) | `admin@muzloto.local` | `Admin2026!` |
| Manager (демо) | `manager1@muzloto.local`, `manager2@muzloto.local` | `Manager2026!` |
| User (демо) | `user@muzloto.local` | `User2026!` |

Создаётся только в окружении Development (`appsettings.Development.json`); вне Development этот пароль запрещён.
Если в `.env` заданы `MUZLOTO_ADMIN_EMAIL` и `MUZLOTO_ADMIN_PASSWORD`, используются они.
Обычного пользователя можно зарегистрировать на `/account/register`.

## Локальный запуск

```bash
cp .env.example .env
# Поменяйте все значения REPLACE_...
docker compose up -d postgres
bash scripts/init-db.sh
bash scripts/install-client.sh
bash scripts/dev.sh
```

Открыть `http://localhost:5080/`. Swagger: `http://localhost:5080/swagger` (`scripts/dev.sh` запускает Development).

Посмотреть [HTTP-примеры](requests/MuzLoto-API.http), [статус модулей](docs/STATUS.md) и [предположения по FormDesigner](docs/FORMDESIGNER.md).

После генерации initial migrations файлы `src/MuzLoto.Web/Data/Migrations/*.cs` нужно закоммитить. Для реальных данных необходимы тесты, проверка поставщика формы, HTTPS и аудит безопасности.

## Этап 3

[Что перенесено из старого Google Apps Script](docs/LEGACY_GOOGLE_SHEETS.md) · [Проверка и ограничения этапа 3](docs/STAGE3.md).


## Этап 4 — финансовые возвраты

При отмене оплаченного заказа создаётся запрос `Refund (Pending)`, а билеты освобождаются сразу.
Администратор отмечает реальное перечисление в [разделе «Возвраты»](/refunds).
В отчёте: валовые платежи, подтверждённые возвраты и чистые поступления.
Пока возврат не подтверждён, заказ можно восстановить в течение часа. После возврата — только новое бронирование и новая оплата.
Перед запуском новой версии **обязательно** выполните `bash scripts/init-db.sh` для создания и применения миграции `RefundWorkflow`.
Подробности: [docs/STAGE4.md](docs/STAGE4.md).

## Этап 5: сотрудники

В интерфейсе администратора добавлен раздел [«Сотрудники»](/staff): создание Admin/Manager/User, смена роли, назначение Manager на конкретные игры. Пользователь может сменить пароль по `/account/change-password`. Документация: [docs/STAGE5.md](docs/STAGE5.md).

## Этап 6a: регистрация и собственные заявки

Регистрация, публичная главная, `/bookings/mine`, `POST /api/auth/login`, 403 для запрещённых страниц, HTTP-тесты. После обновления выполните `bash scripts/init-db.sh` (миграция `BookingOwner`). Подробнее: [docs/STAGE6A.md](docs/STAGE6A.md).

## Этап 6b: политики доступа, CSRF, демо-данные

Политики `CanEditBooking` / `CanManageBooking`, CSRF в заголовке `X-CSRF-TOKEN`, демо-данные в Development. Клиентская форма остаётся в FormDesigner. Подробнее: [docs/STAGE6B.md](docs/STAGE6B.md).

## Этап 6c: SignalR

Обработчики подключения и отключения, учёт подключений, счётчик «Онлайн», отзыв групп при снятии назначения или смене роли, повторная проверка прав при переподключении. Подробнее: [docs/STAGE6C.md](docs/STAGE6C.md).

## Этап 7: полный REST API

Мероприятия v1/v2 (`remainingTickets`, `mapUrl` в v2), заявки с владельцем из токена, сводка продаж, Swagger с двумя версиями и описаниями. Подробнее: [docs/STAGE7.md](docs/STAGE7.md).

## Этап 8a: карточка игры

Полная карточка игры и несколько дат одной игры, обложки, архив, поиск и фильтры. После обновления: `bash scripts/init-db.sh` (миграция `SessionArchive`). Подробнее: [docs/STAGE8A.md](docs/STAGE8A.md).

## Этап 8b: заявки

Изменение количества (частичный возврат или доплата для оплаченных заказов), поиск заявок, история продаж, автоматический повтор транзакций при одновременной работе. После обновления: `bash scripts/init-db.sh` (миграция `PaidQuantityChanges`). Подробнее: [docs/STAGE8B.md](docs/STAGE8B.md).
