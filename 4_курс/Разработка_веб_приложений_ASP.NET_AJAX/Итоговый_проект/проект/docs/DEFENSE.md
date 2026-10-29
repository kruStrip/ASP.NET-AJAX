# Защита итогового проекта «МузЛото»

## 1. Тема (30 секунд)

«МузЛото» проводит музыкальные игры: клиенты бронируют места через форму на главной странице сайта, сотрудники вручную подтверждают
оплату. Раньше всё велось в Google Sheets. Система заменяет таблицу: учёт мест в PostgreSQL без перепродажи, роли
сотрудников, возвраты, отчёты и живые обновления для сотрудников.

Ключевое правило: **заявка не занимает места до подтверждения оплаты**; при завершении игры неоплаченные заявки удаляются,
оплаченные остаются в истории.

## 2. Какая страница за какую технологию отвечает

| Блок курса | Где показать | Что именно |
|---|---|---|
| **Razor Pages** | `/events` (список из БД, поиск/фильтры), `/events/{id}` (карточка), `/events/create`, `/events/{id}/edit`, `/account/register`, `/account/login`, `/reports`, `/staff` | серверный рендеринг через `@model`, Tag Helpers (`asp-for`, `asp-page`, `asp-route-id`), общий `_Layout` с меню по ролям |
| Серверная валидация | `/events/create`, `/account/register` | Data Annotations + `ModelState`, ошибки под полями, классический POST |
| **AJAX** | `/bookings/pending`, `/bookings` | подтверждение оплаты, изменение количества, отмена, завершение игры — `fetch` без перезагрузки, токен в `X-CSRF-TOKEN`, ошибки 400/401/403/409 показываются текстом |
| **REST API** | `/swagger` (v1 и v2), `requests/MuzLoto-API.http` | `api/v1|v2/events` — полный CRUD; `api/v1/bookings` — заявки владельца; `api/v1/reports/*`; DTO, 201+Location, 204, 400 ProblemDetails, 401, 403, 404, 409 |
| **SignalR** | две вкладки на `/bookings/pending` | хаб `BookingHub` `[Authorize]`, группы `session:{id}` по назначению, события из сервиса через `IHubContext`, переподключение, счётчик «Онлайн» |
| **EF Core** | `Data/AppDbContext.cs`, `Data/Migrations` | 10 бизнес-таблиц + Identity, 1:M, M:N `EventStaffAssignment` с составным ключом, индексы, Fluent API, seed |
| **Безопасность** | `/account/register`, `/staff` (403), `/bookings/{id}/edit` чужой (403), CSP | Identity, роли, политики `CanViewBooking/CanEditBooking/CanManageBooking`, JWT, CSRF, CSP и security headers |

## 3. Сценарий живой демонстрации (≈5 минут)

Подготовка: `bash scripts/dev.sh` на **чистой** базе (демо-данные создадутся сами). Окно 1 — обычное, окно 2 — приватное.

| Время | Действие | Что сказать |
|---|---|---|
| 0:00 | Окно 1: `/account/login` → `admin@muzloto.local` / `Admin2026!`. Открыть «Игры» | Данные пришли с сервера (Razor), поиск и фильтры — обычный GET |
| 0:30 | «Добавить игру», отправить пустую форму → ошибки; затем в описании `<script>alert('XSS')</script>`, сохранить | Серверная валидация. Скрипт показан текстом: Razor кодирует вывод, CSP запрещает inline-скрипты |
| 1:15 | Окно 2: `manager1@muzloto.local` / `Manager2026!` → «Подтверждение». В шапке обоих окон «Онлайн: 2» | Менеджер видит только назначенные игры. Счётчик — `OnConnectedAsync` |
| 1:45 | Окно 1: «Подтверждение» → «Подтвердить оплату» у «Меломанов». Окно 2: карточка исчезает сама | AJAX-POST с `X-CSRF-TOKEN` (показать в DevTools → Network). Сервер сохранил в БД и разослал событие через `IHubContext` только в группу этой игры |
| 2:30 | Окно 1: «Покупки» → у «Звёзд караоке» «Ожидает доплаты 5 → 7» → «Доплата получена» (у другой покупки можно уменьшить количество — появится возврат разницы) | Места занимаются только после подтверждения доплаты; уменьшение сразу освобождает места |
| 3:00 | `/swagger` → `POST /api/auth/login` под `user@muzloto.local` / `User2026!` → Authorize → `GET /api/v1/bookings` | JWT 30 минут; пользователь видит только свои заявки. `GET /api/v1/events` с этим токеном — 403, без токена — 401 |
| 3:45 | Переключить Swagger на v2 → `GET /api/v2/events` | v2 — другой контракт: `location`, `availability`, `remainingTickets`, `mapUrl` |
| 4:15 | Окно 2: выйти, войти как `user@muzloto.local` / `User2026!` → «Мои заявки» → скопировать ссылку «Изменить». Выйти, зарегистрировать нового пользователя на `/account/register`, открыть скопированную ссылку → «Доступ запрещён» | resource-based политика: заявка загружается из БД, владелец сравнивается с текущим пользователем — 403. Владелец ту же ссылку открывает |
| 4:40 | Окно 1 (админ), DevTools → Console: `fetch('/bookings/pending?handler=Confirm',{method:'POST',headers:{'X-Requested-With':'XMLHttpRequest'}}).then(r=>r.status)` | Без `X-CSRF-TOKEN` — **400** |

## 4. Ответы на вопросы защиты

1. **Где данные рендерятся на сервере? Почему Razor?** `/events` — `Pages/Events/Index.cshtml(.cs)`: запрос в `OnGetAsync`, вывод через `@model`. Список нужен сразу при открытии и должен работать без JS, индексироваться и открываться по ссылке.
2. **Где AJAX? Почему не SignalR? Где токен?** Подтверждение оплаты — `wwwroot/js/site.js`, `fetch` с заголовком `X-CSRF-TOKEN` (токен из `<meta name="csrf-token">` в `_Layout`). Это действие одного пользователя с ответом «получилось/ошибка» — классический запрос-ответ. SignalR — для оповещения **других**.
3. **Где SignalR? Почему не AJAX? Как защищён хаб?** `Hubs/BookingHub.cs`: изменения приходят от других сотрудников, опрашивать сервер AJAX-ом было бы и медленно, и дорого. `[Authorize(Roles = "Admin,Manager")]`; `JoinSession` проверяет назначение на игру; снятие назначения сразу убирает соединение из группы (`Hubs/HubAccessRevoker.cs`).
4. **Чем REST API отличается от AJAX-обработчиков?** API — отдельные контроллеры (`Controllers/*V1Controller.cs`) с DTO, версиями, JWT вместо cookie, документацией Swagger; их может вызывать любой клиент (curl, Postman, мобильное приложение). AJAX-обработчики — handlers Razor-страниц для нашего же интерфейса, cookie + CSRF. JWT передаётся в заголовке `Authorization: Bearer …`.
5. **Пользователь был офлайн в момент события — получит ли?** Нет, SignalR не хранит пропущенные сообщения. Поэтому после переподключения клиент заново входит в группы и **загружает свежий список** (`connection.onreconnected` → `refreshVisibleList`); источник истины — БД.
6. **Связь AJAX и SignalR?** AJAX → handler страницы → `BookingWorkflow` сохраняет в БД → после коммита `IHubContext<BookingHub>` рассылает событие группе игры → у других клиентов срабатывает обработчик и обновляет список.
7. **Зачем две версии API?** Изменение контракта без поломки существующих клиентов: v1 — плоские поля, v2 — вложенные `location`/`availability`, статус строкой, `remainingTickets`, `mapUrl`, пагинация с обёрткой.
8. **Миграция с таблицами, где индексы?** `Data/Migrations/20261019133346_InitialCreate.cs`. Индексы заданы в `Data/AppDbContext.cs`: `StartsAt` (сортировка), `Games.Title` (поиск), `(GameSessionId, Status)`, `CreatedAt`, `OwnerUserId`; уникальные: `(Provider, ExternalSubmissionId)` — защита от дублей заявок, и фильтрованные — один открытый возврат за отмену, одна ожидающая доплата на заказ.
9. **Seed: где и как проверить?** Роли и админ — `Data/IdentitySeeder.cs` (идемпотентно: проверка `RoleExistsAsync`/`FindByEmailAsync`). Демо-данные — `Data/DemoDataSeeder.cs`, только в Development и только на пустой таблице игр. Проверка — страницы приложения или `SELECT count(*)`.
10. **Fluent API: зачем эти настройки?** `HasPrecision(12, 2)` — деньги без ошибок округления; `OnDelete(Restrict)` у заявок и платежей — оплаченную историю нельзя удалить каскадом; `Cascade` у обложек и назначений — удаляются вместе с пустым сеансом; `HasMaxLength` — ограничения строк в БД; составной ключ `EventStaffAssignment`.
11. **`.Include()` и SQL?** `Pages/Bookings/BookingListBase.cs`: `Include(x => x.GameSession).ThenInclude(x => x.Game)` → один SQL с `INNER JOIN GameSessions` и `INNER JOIN Games` вместо отдельного запроса на каждую заявку (N+1).
12. **M:N связь?** `EventStaffAssignment(UserId, GameSessionId)` — явная связующая сущность с составным PK, навигации `AppUser.EventAssignments` и `GameSession.Staff`; в БД — таблица `EventStaffAssignments` с двумя внешними ключами.
13. **Custom-политика?** `Services/BookingAuthorization.cs`: `BookingAuthorizationHandler` получает **загруженную из БД** заявку, сравнивает `OwnerUserId` с ID из claims; Admin и назначенный менеджер проходят всегда. Вызов — `IAuthorizationService.AuthorizeAsync(User, booking, "CanEditBooking")` в `Pages/Bookings/Edit.cshtml.cs`; кнопка «Изменить» в `/bookings/mine` показывается через ту же политику.
14. **Изменить payload JWT, оставив подпись?** Подпись HMAC-SHA256 считается от заголовка и payload; при проверке не совпадёт → 401. Плюс при каждом запросе сверяются security stamp и роли с БД (`Program.cs`, `OnTokenValidated`) — смена роли или пароля отзывает старые токены.
15. **Почему токен в заголовке, а не в теле?** Заголовок нельзя выставить кросс-доменной HTML-формой, и его не нужно смешивать с данными формы/JSON; сервер настроен читать именно его (`AddAntiforgery(o => o.HeaderName = "X-CSRF-TOKEN")`).
16. **Что блокирует CSP и почему этого мало?** `script-src 'self'; style-src 'self'` — запрещены inline-скрипты и стили и сторонние источники (`Program.cs`, middleware заголовков). Это вторая линия: XSS через собственные скрипты, DOM-манипуляции или опасные ссылки CSP не закрывает — основная защита в кодировании вывода Razor и проверке ссылок (`LinkRules.IsHttpUrl` — `javascript:` не становится ссылкой).
17. **Почему logout — POST?** GET можно вызвать картинкой или ссылкой на чужом сайте и разлогинить пользователя; POST требует CSRF-токен (`Pages/Account/Logout.cshtml.cs`, кнопка в `_Layout` — форма).
18. **Перезапуск сервера без persistent-ключей?** Ключи Data Protection были бы новыми → cookie входа и антифоргери-токены перестали бы расшифровываться → все разлогинены, открытые формы дают 400. В проекте ключи сохраняются: `AddDataProtection().PersistKeysToFileSystem(...)` (`App_Data/keys`, настраивается `DataProtection:KeysPath`).
19. **`PasswordSignInAsync` и `CheckPasswordSignInAsync`?** Первый проверяет пароль **и выдаёт cookie** — страница входа (`Pages/Account/Login.cshtml.cs`). Второй только проверяет пароль и блокировку, cookie не создаёт — API выдаёт JWT (`Controllers/AuthController.cs`). Оба учитывают блокировку после 5 ошибок на 15 минут.
20. **Пользователь открывает чужую задачу?** `/bookings/{id}/edit` чужой заявки: обработчик загружает заявку, политика отказывает → `Forbid()` → `/account/denied` со статусом **403**. В API `GET /api/v1/bookings/{id}` чужой заявки — 403, без токена — 401.

## 5. Чек-лист перед защитой

- [ ] `docker compose up -d postgres`, чистая БД: `bash scripts/init-db.sh`, затем `bash scripts/dev.sh`.
- [ ] `/swagger` открывается, оба документа v1 и v2.
- [ ] Вход под тремя демо-учётками (README).
- [ ] Два окна (обычное + приватное) для SignalR.
- [ ] `dotnet test` с `MUZLOTO_PG_TEST=1` на отдельной тестовой БД — все тесты зелёные.
- [ ] Открыт `requests/MuzLoto-API.http` в IDE на случай вопросов по API.
