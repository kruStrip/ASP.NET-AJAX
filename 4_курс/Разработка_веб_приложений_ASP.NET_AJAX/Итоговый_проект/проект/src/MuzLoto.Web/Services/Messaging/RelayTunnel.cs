using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;

namespace MuzLoto.Web.Services.Messaging;

/// <summary>
/// The server's network cannot reach WhatsApp (DNS and connections are blocked), so traffic goes through a
/// Cloudflare Worker (deploy/cloudflare/wa-relay.js): this service is a small HTTP CONNECT proxy for the WAHA
/// container (WHATSAPP_PROXY_SERVER=app:3128) and turns every CONNECT into a WebSocket to the Worker, which opens
/// the TCP connection. TLS stays end-to-end; the Worker only sees encrypted bytes.
/// Configuration: Relay:Url (wss://…/tunnel), Relay:Token (the Worker's RELAY_TOKEN), Relay:ProxyPassword (WAHA's
/// proxy password). Without Relay:Url nothing listens.
/// </summary>
public sealed class RelayTunnel(IConfiguration config, ILogger<RelayTunnel> logger) : BackgroundService
{
    public const int Port = 3128;
    /// <summary>Largest WebSocket message sent to the relay.</summary>
    public const int MaxMessage = 4 * 1024;
    private string? RelayUrl => config["Relay:Url"];
    public bool IsConfigured => !string.IsNullOrWhiteSpace(RelayUrl) && !string.IsNullOrWhiteSpace(config["Relay:Token"]);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!IsConfigured) return;
        var listener = new TcpListener(IPAddress.Any, Port);
        listener.Start();
        logger.LogInformation("Relay tunnel proxy listens on {Port}", Port);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken);
                _ = Task.Run(() => HandleAsync(client, stoppingToken), CancellationToken.None);
            }
        }
        catch (OperationCanceledException) { }
        finally { listener.Stop(); }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken stoppingToken)
    {
        using var _ = client;
        var stream = client.GetStream();
        try
        {
            var head = await ReadHeadAsync(stream, stoppingToken);
            var lines = head.Split("\r\n");
            var parts = lines[0].Split(' ');
            if (parts.Length < 2 || parts[0] != "CONNECT") { await ReplyAsync(stream, "405 Method Not Allowed", stoppingToken); return; }
            if (!Authorized(lines)) { await ReplyAsync(stream, "407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"relay\"", stoppingToken); return; }
            var target = parts[1];
            var colon = target.LastIndexOf(':');
            var (host, port) = colon > 0 ? (target[..colon], target[(colon + 1)..]) : (target, "443");

            if (Chunked)
            {
                await RunChunkedAsync(stream, host, int.Parse(port), stoppingToken);
                return;
            }
            ClientWebSocket ws;
            try { ws = await OpenAsync(host, int.Parse(port), stoppingToken); }
            catch (Exception ex) when (ex is WebSocketException or HttpRequestException or SocketException)
            {
                // The usual causes: the Worker is unreachable, the token does not match (403) or the host is not allowed.
                logger.LogWarning("Relay: cannot open the tunnel to {Host}:{Port} through the Cloudflare Worker: {Reason}",
                    host, port, ex.InnerException?.Message is { } inner ? $"{ex.Message} ({inner})" : ex.Message);
                await ReplyAsync(stream, "502 Bad Gateway", stoppingToken);
                return;
            }
            using var _ws = ws;
            logger.LogInformation("Relay: tunnel to {Host}:{Port} opened", host, port);
            await ReplyAsync(stream, "200 Connection Established", stoppingToken);
            var (sent, received) = await PipeAsync(stream, ws, stoppingToken);
            logger.LogInformation("Relay: tunnel to {Host}:{Port} closed — sent {Sent} B, received {Received} B, Worker said: {Close}",
                host, port, sent, received, ws.CloseStatusDescription ?? ws.CloseStatus?.ToString() ?? "nothing");
        }
        catch (Exception ex) when (ex is IOException or WebSocketException or SocketException or OperationCanceledException or FormatException)
        {
            logger.LogDebug(ex, "Relay connection ended");
        }
    }

    // ---- Chunked mode (default) ----
    // The provider freezes any single connection to Cloudflare after ~64 KB, so instead of one long WebSocket the bytes
    // travel in pieces of ≤ 8 KB, each in its own short HTTPS request on a fresh connection; a Durable Object in the
    // Worker holds the real TCP connection. Offsets make retries safe: a repeated piece is neither lost nor doubled.

    private const int Piece = 8 * 1024;
    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.Zero })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    /// <summary>Chunked unless Relay:Mode is "websocket".</summary>
    private bool Chunked => !string.Equals(config["Relay:Mode"], "websocket", StringComparison.OrdinalIgnoreCase);

    /// <summary>https://relay.example from wss://relay.example/tunnel.</summary>
    private string HttpBase
    {
        get
        {
            var uri = new Uri(RelayUrl!);
            var scheme = uri.Scheme is "ws" or "http" ? "http" : "https";
            return $"{scheme}://{uri.Authority}";
        }
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, HttpBase + path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config["Relay:Token"]);
        request.Headers.ConnectionClose = true;   // every piece on a fresh connection: none of them grows past the limit
        return request;
    }

    private async Task RunChunkedAsync(NetworkStream stream, string host, int port, CancellationToken stoppingToken)
    {
        string id;
        try
        {
            using var open = Request(HttpMethod.Post, $"/s/open?host={Uri.EscapeDataString(host)}&port={port}");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var response = await Http.SendAsync(open, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Relay: the Worker refused to open {Host}:{Port}: {Status} {Body}", host, port,
                    (int)response.StatusCode, await response.Content.ReadAsStringAsync(timeout.Token));
                await ReplyAsync(stream, "502 Bad Gateway", stoppingToken);
                return;
            }
            using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            id = json.RootElement.GetProperty("id").GetString()!;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            logger.LogWarning("Relay: cannot open {Host}:{Port} through the Worker: {Reason}", host, port, ex.Message);
            await ReplyAsync(stream, "502 Bad Gateway", stoppingToken);
            return;
        }

        logger.LogInformation("Relay: chunked tunnel {Id} to {Host}:{Port} opened", id[..8], host, port);
        await ReplyAsync(stream, "200 Connection Established", stoppingToken);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        long sent = 0, received = 0, retries = 0;
        var why = "the client closed the connection";

        var up = Task.Run(async () =>
        {
            var buffer = new byte[Piece];
            int read;
            while ((read = await stream.ReadAsync(buffer, cts.Token)) > 0)
            {
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        using var request = Request(HttpMethod.Post, $"/s/{id}/up?at={sent}");
                        request.Content = new ByteArrayContent(buffer, 0, read);
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(15));
                        using var response = await Http.SendAsync(request, timeout.Token);
                        if (response.IsSuccessStatusCode) break;
                        if (response.StatusCode == HttpStatusCode.Gone) { why = "the Worker closed the tunnel"; return; }
                    }
                    catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cts.IsCancellationRequested)) { }
                    Interlocked.Increment(ref retries);
                    if (attempt >= 8) { why = "a piece could not be sent after 8 attempts"; return; }
                    await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), cts.Token);
                }
                sent += read;
            }
        }, cts.Token);

        var down = Task.Run(async () =>
        {
            var failures = 0;
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    using var request = Request(HttpMethod.Get, $"/s/{id}/down?from={received}");
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    using var response = await Http.SendAsync(request, timeout.Token);
                    if (response.StatusCode == HttpStatusCode.Gone) { why = "the host closed the connection"; return; }
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token);
                        await stream.WriteAsync(bytes, cts.Token);
                        received += bytes.Length;
                    }
                    else if (response.StatusCode != HttpStatusCode.NoContent) throw new HttpRequestException("HTTP " + (int)response.StatusCode);
                    failures = 0;
                }
                catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cts.IsCancellationRequested))
                {
                    Interlocked.Increment(ref retries);
                    if (++failures >= 8) { why = "the Worker stopped answering"; return; }
                    await Task.Delay(TimeSpan.FromMilliseconds(300 * failures), cts.Token);
                }
            }
        }, cts.Token);

        try { await Task.WhenAny(up, down); }
        finally
        {
            cts.Cancel();
            try { using var close = Request(HttpMethod.Post, $"/s/{id}/close"); using var _ = await Http.SendAsync(close, CancellationToken.None); } catch { }
            logger.LogInformation("Relay: chunked tunnel {Id} to {Host}:{Port} closed — {Why}; sent {Sent} B, received {Received} B, retries {Retries}",
                id[..8], host, port, why, sent, received, Interlocked.Read(ref retries));
        }
    }

    /// <summary>Opens the tunnel to host:port through the Worker; also used by the Telegram client.</summary>
    public async Task<ClientWebSocket> OpenAsync(string host, int port, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", "Bearer " + config["Relay:Token"]);
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        var url = $"{RelayUrl!.TrimEnd('/')}?host={Uri.EscapeDataString(host)}&port={port}";
        try { await ws.ConnectAsync(new Uri(url), ct); }
        catch { ws.Dispose(); throw; }
        return ws;
    }

    /// <summary>Copies bytes both ways until either side closes; returns the byte counts (sent up, received down).</summary>
    public static async Task<(long Sent, long Received)> PipeAsync(Stream stream, WebSocket ws, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        long sent = 0, received = 0;
        var up = Task.Run(async () =>
        {
            // Small messages: Cloudflare dropped 16 KB ones (WhatsApp's ~40 KB key upload never arrived).
            var buffer = new byte[MaxMessage];
            int read;
            while ((read = await stream.ReadAsync(buffer, cts.Token)) > 0)
            {
                await ws.SendAsync(buffer.AsMemory(0, read), WebSocketMessageType.Binary, true, cts.Token);
                Interlocked.Add(ref sent, read);
            }
        }, cts.Token);
        var down = Task.Run(async () =>
        {
            var buffer = new byte[16 * 1024];
            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer.AsMemory(), cts.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;
                await stream.WriteAsync(buffer.AsMemory(0, result.Count), cts.Token);
                Interlocked.Add(ref received, result.Count);
            }
        }, cts.Token);
        await Task.WhenAny(up, down);
        cts.Cancel();
        try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); } catch { }
        return (Interlocked.Read(ref sent), Interlocked.Read(ref received));
    }

    /// <summary>
    /// Diagnostics for «Мессенджеры»: (1) the WhatsApp web page through the whole tunnel; (2) the Worker uploads 200 KB
    /// to WhatsApp over a raw socket — the Cloudflare → WhatsApp leg alone.
    /// </summary>
    public async Task<string> SelfTestAsync(CancellationToken ct)
    {
        if (!IsConfigured) return "Туннель не настроен (нет WA_RELAY_URL / WA_RELAY_TOKEN).";
        var report = new StringBuilder();
        var baseUrl = RelayUrl!.TrimEnd('/');
        baseUrl = baseUrl[..baseUrl.LastIndexOf('/')];
        // The whole chain, as WhatsApp uses it: an HTTPS page from web.whatsapp.com through this proxy and the Worker.
        var started = DateTime.UtcNow;
        try
        {
            var proxy = new WebProxy($"http://127.0.0.1:{Port}");
            if (config["Relay:ProxyPassword"] is { Length: > 0 } password) proxy.Credentials = new NetworkCredential("waha", password);
            using var viaTunnel = new HttpClient(new HttpClientHandler { Proxy = proxy, UseProxy = true }) { Timeout = TimeSpan.FromSeconds(60) };
            var page = await viaTunnel.GetByteArrayAsync("https://web.whatsapp.com/", ct);
            report.Append($"Через туннель: страница WhatsApp {page.Length / 1024} КБ за {(DateTime.UtcNow - started).TotalSeconds:0.0} с — туннель работает. ");
        }
        catch (Exception ex)
        {
            report.Append($"Через туннель: ошибка за {(DateTime.UtcNow - started).TotalSeconds:0.0} с — {ex.GetBaseException().Message}. ");
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
            using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/check?host=web.whatsapp.com&upload=204800");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config["Relay:Token"]);
            using var response = await http.SendAsync(request, ct);
            report.Append("Cloudflare → WhatsApp: " + await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) { report.Append("Cloudflare → WhatsApp: ошибка — " + ex.Message); }
        return report.ToString();
    }

    private bool Authorized(string[] lines)
    {
        var password = config["Relay:ProxyPassword"];
        if (string.IsNullOrEmpty(password)) return true;   // only reachable inside the server's docker networks
        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("waha:" + password));
        return lines.Any(l => l.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase)
            && l["Proxy-Authorization:".Length..].Trim() == expected);
    }

    private static async Task<string> ReadHeadAsync(NetworkStream stream, CancellationToken ct)
    {
        var bytes = new List<byte>(512);
        var one = new byte[1];
        // Byte by byte up to the empty line: whatever follows belongs to the tunnel.
        while (bytes.Count < 8192 && await stream.ReadAsync(one, ct) == 1)
        {
            bytes.Add(one[0]);
            if (bytes.Count >= 4 && bytes[^4] == '\r' && bytes[^3] == '\n' && bytes[^2] == '\r' && bytes[^1] == '\n') break;
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    private static Task ReplyAsync(NetworkStream stream, string status, CancellationToken ct) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\n\r\n"), ct).AsTask();
}
