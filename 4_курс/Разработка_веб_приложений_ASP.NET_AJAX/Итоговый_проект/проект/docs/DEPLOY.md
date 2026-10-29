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

## Обновление

```bash
bash scripts/deploy.sh      # сборка образа локально, загрузка (rsync с докачкой), миграции применяются при старте
```

Первое развёртывание: `MUZLOTO_ADMIN_EMAIL=... bash scripts/deploy.sh` — создаст `.env` и покажет пароль администратора один раз.
Сервер иногда сбрасывает SSH-подключения (боты, `MaxStartups`), скрипт повторяет шаги сам.

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
