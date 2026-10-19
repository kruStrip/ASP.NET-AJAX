# МузЛото — информационная система мероприятий

**Этап 2: ASP.NET Core 8, PostgreSQL, Razor Pages, JWT REST API v1/v2, AJAX, SignalR.**

Разработка идёт по единому техническому заданию. Существующая клиентская форма FormDesigner **остаётся**; система принимает её заявки через отдельный адаптер (реальное сопоставление полей ещё не проверено).

## Что реализовано в исходниках

- Cookie/Identity для сотрудников Admin, Manager, User; вход через Razor Pages.
- Создание и управление сеансами, список ожидающих и подтверждённых заявок.
- Подтверждение оплаты с SERIALIZABLE-транзакцией; неоплаченные заявки не занимают места.
- Завершение игры удаляет неоплаченные заявки; подтверждённые сохраняются.
- JWT `POST /api/auth/token`, REST CRUD сеансов `api/v1/sessions` и `api/v2/sessions`, Swagger в Development.
- AJAX-формы подтверждения, отмены и завершения; клиент SignalR с группами сеансов и переподключением.
- Unit-тесты и отдельный интеграционный тест на временном PostgreSQL; GitHub Actions для сборки, миграций и HTTP smoke.

**Текущий код не проходил локальную компиляцию в среде подготовки** (нет .NET SDK и доступа к Docker). Перед продолжением нужна успешная проверка CI для второго этапа; инструкции — [docs/STAGE2.md](docs/STAGE2.md).

## Локальный запуск

```bash
cp .env.example .env
# Поменяйте все значения REPLACE_...
docker compose up -d postgres
bash scripts/init-db.sh
bash scripts/install-client.sh
bash scripts/dev.sh
```

Открыть `http://localhost:5080/account/login`. Swagger (в Development): `http://localhost:5080/swagger`.

Посмотреть [HTTP-примеры](requests/MuzLoto-API.http), [статус модулей](docs/STATUS.md) и [предположения по FormDesigner](docs/FORMDESIGNER.md).

После генерации initial migrations файлы `src/MuzLoto.Web/Data/Migrations/*.cs` нужно закоммитить. Для реальных данных необходимы тесты, проверка поставщика формы, HTTPS и аудит безопасности.
