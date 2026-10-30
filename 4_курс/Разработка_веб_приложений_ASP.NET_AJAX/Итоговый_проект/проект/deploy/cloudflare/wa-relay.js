// Cloudflare Worker "wa-relay": a byte tunnel for the МузЛото server, whose network cannot reach WhatsApp
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
async function check(host) {
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
  return Response.json(result);
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (url.pathname !== '/tunnel' && url.pathname !== '/check') return new Response('Not found', { status: 404 });
    if (!env.RELAY_TOKEN || request.headers.get('Authorization') !== `Bearer ${env.RELAY_TOKEN}`)
      return new Response('Forbidden', { status: 403 });
    if (url.pathname === '/check') return check(url.searchParams.get('host') ?? 'web.whatsapp.com');
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
    let up = 0, down = 0;
    const done = why => console.log(`[${id}] ${host}:${port} closed — ${why}; sent ${up} B, received ${down} B`);
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
