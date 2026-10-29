# МузЛото — система мероприятий и продажи билетов

Веб-приложение для компании, проводящей музыкальные игры. Заменяет Google Sheets: расписание игр, учёт мест без
перепродажи, ручное подтверждение оплат, возвраты, роли сотрудников, отчёты и обновления в реальном времени.
Клиенты по-прежнему оформляют заявки в форме FormDesigner.

**Стек:** ASP.NET Core 8 · Razor Pages · AJAX (Fetch) · REST API + JWT · SignalR · EF Core 8 + PostgreSQL 16 · ASP.NET Core Identity.

Главные правила: **заявка не занимает места до подтверждения оплаты сотрудником**; подтверждение атомарно проверяет
остаток (без перепродажи при одновременной работе); при завершении игры неоплаченные заявки удаляются, оплаченные
остаются в истории.

## Быстрый старт

Нужны .NET SDK 8, Docker, Node.js (для браузерного клиента SignalR).

```bash
cp .env.example .env              # заполните значения REPLACE_...
docker compose up -d postgres     # PostgreSQL на 127.0.0.1:5432
bash scripts/init-db.sh           # dotnet ef database update — все таблицы из миграций
bash scripts/install-client.sh    # копирует signalr.min.js в wwwroot/lib (без CDN — строгий CSP)
bash scripts/dev.sh               # http://localhost:5080, окружение Development
```

- Приложение: <http://localhost:5080/> · Swagger: <http://localhost:5080/swagger> (документы v1 и v2, кнопка Authorize для JWT).
- База разворачивается одной командой `dotnet ef database update` (её выполняет `init-db.sh`); при старте приложение
  тоже применяет недостающие миграции.
- При первом запуске в Development создаются роли, администратор и вымышленные демо-данные (3–5 записей в каждой таблице).

## Учётные записи (только локальная разработка)

| Роль | E-mail | Пароль | Что может |
|---|---|---|---|
| Admin | `admin@muzloto.local` | `Admin2026!` | всё: игры, сотрудники, возвраты, отчёты |
| Manager | `manager1@muzloto.local`, `manager2@muzloto.local` | `Manager2026!` | заявки и продажи только назначенных игр |
| User | `user@muzloto.local` | `User2026!` | свои заявки (`/bookings/mine`, API) |

Учётные записи создаются только в окружении Development; пароль администратора вне Development запрещён кодом.
Если в `.env` заданы `MUZLOTO_ADMIN_EMAIL` и `MUZLOTO_ADMIN_PASSWORD`, используется этот администратор.
Новых пользователей можно зарегистрировать на `/account/register` (роль User).

## Возможности

**Сотрудникам (Razor Pages + AJAX + SignalR)**
- «Нужно подтвердить», «Покупки», «Отменённые» — подтверждение оплаты, отмена, восстановление в течение часа,
  изменение количества (для оплаченных заказов — частичный возврат или ожидание доплаты); поиск по телефону в любом
  формате, команде, имени, ID. Всё без перезагрузки, другие сотрудники видят изменения сразу.
- «Игры» — полная карточка (5 абзацев описания, 2ГИС, продолжительность, возраст, обложка), несколько дат одной игры,
  архив, поиск и фильтры; «Завершить» — закрытие продаж и удаление неоплаченных заявок.
- «Возвраты» — фиксация фактического перевода денег; «Отчёт» — сводка, продажи по играм и датам за период,
  оперативный отчёт; «История продаж» — все оплаченные заказы за всё время.
- «Сотрудники» (Admin) — учётные записи, роли, назначение менеджеров на игры; отзыв назначения сразу закрывает доступ
  к живым обновлениям этой игры.

**REST API (JWT)** — `POST /api/auth/login`; `api/v1|v2/events` (полный CRUD, две версии контракта);
`api/v1/bookings` (пользователь видит и меняет только свои заявки); `api/v1/reports/summary|by-game|by-date|operational`.
Запросы ко всем эндпоинтам — [requests/MuzLoto-API.http](requests/MuzLoto-API.http).

**Интеграция** — `POST /api/integrations/formdesigner/submissions` с секретом `X-Integration-Key` и защитой от
повторной доставки. Реальный формат и способ доставки заявок FormDesigner ещё не проверены — см. [docs/FORMDESIGNER.md](docs/FORMDESIGNER.md).

## Безопасность

Identity (пароль от 6 символов с цифрой, блокировка после 5 ошибок на 15 минут) · роли Admin/Manager/User ·
resource-based политики `CanViewBooking`, `CanEditBooking`, `CanManageBooking` через `IAuthorizationService` ·
cookie для страниц, JWT (30 минут, проверка security stamp) для API · глобальный `AutoValidateAntiforgeryToken`,
токен AJAX в заголовке `X-CSRF-TOKEN` · CSP без inline-скриптов и стилей, `X-Content-Type-Options`, `X-Frame-Options`,
`Referrer-Policy`, HSTS вне Development · ключи Data Protection сохраняются между перезапусками.

## Тесты

```bash
dotnet test tests/MuzLoto.Tests/MuzLoto.Tests.csproj --filter 'Category!=Integration'   # unit
# интеграционные: отдельная пустая БД PostgreSQL (не muzloto_dev!)
MUZLOTO_PG_TEST=1 ConnectionStrings__Default='Host=localhost;Database=muzloto_test;Username=muzloto;Password=...' \
Jwt__Key="$(openssl rand -hex 48)" dotnet test tests/MuzLoto.Tests/MuzLoto.Tests.csproj
```

HTTP-тесты поднимают приложение целиком (`WebApplicationFactory`): регистрация и блокировка, 401/403, CSRF (400 без
заголовка), API с настоящими JWT, политики владельца, SignalR-подключения с группами и отзывом доступа, одновременное
подтверждение последних мест, XSS, отчёты. GitHub Actions выполняет сборку, тесты на временном PostgreSQL и HTTP smoke.

## Документация

- [docs/DEFENSE.md](docs/DEFENSE.md) — сценарий демонстрации и ответы на вопросы защиты.
- [docs/STATUS.md](docs/STATUS.md) — что сделано и проверено по этапам, что осталось.
- [docs/DECISIONS.md](docs/DECISIONS.md) — уточнения к ТЗ, принятые по ходу разработки.
- `docs/STAGE*.md` — подробности каждого этапа; [docs/architecture.md](docs/architecture.md); [docs/LEGACY_GOOGLE_SHEETS.md](docs/LEGACY_GOOGLE_SHEETS.md).

Перед работой с реальными персональными данными и оплатами нужны: проверка интеграции FormDesigner, HTTPS,
резервные копии БД и хранение секретов вне `.env` на сервере.
