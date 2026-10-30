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

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (url.pathname !== '/tunnel') return new Response('Not found', { status: 404 });
    if (!env.RELAY_TOKEN || request.headers.get('Authorization') !== `Bearer ${env.RELAY_TOKEN}`)
      return new Response('Forbidden', { status: 403 });
    if (request.headers.get('Upgrade')?.toLowerCase() !== 'websocket')
      return new Response('Expected WebSocket', { status: 426 });

    const host = url.searchParams.get('host') ?? '';
    const port = Number(url.searchParams.get('port') ?? '443');
    if (!(ALLOWED_HOSTS.test(host) || isTelegramIp(host)) || ![443, 80, 5222].includes(port))
      return new Response('Host not allowed', { status: 403 });

    const socket = connect({ hostname: host, port });
    const [client, server] = Object.values(new WebSocketPair());
    server.accept();

    const writer = socket.writable.getWriter();
    server.addEventListener('message', async event => {
      try {
        const data = typeof event.data === 'string' ? new TextEncoder().encode(event.data) : new Uint8Array(event.data);
        await writer.write(data);
      } catch { try { server.close(1011, 'upstream write failed'); } catch {} }
    });
    server.addEventListener('close', () => { try { socket.close(); } catch {} });
    server.addEventListener('error', () => { try { socket.close(); } catch {} });

    (async () => {
      const reader = socket.readable.getReader();
      try {
        for (;;) {
          const { value, done } = await reader.read();
          if (done) break;
          server.send(value);
        }
      } catch {}
      try { server.close(1000, 'upstream closed'); } catch {}
    })();

    return new Response(null, { status: 101, webSocket: client });
  }
};
