# SecureTodo

Учебный безопасный Todo-менеджер на ASP.NET Core 8, Razor Pages, EF Core SQLite и Identity.

## Запуск

```bash
dotnet restore
dotnet run
```

Откройте `http://localhost:5187`. Swagger доступен в Development-режиме по адресу
`http://localhost:5187/swagger`.

Seed-администратор:

- e-mail: `admin@todo.com`
- пароль: `Admin123!`

Это демонстрационные реквизиты. Перед реальным развёртыванием замените пароль и перенесите
JWT-ключ из `appsettings.json` в Secret Manager или переменные окружения.

## Реализовано

- регистрация, вход, POST-выход, серверная валидация и блокировка после пяти ошибок;
- роли `Admin`, `Author`, `User`, seed и управление ролями;
- CRUD задач и resource-based политика `CanEditTask`;
- Cookie для Razor Pages и JWT Bearer для REST API;
- 15-минутные access-токены, 7-дневные refresh-токены, ротация и отзыв;
- Swagger Bearer authorization и примеры запросов в `SecureTodo.http`;
- глобальная antiforgery-проверка, AJAX с `X-CSRF-TOKEN`, Razor encoding и CSP;
- security headers и authenticator-based 2FA с QR-кодом.

Материалы для сдачи:

- [подробный отчёт](docs/report.md);
- [пошаговая шпаргалка для защиты](docs/defense-script.md).
