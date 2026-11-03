/**
 * Telegram Bot API relay for the МузЛото website.
 *
 * The production server cannot reach api.telegram.org (Telegram is blocked for the hosting provider), so the site
 * calls this Worker instead. See docs/DEPLOY.md, «Если api.telegram.org недоступен».
 *
 * Two ways through:
 *   POST /bot<ТОКЕН>/<метод>  — the call is forwarded as is (path, query string and body unchanged). Only the bot
 *                               methods listed in ALLOWED_METHODS are let through; deleteMessage is there for
 *                               operations only (deleting a message the bot sent by mistake), the site does not use it.
 *   POST /send-file           — JSON {token, chatId, topicId, caption, fileName, fileUrl}: the Worker downloads the
 *                               receipt from our site itself and uploads it to Telegram. The site cannot upload the
 *                               file: any request body over ~10 KB to *.workers.dev is dropped on the way by the
 *                               provider's filtering (checked 2026-11-03), while small JSON passes and the Worker's own
 *                               requests to Telegram are not affected.
 *
 * Both require the shared secret header X-Relay-Secret: the URL alone is not enough, so nobody else can push
 * messages through the company's bot.
 *
 * Environment (Worker settings, as secrets/variables): RELAY_SECRET, optional FILE_HOSTS (comma-separated hosts the
 * receipt may be downloaded from, default "muzloto.b-o-c-a.ru").
 */

const TELEGRAM_API = "https://api.telegram.org";
const ALLOWED_METHODS = new Set(["getMe", "getChat", "sendMessage", "sendPhoto", "sendDocument", "deleteMessage"]);
const DEFAULT_FILE_HOSTS = "muzloto.b-o-c-a.ru";
const MAX_FILE_BYTES = 20 * 1024 * 1024;

export default {
  async fetch(request, env) {
    if (!env.RELAY_SECRET || request.headers.get("x-relay-secret") !== env.RELAY_SECRET) {
      return new Response("forbidden", { status: 403 });
    }
    if (request.method !== "POST" && request.method !== "GET") {
      return new Response("method not allowed", { status: 405 });
    }
    const url = new URL(request.url);
    if (url.pathname === "/send-file") {
      if (request.method !== "POST") return new Response("method not allowed", { status: 405 });
      return sendFile(request, env);
    }
    const match = url.pathname.match(/^\/bot[^/]+\/([A-Za-z]+)$/);
    if (!match || !ALLOWED_METHODS.has(match[1])) {
      return new Response("not found", { status: 404 });
    }
    // The body is buffered so Telegram gets a normal Content-Length (it is loose with chunked uploads).
    const body = request.method === "GET" ? undefined : await request.arrayBuffer();
    // The query string is kept too: getChat and friends take their arguments in it.
    const upstream = await fetch(TELEGRAM_API + url.pathname + url.search, {
      method: request.method,
      headers: body
        ? { "content-type": request.headers.get("content-type") ?? "application/octet-stream" }
        : undefined,
      body,
    });
    // Re-build the response: the body is already decompressed, so the original encoding headers must not be kept.
    const headers = new Headers();
    const contentType = upstream.headers.get("content-type");
    if (contentType) headers.set("content-type", contentType);
    return new Response(upstream.body, { status: upstream.status, headers });
  },
};

/** {token, chatId, topicId, caption, fileName, fileUrl} -> the receipt lands in the group as a photo or a document. */
async function sendFile(request, env) {
  let call;
  try {
    call = await request.json();
  } catch {
    return failure(400, "Релей: тело запроса — не JSON.");
  }
  const token = text(call.token);
  const chatId = text(call.chatId);
  const fileUrl = text(call.fileUrl);
  if (!token || !chatId || !fileUrl) return failure(400, "Релей: нужны token, chatId и fileUrl.");

  const allowed = text(env.FILE_HOSTS) || DEFAULT_FILE_HOSTS;
  let target;
  try {
    target = new URL(fileUrl);
  } catch {
    return failure(400, "Релей: fileUrl — не ссылка.");
  }
  const hosts = allowed.split(",").map((h) => h.trim().toLowerCase()).filter(Boolean);
  if (target.protocol !== "https:" || !hosts.includes(target.hostname.toLowerCase())) {
    return failure(403, `Релей: скачивать файлы можно только с ${hosts.join(", ")}.`);
  }

  let file;
  try {
    file = await fetch(target.toString(), { redirect: "follow" });
  } catch {
    return failure(502, "Релей: не смог скачать файл с сайта.");
  }
  if (!file.ok) return failure(502, `Релей: сайт ответил ${file.status} на запрос файла.`);
  const bytes = await file.arrayBuffer();
  if (bytes.byteLength === 0) return failure(502, "Релей: сайт вернул пустой файл.");
  if (bytes.byteLength > MAX_FILE_BYTES) return failure(413, "Релей: файл больше 20 МБ.");

  const fileName = text(call.fileName) || "receipt";
  const contentType = file.headers.get("content-type") || "application/octet-stream";
  const isPhoto = contentType.startsWith("image/") || /\.(jpe?g|png|webp)$/i.test(fileName);
  const form = new FormData();
  form.set("chat_id", chatId);
  const topicId = text(call.topicId);
  if (topicId) form.set("message_thread_id", topicId);
  const caption = text(call.caption);
  // Telegram rejects captions longer than 1024 characters; the site trims them too, this is a safety net.
  if (caption) form.set("caption", caption.length > 1024 ? caption.slice(0, 1023) + "…" : caption);
  form.set(isPhoto ? "photo" : "document", new Blob([bytes], { type: contentType }), fileName);

  const upstream = await fetch(`${TELEGRAM_API}/bot${token}/${isPhoto ? "sendPhoto" : "sendDocument"}`, {
    method: "POST",
    body: form,
  });
  return new Response(upstream.body, {
    status: upstream.status,
    headers: { "content-type": upstream.headers.get("content-type") || "application/json" },
  });
}

function text(value) {
  return typeof value === "string" ? value.trim() : value == null ? "" : String(value).trim();
}

/** Same shape as a Bot API error, so the site shows the reason as usual. */
function failure(status, description) {
  return new Response(JSON.stringify({ ok: false, error_code: status, description }), {
    status,
    headers: { "content-type": "application/json" },
  });
}
