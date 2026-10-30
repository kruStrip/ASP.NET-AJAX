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
            await PipeAsync(stream, ws, stoppingToken);
        }
        catch (Exception ex) when (ex is IOException or WebSocketException or SocketException or OperationCanceledException or FormatException)
        {
            logger.LogDebug(ex, "Relay connection ended");
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

    /// <summary>Copies bytes both ways until either side closes.</summary>
    public static async Task PipeAsync(Stream stream, WebSocket ws, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var up = Task.Run(async () =>
        {
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, cts.Token)) > 0)
                await ws.SendAsync(buffer.AsMemory(0, read), WebSocketMessageType.Binary, true, cts.Token);
        }, cts.Token);
        var down = Task.Run(async () =>
        {
            var buffer = new byte[16 * 1024];
            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer.AsMemory(), cts.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;
                await stream.WriteAsync(buffer.AsMemory(0, result.Count), cts.Token);
            }
        }, cts.Token);
        await Task.WhenAny(up, down);
        cts.Cancel();
        try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); } catch { }
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
