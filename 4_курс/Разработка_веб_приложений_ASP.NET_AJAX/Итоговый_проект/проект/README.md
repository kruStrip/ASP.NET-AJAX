# МузЛото — информационная система мероприятий

**Текущее состояние: этап 6a.** ASP.NET Core 8, PostgreSQL, Razor Pages, Identity (cookie + JWT), REST API v1/v2, AJAX, SignalR.
Сборка, unit- и интеграционные тесты на PostgreSQL проходят в GitHub Actions. Подробный статус — [docs/STATUS.md](docs/STATUS.md),
изменения ТЗ — [docs/DECISIONS.md](docs/DECISIONS.md).

## Что реализовано

- Регистрация (`/account/register`, роль User), вход, выход (POST), смена пароля, блокировка на 15 минут после 5 ошибок.
- Роли Admin / Manager / User; сотрудники и назначения менеджеров на игры (`/staff`).
- Игры и сеансы, заявки «Нужно подтвердить», подтверждение оплаты (SERIALIZABLE, без перепродажи), отмена и восстановление в течение часа.
- Завершение игры: неоплаченные заявки удаляются, оплаченные сохраняются. Ручные возвраты и чистая выручка.
- «Мои заявки» (`/bookings/mine`) — только заявки текущего пользователя.
- JWT `POST /api/auth/login`, REST CRUD сеансов `api/v1/sessions` и `api/v2/sessions`, Swagger `/swagger` в Development.
- AJAX-действия с частичным обновлением, SignalR-уведомления по группам игр с переподключением.

## Учётные данные для локальной разработки

| Роль | E-mail | Пароль |
|---|---|---|
| Admin (seed) | `admin@muzloto.local` | `Admin2026!` |

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
