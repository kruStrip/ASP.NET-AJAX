# Практическая работа: безопасность API

## Цель

Добавить в `TaskFlowApi` аутентификацию пользователей, авторизацию по ролям и правам на конкретный ресурс, а также базовую защиту HTTP-ответов.

## Реализация

### Аутентификация

В API добавлен контроллер `AuthController`.

- `POST /api/auth/register` регистрирует пользователя.
- Пароль не сохраняется открытым текстом: применяется `PasswordHasher<AppUser>`.
- `POST /api/auth/login` проверяет пароль и возвращает JWT сроком действия один час.
- JWT содержит идентификатор, имя, e-mail и роль пользователя.
- `GET /api/auth/me` доступен только с валидным заголовком `Authorization: Bearer <token>` и возвращает claims текущего пользователя.

Настройка проверки JWT находится в `Program.cs`. Проверяются issuer, audience, срок действия токена и ключ подписи. В production значения `Jwt__Key`, `Jwt__Issuer` и `Jwt__Audience` должны передаваться через безопасную конфигурацию, а не храниться в репозитории.

### Авторизация

Используются роли `User`, `Author` и `Admin`.

| Операция | Требование |
| --- | --- |
| Чтение задач и проектов | Открытый доступ |
| Регистрация и вход | Открытый доступ |
| Создание или изменение проекта | `Author` или `Admin` |
| Создание задачи | `Author` или `Admin` |
| Изменение статуса или полное изменение задачи | Автор задачи или `Admin` |
| Удаление задачи или проекта | Только `Admin` |

Для изменения задачи реализована resource-based policy `CanEditTask`. При создании сервер записывает идентификатор текущего пользователя в `CreatedByUserId`; клиент не может подменить владельца через тело запроса. Обработчик `CanEditTaskHandler` разрешает действие только автору задачи либо администратору.

### Защита ответов

Каждый ответ API получает следующие заголовки:

- `Content-Security-Policy: default-src 'self'; frame-ancestors 'none'; base-uri 'self'`;
- `X-Content-Type-Options: nosniff`;
- `X-Frame-Options: DENY`;
- `Referrer-Policy: strict-origin-when-cross-origin`.

Вне окружения Development дополнительно включается HSTS. Для API применяется JWT в заголовке `Authorization`, поэтому cookie-аутентификация и связанные с ней CSRF-токены не используются.

## Изменения данных

Миграция `20261005120000_AddSecurityOwnership` добавляет:

- поле `Role` в таблицу `Users`;
- поле `CreatedByUserId` и индекс в таблицу `Tasks`.

Начальный пользователь `demo` получает роль `Admin`. Регистрация через API всегда назначает только роль `User`; повышенные роли не выдаются из публичного endpoint.

## Проверка

Проверка проведена локально на `http://127.0.0.1:5278`.

| Проверка | Ожидаемый результат | Фактический результат |
| --- | --- | --- |
| `dotnet build TaskFlowApi/TaskFlowApi.csproj --no-restore` | Успешная сборка | Успешно, 0 предупреждений и 0 ошибок |
| Применение миграции | Новые поля в SQLite | Успешно |
| `POST /api/auth/register` | `201 Created` | `201 Created` |
| `POST /api/auth/login` | JWT в ответе | `200 OK` |
| `GET /api/auth/me` без токена | Отказ | `401 Unauthorized` |
| `GET /api/auth/me` с JWT | Claims пользователя | `200 OK` |
| Создание задачи ролью `User` | Отказ по policy | `403 Forbidden` |
| Проверка HTTP-заголовков | CSP, nosniff, DENY, Referrer Policy | Все присутствуют |

Тестовая учётная запись после проверки удалена.

## Файлы работы

- `Program.cs` — JWT, policy, middleware и security headers.
- `Controllers/AuthController.cs` — регистрация, вход и просмотр claims.
- `Authorization/CanEditTaskRequirement.cs` и `Authorization/CanEditTaskHandler.cs` — проверка права на конкретную задачу.
- `Controllers/TasksV2Controller.cs` и `Controllers/ProjectsController.cs` — защита действий атрибутами `[Authorize]`.
- `Migrations/20261005120000_AddSecurityOwnership.cs` — изменение схемы данных.
- `TaskFlowApi.http` — примеры запросов для ручной проверки.
