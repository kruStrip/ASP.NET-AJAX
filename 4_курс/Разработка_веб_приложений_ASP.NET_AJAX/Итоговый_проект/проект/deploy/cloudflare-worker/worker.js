/**
 * Telegram Bot API relay for the МузЛото website.
 *
 * The production server cannot reach api.telegram.org (Telegram is blocked for the hosting provider), so the site
 * calls this Worker instead: the path and the body are forwarded unchanged, the Worker only adds the missing
 * network route. See docs/DEPLOY.md, «Если api.telegram.org недоступен».
 *
 * Only the bot methods the site uses are allowed, and only with the shared secret header: the URL alone is not
 * enough, so nobody else can push messages through the company's bot.
 *
 * Environment (Worker settings, as a secret): RELAY_SECRET.
 */

const TELEGRAM_API = "https://api.telegram.org";
const ALLOWED_METHODS = new Set(["getMe", "getChat", "sendMessage", "sendPhoto", "sendDocument"]);

export default {
  async fetch(request, env) {
    if (!env.RELAY_SECRET || request.headers.get("x-relay-secret") !== env.RELAY_SECRET) {
      return new Response("forbidden", { status: 403 });
    }
    if (request.method !== "POST" && request.method !== "GET") {
      return new Response("method not allowed", { status: 405 });
    }
    const url = new URL(request.url);
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
