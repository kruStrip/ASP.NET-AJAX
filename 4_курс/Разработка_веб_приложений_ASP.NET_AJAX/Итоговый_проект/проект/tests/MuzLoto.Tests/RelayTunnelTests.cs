using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MuzLoto.Web.Services.Messaging;
using Xunit;

namespace MuzLoto.Tests;

/// <summary>
/// The CONNECT proxy for the WhatsApp gateway, end to end without Cloudflare: a stand-in relay does what the Worker
/// does (WebSocket in, TCP out), and an echo server plays WhatsApp.
/// </summary>
[CollectionDefinition(nameof(RelayTunnelTests), DisableParallelization = true)]
[Collection(nameof(RelayTunnelTests))]
public sealed class RelayTunnelTests
{
    [Fact]
    public async Task Connect_TunnelsBytesThroughTheRelay_AndRejectsWrongPassword()
    {
        // "WhatsApp": echoes what it receives.
        var echo = new TcpListener(IPAddress.Loopback, 0);
        echo.Start();
        var echoPort = ((IPEndPoint)echo.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                var c = await echo.AcceptTcpClientAsync();
                _ = Task.Run(async () => { using (c) await c.GetStream().CopyToAsync(c.GetStream()); });
            }
        });

        // "Cloudflare Worker": checks the token, opens TCP to host:port, pipes bytes.
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var relay = builder.Build();
        relay.UseWebSockets();
        relay.Map("/tunnel", async (HttpContext http) =>
        {
            if (http.Request.Headers.Authorization != "Bearer relay-token") { http.Response.StatusCode = 403; return; }
            using var ws = await http.WebSockets.AcceptWebSocketAsync();
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(http.Request.Query["host"]!, int.Parse(http.Request.Query["port"]!));
            await RelayTunnel.PipeAsync(tcp.GetStream(), ws, CancellationToken.None);
        });
        await relay.StartAsync();
        var relayUrl = relay.Urls.Single().Replace("http://", "ws://") + "/tunnel";

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Relay:Url"] = relayUrl, ["Relay:Token"] = "relay-token", ["Relay:ProxyPassword"] = "secret", ["Relay:Mode"] = "websocket"
        }).Build();
        var tunnel = new RelayTunnel(config, NullLogger<RelayTunnel>.Instance);
        using var stop = new CancellationTokenSource();
        await tunnel.StartAsync(stop.Token);
        try
        {
            async Task<(NetworkStream Stream, string Reply, TcpClient Client)> ConnectAsync(string password)
            {
                var client = new TcpClient();
                for (var i = 0; ; i++)
                {
                    try { await client.ConnectAsync(IPAddress.Loopback, RelayTunnel.Port); break; }
                    catch (SocketException) when (i < 20) { await Task.Delay(100); }
                }
                var stream = client.GetStream();
                var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes("waha:" + password));
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{echoPort} HTTP/1.1\r\nHost: x\r\nProxy-Authorization: Basic {auth}\r\n\r\n"));
                var buffer = new byte[256];
                var read = await stream.ReadAsync(buffer);
                return (stream, Encoding.ASCII.GetString(buffer, 0, read), client);
            }

            var (bad, badReply, badClient) = await ConnectAsync("wrong");
            using (badClient) Assert.StartsWith("HTTP/1.1 407", badReply);

            var (stream, reply, client) = await ConnectAsync("secret");
            using (client)
            {
                Assert.StartsWith("HTTP/1.1 200", reply);
                var payload = Encoding.UTF8.GetBytes("hello whatsapp");
                await stream.WriteAsync(payload);
                var back = new byte[payload.Length];
                var total = 0;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (total < back.Length) total += await stream.ReadAsync(back.AsMemory(total), timeout.Token);
                Assert.Equal(payload, back);
            }
        }
        finally
        {
            stop.Cancel();
            await tunnel.StopAsync(CancellationToken.None);
            await relay.StopAsync();
            echo.Stop();
        }
    }

    /// <summary>A stand-in for the Worker's Durable Object: one TCP connection per id, bytes in and out by offset.</summary>
    private sealed class FakeSession
    {
        public required TcpClient Tcp { get; init; }
        public readonly List<byte> Down = new();
        public long UpOffset;
        public bool Closed;
    }

    [Fact]
    public async Task ChunkedMode_Carries200KB_InSmallRequests_AndSurvivesFailedRequests()
    {
        var echo = new TcpListener(IPAddress.Loopback, 0);
        echo.Start();
        var echoPort = ((IPEndPoint)echo.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                var c = await echo.AcceptTcpClientAsync();
                _ = Task.Run(async () => { using (c) await c.GetStream().CopyToAsync(c.GetStream()); });
            }
        });

        var sessions = new System.Collections.Concurrent.ConcurrentDictionary<string, FakeSession>();
        var requests = 0;
        var largestBody = 0;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var relay = builder.Build();
        relay.Use(async (http, next) =>
        {
            if (http.Request.Headers.Authorization != "Bearer relay-token") { http.Response.StatusCode = 403; return; }
            // Every 5th request fails before doing anything: the site must retry without losing or doubling bytes.
            if (Interlocked.Increment(ref requests) % 5 == 0) { http.Response.StatusCode = 503; return; }
            await next(http);
        });
        relay.MapPost("/s/open", async (HttpContext http) =>
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(http.Request.Query["host"]!, int.Parse(http.Request.Query["port"]!));
            var id = Guid.NewGuid().ToString();
            var session = new FakeSession { Tcp = tcp };
            sessions[id] = session;
            _ = Task.Run(async () =>
            {
                var buffer = new byte[3000];
                int read;
                while ((read = await tcp.GetStream().ReadAsync(buffer)) > 0)
                    lock (session.Down) session.Down.AddRange(buffer.AsSpan(0, read).ToArray());
                session.Closed = true;
            });
            return Results.Json(new { id });
        });
        relay.MapPost("/s/{id}/up", async (string id, long at, HttpContext http) =>
        {
            var session = sessions[id];
            using var body = new MemoryStream();
            await http.Request.Body.CopyToAsync(body);
            largestBody = Math.Max(largestBody, (int)body.Length);
            if (at < session.UpOffset) return Results.Ok("duplicate");
            if (at > session.UpOffset) return Results.StatusCode(409);
            session.UpOffset += body.Length;
            await session.Tcp.GetStream().WriteAsync(body.ToArray());
            return Results.Ok("ok");
        });
        relay.MapGet("/s/{id}/down", async (string id, long from) =>
        {
            var session = sessions[id];
            for (var i = 0; i < 50; i++)
            {
                lock (session.Down)
                    if (session.Down.Count > from)
                        return Results.Bytes(session.Down.Skip((int)from).Take(8 * 1024).ToArray());
                await Task.Delay(20);
            }
            return session.Closed ? Results.StatusCode(410) : Results.NoContent();
        });
        relay.MapPost("/s/{id}/close", (string id) => { if (sessions.TryRemove(id, out var s)) s.Tcp.Dispose(); return Results.Ok(); });
        await relay.StartAsync();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Relay:Url"] = relay.Urls.Single().Replace("http://", "ws://") + "/tunnel", ["Relay:Token"] = "relay-token"
        }).Build();
        var tunnel = new RelayTunnel(config, NullLogger<RelayTunnel>.Instance);
        using var stop = new CancellationTokenSource();
        await tunnel.StartAsync(stop.Token);
        try
        {
            using var client = new TcpClient();
            for (var i = 0; ; i++)
            {
                try { await client.ConnectAsync(IPAddress.Loopback, RelayTunnel.Port); break; }
                catch (SocketException) when (i < 20) { await Task.Delay(100); }
            }
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{echoPort} HTTP/1.1\r\nHost: x\r\n\r\n"));
            var head = new byte[64];
            var headRead = await stream.ReadAsync(head);
            Assert.StartsWith("HTTP/1.1 200", Encoding.ASCII.GetString(head, 0, headRead));

            var payload = new byte[200 * 1024];
            Random.Shared.NextBytes(payload);
            var writing = stream.WriteAsync(payload).AsTask();
            var back = new byte[payload.Length];
            var total = 0;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (total < back.Length) total += await stream.ReadAsync(back.AsMemory(total), timeout.Token);
            await writing;
            Assert.Equal(payload, back);                       // nothing lost, nothing doubled, in order
            Assert.True(largestBody <= 8 * 1024, $"a piece of {largestBody} B is larger than 8 KB");
        }
        finally
        {
            stop.Cancel();
            await tunnel.StopAsync(CancellationToken.None);
            await relay.StopAsync();
            echo.Stop();
        }
    }
}
