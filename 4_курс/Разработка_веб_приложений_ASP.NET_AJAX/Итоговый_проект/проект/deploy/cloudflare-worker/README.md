# Релей Telegram Bot API (Cloudflare Worker)

Прод-сервер `muzloto.b-o-c-a.ru` физически не достукивается до `api.telegram.org` (провайдер блокирует
Telegram), поэтому бот ходит через этот воркер. Воркер только пересылает запрос в Telegram, тело и путь
не меняются; разрешены лишь методы, которые использует сайт (`getMe`, `getChat`, `sendMessage`, `sendPhoto`,
`sendDocument`) и только с общим секретом в заголовке `X-Relay-Secret`.

## Развернуть в панели Cloudflare (без CLI)

1. <https://dash.cloudflare.com> → **Workers & Pages** → **Create** → **Worker**.
2. Имя: `muzloto-telegram-relay`. Код замените содержимым `worker.js` (кнопка **Edit code** → **Deploy**).
3. **Settings** → **Variables and Secrets** → **Add** → тип **Secret**:
   - имя `RELAY_SECRET`, значение — длинная случайная строка, например из
     `openssl rand -hex 32`;
4. Запомните адрес воркера: `https://muzloto-telegram-relay.<account>.workers.dev`.
5. Проверьте с сервера (токен подставьте свой, в репозиторий его не копируйте):

   ```bash
   curl -s -H "x-relay-secret: <секрет>" \
     "https://muzloto-telegram-relay.<account>.workers.dev/bot<ТОКЕН>/getMe"
   # {"ok":true,"result":{...}}
   ```

   Без заголовка тот же адрес должен отвечать `403 forbidden`.

### Если панель пишет «This uploader does not yet support projects that require a build process»

Это значит, что нажата кнопка **Upload and deploy** (загрузка архива с готовыми файлами) — она годна
только для статических сайтов и Worker без сборки через архив не принимает. Для этого воркера нужен
путь **Create → Worker → Edit code**: пустой воркер, вставить `worker.js` в редактор, **Deploy**.
Либо собрать и залить через `npx wrangler deploy` (см. ниже) — результат одинаковый.

## Развернуть через wrangler

```bash
cd deploy/cloudflare-worker
npx wrangler deploy
npx wrangler secret put RELAY_SECRET
```

## Подключить в приложении

В `/home/BocA/muzloto/.env` на сервере:

```dotenv
TELEGRAM_API_BASE_URL=https://muzloto-telegram-relay.<account>.workers.dev
TELEGRAM_RELAY_SECRET=<тот же секрет>
```

`docker compose up -d` (образ не меняется, только переменные). Эти значения попадают в приложение как
`Telegram:ApiBaseUrl` и `Telegram:RelaySecret`. Пустой `TELEGRAM_API_BASE_URL` = обращаться напрямую
в `api.telegram.org` (годится везде, где Telegram не блокируют).

## Проверка логики без Cloudflare

```bash
cd deploy/cloudflare-worker
node smoke.mjs
```

Скрипт гоняет воркер в Node (там те же `fetch`/`Request`/`FormData`) против локальной заглушки вместо
Telegram: секрет обязателен, посторонние методы не пересылаются, путь, JSON и multipart-загрузка чека
доходят без изменений.
