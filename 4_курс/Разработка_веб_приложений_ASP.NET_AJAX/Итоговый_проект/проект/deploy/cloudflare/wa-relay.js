// Cloudflare Worker "wa-relay" (deploy with wrangler from this folder: see wrangler.jsonc): a byte tunnel for the МузЛото server, whose network cannot reach WhatsApp
// (and possibly Telegram). The site opens a WebSocket to /tunnel?host=…&port=…; the Worker connects to that host
// over TCP and passes bytes both ways. TLS stays end-to-end between the server and WhatsApp/Telegram: the Worker
// sees only encrypted bytes.
//
// Secret: Settings → Variables and Secrets → add RELAY_TOKEN (type Secret). The same value goes to the server .env
// as WA_RELAY_TOKEN. Only WhatsApp and Telegram addresses are allowed, so this is not an open proxy.
import { connect } from 'cloudflare:sockets';

const ALLOWED_HOSTS = /^([a-z0-9-]+\.)*(whatsapp\.com|whatsapp\.net)$/i;
// Telegram data centres (MTProto), by address.
const TELEGRAM_NETS = [
  { net: '149.154.160.0', bits: 20 }, { net: '91.108.4.0', bits: 22 }, { net: '91.108.8.0', bits: 22 },
  { net: '91.108.12.0', bits: 22 }, { net: '91.108.16.0', bits: 22 }, { net: '91.108.56.0', bits: 22 },
  { net: '95.161.64.0', bits: 20 }
];

const ipToInt = ip => ip.split('.').reduce((n, p) => (n << 8) + Number(p), 0) >>> 0;
const isTelegramIp = host => /^\d+\.\d+\.\d+\.\d+$/.test(host) &&
  TELEGRAM_NETS.some(({ net, bits }) => (ipToInt(host) >>> (32 - bits)) === (ipToInt(net) >>> (32 - bits)));

// Diagnostics: can this Worker reach the host by an ordinary HTTPS request, and by a raw TCP socket?
// Diagnostics: a WebSocket that sends every message straight back (tests the site → Cloudflare leg alone).
function echo() {
  const [client, server] = Object.values(new WebSocketPair());
  try { server.binaryType = 'arraybuffer'; } catch {}
  server.accept();
  let bytes = 0, messages = 0;
  server.addEventListener('message', event => {
    const size = event.data.byteLength ?? event.data.size ?? event.data.length ?? 0;
    bytes += size; messages++;
    server.send(event.data);
  });
  server.addEventListener('close', () => console.log(`[echo] ${messages} messages, ${bytes} B echoed`));
  return new Response(null, { status: 101, webSocket: client });
}

async function check(host, upload) {
  const withTimeout = (promise, ms) => Promise.race([promise, new Promise((_, no) => setTimeout(() => no(new Error(`timeout ${ms} ms`)), ms))]);
  const result = { host };
  const t1 = Date.now();
  try {
    const r = await withTimeout(fetch(`https://${host}/`, { redirect: 'manual' }), 10000);
    result.https = `HTTP ${r.status} in ${Date.now() - t1} ms`;
  } catch (err) { result.https = `failed: ${err.message}`; }
  const t2 = Date.now();
  try {
    const socket = connect({ hostname: host, port: 443 });
    await withTimeout(socket.opened, 10000);
    result.tcp = `opened in ${Date.now() - t2} ms`;
    socket.close();
  } catch (err) { result.tcp = `failed: ${err.message}`; }
  // A full TLS request over a raw socket, the way the tunnel carries WhatsApp's own TLS.
  const t3 = Date.now();
  try {
    const socket = connect({ hostname: host, port: 443 }, { secureTransport: 'on' });
    const writer = socket.writable.getWriter();
    await writer.write(new TextEncoder().encode(`GET / HTTP/1.1\r\nHost: ${host}\r\nConnection: close\r\n\r\n`));
    const { value } = await withTimeout(socket.readable.getReader().read(), 10000);
    result.tlsSocket = `answered in ${Date.now() - t3} ms: ${new TextDecoder().decode(value).split('\r\n')[0]}`;
    socket.close();
  } catch (err) { result.tlsSocket = `failed: ${err.message}`; }
  // Large upload over a raw TLS socket (tests the Cloudflare → host leg with WhatsApp-sized writes).
  if (upload > 0) {
    const t4 = Date.now();
    let written = 0;
    try {
      const socket = connect({ hostname: host, port: 443 }, { secureTransport: 'on' });
      const writer = socket.writable.getWriter();
      await writer.write(new TextEncoder().encode(`POST / HTTP/1.1\r\nHost: ${host}\r\nContent-Type: application/octet-stream\r\nContent-Length: ${upload}\r\nConnection: close\r\n\r\n`));
      const chunk = new Uint8Array(4096);
      await withTimeout((async () => {
        while (written < upload) {
          const part = chunk.subarray(0, Math.min(chunk.length, upload - written));
          await writer.write(part);
          written += part.length;
        }
      })(), 15000);
      const { value } = await withTimeout(socket.readable.getReader().read(), 15000);
      result.upload = `${written} B written in ${Date.now() - t4} ms, answer: ${new TextDecoder().decode(value).split('\r\n')[0]}`;
      socket.close();
    } catch (err) { result.upload = `failed after ${written} B: ${err.message}`; }
  }
  return Response.json(result);
}

// ---- Chunked tunnel ----------------------------------------------------------------------------------------------
// The server's provider freezes any single connection to Cloudflare after ~64 KB, so one long WebSocket cannot carry
// WhatsApp (its login uploads ~40 KB of keys at once). Instead a Durable Object holds the TCP connection to WhatsApp
// and the site moves bytes in small pieces, each request on a fresh connection:
//   POST /s/open?host=&port=        → { id }
//   POST /s/{id}/up?at=N   (body)   → bytes for the host; N is the byte offset, so a retried piece is not written twice
//   GET  /s/{id}/down?from=N        → the host's bytes from offset N (≤ 8 KB); waits up to 20 s; 204 = nothing yet,
//                                     410 = the host closed the connection
//   POST /s/{id}/close
const PIECE = 8 * 1024;

async function chunkedTunnel(request, url, env) {
  if (!env.TUNNEL) return new Response('Durable Object binding TUNNEL is missing (deploy with wrangler)', { status: 500 });
  const [, , id, op] = url.pathname.split('/');
  if (id === 'open') {
    const host = url.searchParams.get('host') ?? '';
    const port = Number(url.searchParams.get('port') ?? '443');
    if (!(ALLOWED_HOSTS.test(host) || isTelegramIp(host)) || ![443, 80, 5222].includes(port))
      return new Response('Host not allowed', { status: 403 });
    const sessionId = crypto.randomUUID();
    const stub = env.TUNNEL.get(env.TUNNEL.idFromName(sessionId));
    const opened = await stub.fetch(`https://do/open?host=${encodeURIComponent(host)}&port=${port}`, { method: 'POST' });
    return opened.ok ? Response.json({ id: sessionId }) : opened;
  }
  if (!/^[0-9a-f-]{36}$/.test(id ?? '') || !['up', 'down', 'close'].includes(op))
    return new Response('Not found', { status: 404 });
  const stub = env.TUNNEL.get(env.TUNNEL.idFromName(id));
  return stub.fetch(`https://do/${op}${url.search}`, { method: request.method, body: request.method === 'POST' ? request.body : undefined });
}

/** One tunnelled TCP connection; lives as long as the site keeps polling it. */
export class Tunnel {
  constructor(state, env) {
    this.state = state;
    this.socket = null;
    this.writer = null;
    this.upOffset = 0;            // bytes written to the host so far
    this.downBase = 0;            // offset of the first byte still buffered
    this.downBuffers = [];        // host bytes not yet acknowledged by the site
    this.closed = false;
    this.waiters = [];
    this.label = '';
  }

  wake() { for (const resolve of this.waiters.splice(0)) resolve(); }

  async fetch(request) {
    const url = new URL(request.url);
    const op = url.pathname.slice(1);
    if (op === 'open') {
      const host = url.searchParams.get('host'); const port = Number(url.searchParams.get('port'));
      this.label = `${host}:${port}`;
      this.socket = connect({ hostname: host, port });
      this.writer = this.socket.writable.getWriter();
      this.socket.opened.then(() => console.log(`[do] TCP to ${this.label} opened`)).catch(err => this.finish('connect failed: ' + err));
      this.pump();
      return new Response('ok');
    }
    if (!this.socket) return new Response('No such tunnel', { status: 410 });
    if (op === 'up') {
      const at = Number(url.searchParams.get('at'));
      const data = new Uint8Array(await request.arrayBuffer());
      if (at < this.upOffset) return new Response('duplicate');            // a retried piece already written
      if (at > this.upOffset) return new Response('gap', { status: 409 }); // an earlier piece is still on its way
      this.upOffset += data.byteLength;
      try { await this.writer.write(data); } catch (err) { this.finish('write failed: ' + err); return new Response('closed', { status: 410 }); }
      return new Response('ok');
    }
    if (op === 'down') {
      const from = Number(url.searchParams.get('from'));
      this.drop(from);
      if (this.available(from) === 0 && !this.closed)
        await Promise.race([new Promise(resolve => this.waiters.push(resolve)), new Promise(resolve => setTimeout(resolve, 20000))]);
      const piece = this.take(from, PIECE);
      if (piece.byteLength > 0) return new Response(piece, { headers: { 'Content-Type': 'application/octet-stream' } });
      return new Response(null, { status: this.closed ? 410 : 204 });
    }
    if (op === 'close') { this.finish('closed by the site'); return new Response('ok'); }
    return new Response('Not found', { status: 404 });
  }

  async pump() {
    const reader = this.socket.readable.getReader();
    try {
      for (;;) {
        const { value, done } = await reader.read();
        if (done) break;
        this.downBuffers.push(value);
        this.wake();
      }
      this.finish('host closed the connection');
    } catch (err) { this.finish('read failed: ' + err); }
  }

  finish(why) {
    if (this.closed) return;
    this.closed = true;
    console.log(`[do] ${this.label} ${why}; up ${this.upOffset} B, down ${this.downBase + this.bufferedBytes()} B`);
    try { this.socket?.close(); } catch {}
    this.wake();
  }

  bufferedBytes() { return this.downBuffers.reduce((n, b) => n + b.byteLength, 0); }
  available(from) { return Math.max(0, this.downBase + this.bufferedBytes() - from); }

  /** Forgets bytes the site has confirmed by asking for a later offset. */
  drop(from) {
    while (this.downBuffers.length && this.downBase + this.downBuffers[0].byteLength <= from) {
      this.downBase += this.downBuffers[0].byteLength;
      this.downBuffers.shift();
    }
  }

  take(from, max) {
    const out = new Uint8Array(Math.min(max, this.available(from)));
    let filled = 0, position = this.downBase;
    for (const buffer of this.downBuffers) {
      const start = Math.max(0, from - position);
      if (start < buffer.byteLength && filled < out.length) {
        const part = buffer.subarray(start, start + (out.length - filled));
        out.set(part, filled);
        filled += part.byteLength;
      }
      position += buffer.byteLength;
    }
    return out;
  }
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    const chunked = url.pathname.startsWith('/s/');
    if (!chunked && !['/tunnel', '/check', '/echo'].includes(url.pathname)) return new Response('Not found', { status: 404 });
    if (!env.RELAY_TOKEN || request.headers.get('Authorization') !== `Bearer ${env.RELAY_TOKEN}`)
      return new Response('Forbidden', { status: 403 });
    if (url.pathname === '/check') return check(url.searchParams.get('host') ?? 'web.whatsapp.com', Number(url.searchParams.get('upload') ?? '0'));
    if (url.pathname === '/echo') return echo();
    if (chunked) return chunkedTunnel(request, url, env);
    if (request.headers.get('Upgrade')?.toLowerCase() !== 'websocket')
      return new Response('Expected WebSocket', { status: 426 });

    const host = url.searchParams.get('host') ?? '';
    const port = Number(url.searchParams.get('port') ?? '443');
    if (!(ALLOWED_HOSTS.test(host) || isTelegramIp(host)) || ![443, 80, 5222].includes(port))
      return new Response('Host not allowed', { status: 403 });

    const socket = connect({ hostname: host, port });
    const [client, server] = Object.values(new WebSocketPair());
    server.accept();

    // Diagnostics in Workers Logs (Observability): did the TCP connection open, how many bytes went each way, why it ended.
    const id = crypto.randomUUID().slice(0, 8);
    let up = 0, down = 0, messages = 0, largest = 0;
    const done = why => console.log(`[${id}] ${host}:${port} closed — ${why}; sent ${up} B in ${messages} messages (largest ${largest} B), received ${down} B`);
    socket.opened.then(() => console.log(`[${id}] TCP to ${host}:${port} opened`))
      .catch(err => { done('TCP connect failed: ' + err); try { server.close(1011, 'connect failed'); } catch {} });

    const writer = socket.writable.getWriter();
    // Messages from the site carry the client's bytes. Ask for ArrayBuffers (no async Blob reading); a Blob is still
    // handled in case the runtime ignores that. Writes are queued by the stream in call order and never awaited one
    // by one: a slow write must not hold back the data behind it (a stalled ~40 KB key upload broke WhatsApp's login).
    try { server.binaryType = 'arraybuffer'; } catch {}
    const syncBytes = data =>
      data instanceof ArrayBuffer ? new Uint8Array(data)
      : ArrayBuffer.isView(data) ? new Uint8Array(data.buffer, data.byteOffset, data.byteLength)
      : typeof data === 'string' ? new TextEncoder().encode(data)
      : null;
    const fail = err => { done('write to upstream failed: ' + err); try { server.close(1011, 'upstream write failed'); } catch {} };
    const send = bytes => {
      if (up === 0) console.log(`[${id}] first ${bytes.byteLength} B from the site`);
      up += bytes.byteLength;
      messages++;
      largest = Math.max(largest, bytes.byteLength);
      writer.write(bytes).catch(fail);
    };
    let blobs = Promise.resolve();   // keeps order only while Blobs are being read
    server.addEventListener('message', event => {
      const bytes = syncBytes(event.data);
      if (bytes) blobs = blobs.then(() => send(bytes));
      else blobs = blobs.then(async () => send(new Uint8Array(await event.data.arrayBuffer()))).catch(fail);
    });
    server.addEventListener('close', event => { done(`client closed (${event.code})`); try { socket.close(); } catch {} });
    server.addEventListener('error', () => { try { socket.close(); } catch {} });

    (async () => {
      const reader = socket.readable.getReader();
      try {
        for (;;) {
          const { value, done: finished } = await reader.read();
          if (finished) break;
          if (down === 0) console.log(`[${id}] first ${value.byteLength} B from ${host}`);
          down += value.byteLength;
          server.send(value);
        }
        done('upstream ended');
      } catch (err) { done('read from upstream failed: ' + err); }
      try { server.close(1000, 'upstream closed'); } catch {}
    })();

    return new Response(null, { status: 101, webSocket: client });
  }
};
