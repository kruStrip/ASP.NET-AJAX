# TaskBoard — практическая работа (ASP.NET Core MVC)

## Как запустить

SDK .NET 8 установлен локально в `~/.dotnet` (в системе был только runtime 10).

```bash
export DOTNET_ROOT=$HOME/.dotnet
export PATH=$HOME/.dotnet:$PATH

cd ~/dotnet-lessons/TaskBoard
dotnet run
```

Приложение слушает `http://localhost:5000` (профиль `http` в `Properties/launchSettings.json`).

> На этой машине порт 5000 уже занят другим процессом (не нашим).
> Если `dotnet run` падает с `address already in use`, запускайте на другом порту:
> ```bash
> ASPNETCORE_URLS=http://localhost:5080 dotnet run --no-launch-profile
> ```

Чтобы `dotnet` находился всегда, добавьте две строки `export` в `~/.bashrc`.

## Что где лежит

| Шаг | Файл |
|-----|------|
| 1, 2 — middleware (заголовок, логи, `/health`, замер времени) | `Program.cs` |
| 3 — сервис и DI | `Services/ITaskService.cs`, `Services/InMemoryTaskService.cs`, `Program.cs` |
| 4 — контроллер и маршрутизация | `Controllers/TasksController.cs`, `Program.cs` |
| 5 — представления | `Views/Tasks/*.cshtml`, `Views/Shared/_Layout.cshtml` |
| 6 — JSON API | `Controllers/TasksController.cs` (методы `Api*`) |
| Уровень 1 — счётчик | `TasksController.Index` (ViewBag) + `Views/Tasks/Index.cshtml` |
| Уровень 2 — проверка `X-Api-Key` | `Program.cs` (4-й middleware) |
| Уровень 3 — фильтр `?status=` | `TasksController.Index/Done/Delete` + `Views/Tasks/Index.cshtml` |

## Проверка через curl

JSON API закрыт ключом (Уровень 2), поэтому нужен заголовок `X-Api-Key: secret123`:

```bash
B=http://localhost:5000

# 401 без ключа
curl -i $B/tasks/api/list

# список
curl -H 'X-Api-Key: secret123' $B/tasks/api/list

# создание
curl -X POST $B/tasks/api/create \
  -H 'X-Api-Key: secret123' \
  -H 'Content-Type: application/json' \
  -d '{"title":"Тест через curl","description":"Создано из терминала"}'

# по id
curl -H 'X-Api-Key: secret123' $B/tasks/api/1

# короткое замыкание конвейера
curl $B/health        # -> healthy
```

## Ответы на вопросы из методички

**Почему сервис Singleton, а не Scoped?**
Список задач хранится в поле `_tasks` самого объекта `InMemoryTaskService`. Singleton — один
экземпляр на всё приложение, поэтому список живёт между запросами. При `AddScoped` на каждый
HTTP-запрос создавался бы новый сервис с пустым списком: задача, созданная в POST-запросе,
исчезала бы при редиректе на `/tasks`. Проверено — со Scoped счётчик показывает «Всего задач: 0»
сразу после создания задачи. При перезапуске приложения все задачи теряются в любом случае:
данные только в оперативной памяти, БД нет.

**Почему middleware замера времени стоит до логирующего?**
Каждый middleware измеряет только то, что находится дальше него по конвейеру. Стоя первым, он
охватывает всю обработку целиком, включая логирование, маршрутизацию и рендеринг View. Если
поставить его после логирующего — время всё ещё будет считаться, но уже без учёта работы
вышестоящих middleware, то есть цифра будет заниженной и неполной.

**Про запись заголовка после `await next()`**
Код из методички (`sw.Stop(); context.Response.Headers.Append(...)` после `await next(context)`)
падает с `InvalidOperationException: Headers are read-only, response has already started` —
к моменту возврата из `next()` тело ответа уже начало уходить клиенту, а заголовки отправляются
раньше тела. Проверено: страница отдаётся, но в консоли — исключение, и заголовка нет.
Поэтому в `Program.cs` заголовок пишется из колбэка `Response.OnStarting`, который выполняется
в последний момент перед отправкой заголовков.
