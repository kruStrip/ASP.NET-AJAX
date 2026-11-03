# Релей Telegram Bot API (Cloudflare Worker)

Прод-сервер `muzloto.b-o-c-a.ru` физически не достукивается до `api.telegram.org` (провайдер блокирует
Telegram), поэтому бот ходит через этот воркер. Обычные вызовы воркер только пересылает (тело, путь и query
string не меняются); разрешены лишь методы, которые использует сайт (`getMe`, `getChat`, `sendMessage`, `sendPhoto`,
`sendDocument`) плюс `deleteMessage` для ручной уборки (удалить отправленное по ошибке сообщение; сайт его не
вызывает) — и только с общим секретом в заголовке `X-Relay-Secret`.

## Почему чек идёт отдельным путём `/send-file`

Провайдер пропускает к `*.workers.dev` тела запросов только примерно до 10 КБ: POST на 68 КБ с прод-сервера
висит до таймаута — после первых ~10 КБ уходят лишь ретрансмиты (`cwnd:1`, `bytes_acked:10581`), хотя те же
68 КБ на другие узлы Cloudflare (`speed.cloudflare.com`, `postman-echo.com`) проходят, а MTU/PMTU и `advmss`
ни при чём (замерено 2026-11-03). Поэтому сайт при настроенном релее **не загружает файл чека**, а шлёт маленький
JSON:

```json
POST /send-file
{ "token": "123:ABC", "chatId": "-100…", "topicId": "2",
  "caption": "Номер клиента: …", "fileName": "…jpg",
  "fileUrl": "https://muzloto.b-o-c-a.ru/receipts/<id>/<файл>?t=<подпись>" }
```

Воркер сам скачивает файл по ссылке и отправляет его в Telegram как `sendPhoto` (картинка) или `sendDocument`
(всё остальное, например PDF). Ограничения: только `https`, только хосты из переменной `FILE_HOSTS`
(по умолчанию `muzloto.b-o-c-a.ru`, можно перечислить через запятую), файл до 20 МБ, подпись не длиннее
1024 символов. Чужой хост → `403`, нет полей → `400`, сайт не отдал файл → `502`; ответы в форме ошибок Bot API,
поэтому сайт показывает причину как обычно.

Без релея (прямой `api.telegram.org`) приложение по-прежнему загружает файл с сервера — там ограничения нет.


## Развернуть в панели Cloudflare (без CLI)

1. <https://dash.cloudflare.com> → **Workers & Pages** → **Create** → **Worker**.
2. Имя: `muzloto-telegram-relay`. Код замените содержимым `worker.js` (кнопка **Edit code** → **Deploy**).
3. **Settings** → **Variables and Secrets** → **Add** → тип **Secret**:
   - имя `RELAY_SECRET`, значение — длинная случайная строка, например из
     `openssl rand -hex 32`;
   - по желанию тип **Text** `FILE_HOSTS` — хосты, с которых можно скачивать чек для `/send-file`
     (по умолчанию `muzloto.b-o-c-a.ru`);
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
Telegram: секрет обязателен, посторонние методы не пересылаются, путь, query string, JSON и multipart-загрузка
чека доходят без изменений; `/send-file` скачивает файл со «сайта»-заглушки и отправляет его как фото или
документ, а чужой хост и нехватку полей отклоняет, не обращаясь к Telegram.

## Обновить код на уже развёрнутом воркере

**Edit code** → вставить новый `worker.js` → **Deploy**. Проверка, что версия свежая: `POST /send-file` без
тела должен ответить `400` с текстом «Релей: тело запроса — не JSON.»; старый воркер отвечает `404 not found`.
Приложение в этом случае пишет «Релей Telegram не умеет отправлять чек по ссылке — обновите
deploy/cloudflare-worker/worker.js».
