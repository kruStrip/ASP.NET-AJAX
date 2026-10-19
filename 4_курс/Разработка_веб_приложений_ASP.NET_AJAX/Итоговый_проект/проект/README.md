# МузЛото — информационная система мероприятий

**Этап 1: исходный код пилотного вертикального сценария, ещё без проверенной сборки.** До эксплуатации с реальными клиентами необходимы сборка, интеграционные тесты, аудит доступа и проверка FormDesigner.

## Стек

ASP.NET Core 8, Razor Pages, ASP.NET Core Identity, EF Core + PostgreSQL, SignalR Hub, CSS/JavaScript. Клиентская форма FormDesigner не заменяется.

## Что реализовано в первом этапе

- Вход сотрудника и роли Admin, Manager, User.
- Добавление и изменение сеансов игр; расчёт вместимости.
- Список поступивших заявок, ручное подтверждение, отмена, завершение игры.
- Удаление Pending после завершения, сохранение оплаченных записей.
- Canonical JSON приём заявок с секретным заголовком и уникальным submission ID.
- Статус Pending не резервирует места. Подтверждение проверяет доступные места в Serializable-транзакции.
- Сводка выручки и оплаченных билетов; защищённый SignalR Hub.

**Важно:** SignalR-клиент, AJAX-действия, JWT REST API, версионирование, уведомления, импорт и полноценные отчёты — последующие этапы. См. [docs/STATUS.md](docs/STATUS.md).

## Локальный запуск (Linux)

Нужно установить **.NET 8 SDK**, Docker и Docker Compose. В каталоге проекта:

```bash
cp .env.example .env
# Откройте .env и замените ВСЕ значения REPLACE_...
docker compose up -d postgres
bash scripts/init-db.sh
bash scripts/dev.sh
```

Откройте `http://localhost:5080/account/login` (или `/dashboard`, если вошли). Администратор создаётся из `MUZLOTO_ADMIN_EMAIL` / `MUZLOTO_ADMIN_PASSWORD`; секреты нельзя коммитить. Если локальный порт PostgreSQL 5432 занят, измените Docker Compose и ConnectionString в `scripts/common.sh`.

**Миграции**: поскольку среда подготовки не содержала SDK, первой командой нужно сгенерировать InitialCreate из актуальной EF Core модели. После этого **добавьте созданные `Data/Migrations/*.cs` в Git**, чтобы дальнейшее создание базы было воспроизводимо.

## Проверка

```bash
dotnet build src/MuzLoto.Web/MuzLoto.Web.csproj
dotnet test tests/MuzLoto.Tests/MuzLoto.Tests.csproj
```

Для тестового входящего запроса см. [requests/MuzLoto.http](requests/MuzLoto.http), а для предпосылок FormDesigner — [docs/FORMDESIGNER.md](docs/FORMDESIGNER.md).

## Политика данных

Это **публичный учебный репозиторий**. Нельзя добавлять реальные заявки клиентов, телефоны, credentials, историю транзакций или production-экспорты. `.env`, `appsettings.Production.json` и локальные данные исключены из Git.

## Дальнейшая работа

В следующем цикле — JWT API v1/v2 со Swagger, AJAX-обновления и SignalR-клиент, адаптер реального payload FormDesigner, управление сотрудниками и тесты на PostgreSQL.
