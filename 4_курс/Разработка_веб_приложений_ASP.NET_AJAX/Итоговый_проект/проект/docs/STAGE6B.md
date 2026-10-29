# Этап 6b — политики доступа, CSRF, демо-данные

Клиентская форма остаётся в FormDesigner (см. [DECISIONS.md](DECISIONS.md)).

## Resource-based авторизация

`Services/BookingAuthorization.cs`: `BookingAuthorizationHandler` + политики

| Политика | Кому разрешено |
|---|---|
| `CanEditBooking` | владелец неоплаченной заявки, Admin, Manager, назначенный на игру |
| `CanManageBooking` | Admin, Manager, назначенный на игру (подтвердить, отменить, восстановить) |

`IAuthorizationService.AuthorizeAsync(User, booking, policy)` — на `/bookings/{id}/edit` (изменение неоплаченной заявки:
команда, имя, Telegram, количество в пределах остатка), в обработчиках «Нужно подтвердить», «Покупки», «Отменённые»;
кнопка «Изменить» в «Мои заявки» показывается только при успехе политики. Чужая заявка → 403.
`StaffAccess` используется внутри обработчика — проверка назначения не дублируется.

## CSRF

- Глобальный `AutoValidateAntiforgeryTokenAttribute` для Razor Pages и контроллеров.
- `<meta name="csrf-token">` в layout; `site.js` передаёт токен **только** в заголовке `X-CSRF-TOKEN`
  (сервер: `AddAntiforgery(o => o.HeaderName = "X-CSRF-TOKEN")`).
- JWT-контроллеры и адаптер FormDesigner помечены `[IgnoreAntiforgeryToken]`: у них нет cookie-аутентификации, CSRF неприменим.

## Демо-данные (только Development)

`Data/DemoDataSeeder.cs`, включается `Seed:DemoData` в `appsettings.Development.json`, срабатывает только на пустой таблице игр:
3 игры, 5 сеансов, 4 обложки, 3 назначения, 9 заявок, 6 платежей, 4 возврата, 3 изменения количества (с этапа 10), история статусов, ключи заявок FormDesigner.
Все данные вымышленные.

| Роль | E-mail | Пароль |
|---|---|---|
| Admin | `admin@muzloto.local` | `Admin2026!` |
| Manager | `manager1@muzloto.local`, `manager2@muzloto.local` | `Manager2026!` |
| User | `user@muzloto.local` | `User2026!` |

## Прочее

- Количество билетов в заявке FormDesigner ограничено остатком мест, а не 1–10.
- Деньги и даты форматируются в `ru-RU` независимо от локали сервера.

## Проверка

35 тестов (16 unit + 19 интеграционных): владелец меняет свою заявку, чужой пользователь получает 403,
AJAX без `X-CSRF-TOKEN` → 400 и с ним → 200, JWT-API не блокируется антифоргери.
Seed проверен на чистой БД: первый запуск создаёт данные, второй — не дублирует.
