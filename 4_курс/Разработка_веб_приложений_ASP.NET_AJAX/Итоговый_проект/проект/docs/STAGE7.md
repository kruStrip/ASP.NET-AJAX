# Этап 7 — полный REST API

Все бизнес-эндпоинты — отдельные Web API-контроллеры с DTO и JWT (`[Authorize]` со схемой Bearer).
Токен: `POST /api/auth/login` (`/api/auth/token` оставлен для совместимости). Запросы ко всем эндпоинтам — [requests/MuzLoto-API.http](../requests/MuzLoto-API.http).

## Эндпоинты

| Метод | Путь | Кто | Ответы |
|---|---|---|---|
| POST | `/api/auth/login` | все | 200 токен, 401 |
| GET | `/api/v1/events`, `/api/v2/events` | Admin (все), Manager (назначенные) | 200, 401, 403 |
| GET | `/api/v{1,2}/events/{id}` | то же | 200, 404 |
| POST | `/api/v{1,2}/events` | Admin | 201 + Location, 400 ProblemDetails, 403, 404 |
| PUT | `/api/v{1,2}/events/{id}` | Admin | 200, 400, 404, 409 (вместимость < оплаченных) |
| DELETE | `/api/v{1,2}/events/{id}` | Admin | 204, 404, 409 (есть заявки); обложки и назначения — каскадно |
| GET | `/api/v1/bookings?status=&page=&pageSize=` | User — свои, Manager — свои и назначенных игр, Admin — все | 200 |
| GET | `/api/v1/bookings/{id}` | политика `CanViewBooking` | 200, 403 чужая, 404 |
| POST | `/api/v1/bookings` (+ `Idempotency-Key`) | любой вошедший; владелец = `sub` токена | 201 + Location, 200 повтор, 400, 404, 409 (мест меньше) |
| PUT | `/api/v1/bookings/{id}` | политика `CanEditBooking` (владелец неоплаченной, Admin, назначенный Manager) | 200, 400, 403, 404, 409 оплаченная |
| GET | `/api/v1/reports/summary` | Admin (все), Manager (назначенные) | 200, 403 для User |

## Версии

- **v1** — плоский контракт: `venue`, `timeZoneId`, `capacity`, `reserved`, `salesOpen`, `completed`.
- **v2** — изменённый контракт: вложенные `location` и `availability`, статус строкой (`open`/`closed`/`completed`),
  **`remainingTickets`** и **`mapUrl`** (только http/https), поиск и пагинация с обёрткой `{ page, pageSize, items }`.
  PUT в v1 не стирает `mapUrl`, заданный через v2.
- Swagger: два документа `/swagger/v1/swagger.json` и `/swagger/v2/swagger.json`, описания из XML-комментариев, кнопка Authorize для JWT.

## Сводка продаж

`Services/SalesReport.cs` — единый расчёт для `/reports` и API (страница и API не расходятся):
продано билетов, подтверждённых заказов, подтверждённая выручка, **средний чек = подтверждённая выручка / число
подтверждённых заказов**, **среднее число билетов на заказ**, ожидающие заявки, валовые платежи, возвраты, чистые поступления.
Неоплаченные заявки продажами не считаются.

## Исправлено попутно

- `PUT /api/v{n}/sessions/{id}` всегда отвечал **500** (EF Core 8 запрещает `IsLoaded = false` у загруженной навигации) — с этапа 2.
- Swagger UI отвечал **500**: одинаковые имена схем `ReadDto`/`WriteDto` в разных контроллерах.

## Проверка

45 тестов (16 unit + 29 интеграционных), из них 6 новых HTTP-тестов API с настоящими JWT: полный CRUD и коды ответов,
контракт v2, владелец из токена (подмена `ownerUserId` в теле не работает), чужая заявка — 403,
`Idempotency-Key`, область видимости менеджера, отчёт считает только подтверждённые продажи, оба документа Swagger.
