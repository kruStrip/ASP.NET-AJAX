# Развёртывание: https://muzloto.b-o-c-a.ru

Приложение работает на общем сервере вместе с проектом calendar (`b-o-c-a.ru`) и не меняет его настроек.

## Как устроено

| Часть | Где | Примечание |
|---|---|---|
| Приложение | контейнер `muzloto-app-1` (образ `muzloto-web:<коммит>`), Production | память ≤ 320 МБ; ключи Data Protection и обложки — том `muzloto_appdata` (`/data`) |
| База | контейнер `muzloto-db-1`, PostgreSQL 16 | **своя**, доступна только приложению (сеть `muzloto_internal`); том `muzloto_pgdata` |
| Конфигурация | `/home/BocA/muzloto/docker-compose.yml` (из `deploy/docker-compose.prod.yml`) | |
| Секреты | `/home/BocA/muzloto/.env` (права 600) | созданы на сервере при первом развёртывании, в репозиторий не попадают |
| Прокси и HTTPS | nginx проекта calendar, файл `nginx/conf.d/muzloto.conf` (из `deploy/nginx/muzloto.conf`) | приложение подключено к сети `deploy_calendar-network` под именем `muzloto-app`; адрес разрешается при запросе, поэтому остановка МузЛото не мешает nginx |
| Сертификат | Let's Encrypt, `muzloto.b-o-c-a.ru` | продлевает контейнер `calendar-certbot` (каждые 12 ч) |
| Резервные копии | cron 03:30 → `/home/BocA/muzloto/backups/muzloto-ГГГГММДД.sql.gz`, 14 дней | |

Приложение доверяет заголовкам `X-Forwarded-*` только при `ForwardedHeaders:Enabled=true` (задано в compose):
порт контейнера не опубликован наружу, к нему ходит только nginx.

## Обновление: CI/CD

Каждый push в `main`, затрагивающий проект, запускает `.github/workflows/muzloto.yml`:

1. **build** — сборка, unit-тесты, миграции на временной PostgreSQL, интеграционные тесты, HTTP smoke.
2. **deploy** (только push в `main`, только если build зелёный, окружение `production`):
   образ собирается в Actions и публикуется в `ghcr.io/krustrip/muzloto-web:<sha>`; затем по SSH вызывается
   `deploy <sha>`. Сервер сам скачивает образ и compose-файл этого коммита, перезапускает приложение
   (миграции применяются при старте) и проверяет здоровье; **если новая версия нездорова — автоматически
   возвращает предыдущую**. Последний шаг — проверка https://muzloto.b-o-c-a.ru снаружи.

Безопасность: репозиторий публичный, поэтому
- секреты (`DEPLOY_SSH_KEY`, `DEPLOY_KNOWN_HOSTS`) лежат в окружении `production`, доступном только ветке `main`;
  pull request из форков их не получают;
- ключ деплоя отдельный и ограничен в `authorized_keys` одной командой (`restrict,command="…/ci-deploy.sh"`):
  только «развернуть образ этого коммита», без оболочки и проброса портов;
- сервер проверяет формат коммита, деплои не идут параллельно (`flock`).

Первичная настройка (один раз, владельцем репозитория): `bash scripts/setup-cd.sh`. После первого деплоя пакет
`muzloto-web` в GHCR должен быть **публичным** (как образы calendar), иначе сервер не сможет его скачать.

Ручное обновление без GitHub (запасной путь):

```bash
bash scripts/deploy.sh      # сборка образа локально, загрузка (rsync с докачкой), миграции применяются при старте
```

Первое развёртывание: `MUZLOTO_ADMIN_EMAIL=... bash scripts/deploy.sh` — создаст `.env` и покажет пароль администратора один раз.
Сервер иногда сбрасывает SSH-подключения (боты, `MaxStartups`), скрипт повторяет шаги сам.

## Мессенджеры (Telegram и WhatsApp)

Сообщение «ваша игра подтверждена» уходит клиенту после подтверждения оплаты. Ключи и сессии — только на сервере:

| Переменная в `/home/BocA/muzloto/.env` | Что это |
|---|---|
| `TELEGRAM_API_ID`, `TELEGRAM_API_HASH` | приложение Telegram, выдаются на https://my.telegram.org → API development tools |
| `TELEGRAM_PHONE` | номер личного аккаунта, с которого пишем |
| `WAHA_API_KEY` | ключ шлюза WhatsApp (контейнер `waha`, только во внутренней сети) |

После изменения `.env`: `docker compose up -d`. Затем на сайте «Управление → Мессенджеры»: «Получить код и подключить»
для Telegram (код придёт в Telegram), «Подключить номер» для WhatsApp и скан QR-кода телефоном с отдельным номером.
Сессия Telegram хранится в томе `appdata` (`/data/messengers`), WhatsApp — в томе `wahadata`.

## Откат и диагностика

```bash
ssh boca 'cd /home/BocA/muzloto && docker compose logs --tail 100 app'      # журнал
ssh boca 'cd /home/BocA/muzloto && MUZLOTO_TAG=<прежний коммит> docker compose up -d'   # откат версии
ssh boca 'gunzip -c /home/BocA/muzloto/backups/<файл>.sql.gz | docker exec -i muzloto-db-1 psql -U muzloto -d muzloto'  # восстановление (в пустую БД)
```

Отключить сайт, не трогая calendar: заменить `conf.d/muzloto.conf` на `deploy/nginx/muzloto.http.conf`
(или удалить), затем `docker exec calendar-nginx nginx -t && docker exec calendar-nginx nginx -s reload`.
Копия папки `conf.d` до подключения МузЛото: `/home/BocA/muzloto/nginx-conf.d-backup-20261029-131635`.

## Проверено после развёртывания (29.10.2026)

TLS 1.3 и сертификат; HTTP → HTTPS; заголовки CSP, HSTS, X-Frame-Options, nosniff, Referrer-Policy;
cookie `Secure`; Swagger в Production закрыт (404); API без токена — 401; вебхук без ключа — 401;
вход администратора и SignalR через nginx по WebSocket; `b-o-c-a.ru` всё время отвечал 200.
