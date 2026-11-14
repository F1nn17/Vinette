using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Vinette.TestSupport;

namespace Vinette.Api.Tests;

/// <summary>A browser's side of the real-time channel for tests: the SignalR JSON protocol over the test server's WebSocket.</summary>
internal sealed class HubSocket(WebSocket socket) : IDisposable
{
    private const char Separator = '\u001e';

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly Queue<JsonElement> frames = new();
    private readonly byte[] buffer = new byte[16 * 1024];
    private string partial = string.Empty;

    public WebSocket Socket => socket;

    public async Task SendTextAsync(string text) =>
        await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, Token);

    /// <summary>The next frame of the hub that is not a ping, or null if the connection was closed.</summary>
    public async Task<JsonElement?> NextAsync(TimeSpan within)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(within);
        while (true)
        {
            while (frames.Count > 0)
            {
                var frame = frames.Dequeue();
                if (frame.TryGetProperty("type", out var type) && type.GetInt32() == 6)
                {
                    continue; // ping
                }

                return frame;
            }

            if (socket.State is not WebSocketState.Open)
            {
                return null;
            }

            try
            {
                var received = await socket.ReceiveAsync(buffer, timeout.Token);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                partial += Encoding.UTF8.GetString(buffer, 0, received.Count);
            }
            catch (WebSocketException)
            {
                return null;
            }

            var parts = partial.Split(Separator);
            partial = parts[^1];
            foreach (var part in parts[..^1])
            {
                frames.Enqueue(JsonElement.Parse(part));
            }
        }
    }

    /// <summary>The next event of the channel (<c>mailEvent</c>) of the given kind, within the time; null if none came.</summary>
    public async Task<JsonElement?> NextEventAsync(string type, TimeSpan within)
    {
        var until = DateTime.UtcNow + within;
        while (DateTime.UtcNow < until)
        {
            try
            {
                var frame = await NextAsync(until - DateTime.UtcNow);
                if (frame is null)
                {
                    return null;
                }

                if (frame.Value.TryGetProperty("target", out var target) && target.GetString() == "mailEvent"
                    && frame.Value.GetProperty("arguments")[0].GetProperty("type").GetString() == type)
                {
                    return frame.Value.GetProperty("arguments")[0];
                }
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        return null;
    }

    public void Dispose() => socket.Dispose();

    /// <summary>
    /// Connects the way the browser does: with the session cookie and an Origin of the application. With <paramref name="negotiate"/>
    /// the connection is asked for first (the library's way); without it the WebSocket is opened at once, which is what the page
    /// does (so that any instance of the application can take it).
    /// </summary>
    public static async Task<HubSocket> ConnectAsync(WebApplicationFactory<Program> web, string cookie, HttpClient http, bool negotiate = true)
    {
        var url = "ws://localhost/hubs/mail";
        if (negotiate)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/hubs/mail/negotiate?negotiateVersion=1");
            request.Headers.Add("Cookie", cookie);
            request.Headers.Add("Origin", CsrfClient.Origin); // a browser always sends it, and the hub wants it to be ours
            var answer = await http.SendAsync(request, Token);
            answer.EnsureSuccessStatusCode();
            var token = JsonElement.Parse(await answer.Content.ReadAsStringAsync(Token)).GetProperty("connectionToken").GetString();
            url += $"?id={Uri.EscapeDataString(token!)}";
        }

        var client = web.Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            request.Headers["Cookie"] = cookie;
            request.Headers["Origin"] = CsrfClient.Origin;
        };
        var hub = new HubSocket(await client.ConnectAsync(new Uri(url), Token));
        await hub.SendTextAsync($"{{\"protocol\":\"json\",\"version\":1}}{Separator}");
        var handshake = await hub.NextAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(handshake);
        Assert.False(handshake.Value.TryGetProperty("error", out _));
        return hub;
    }
}
