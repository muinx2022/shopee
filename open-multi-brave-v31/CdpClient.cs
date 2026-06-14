using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace OpenMultiBraveLauncherV3;

internal sealed class CdpClient(int cdpPort)
{
    public int Port { get; } = cdpPort;

    public async Task<string> GetPageWebSocketUrlAsync()
    {
        using var response = await AppServices.DirectHttp.GetAsync($"http://127.0.0.1:{Port}/json/list");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("CDP /json/list khong hop le.");

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var type = item.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (!string.Equals(type, "page", StringComparison.OrdinalIgnoreCase))
                continue;

            if (item.TryGetProperty("webSocketDebuggerUrl", out var wsProp))
            {
                var ws = wsProp.GetString();
                if (!string.IsNullOrWhiteSpace(ws))
                    return ws;
            }
        }

        throw new InvalidOperationException($"Khong co tab tren CDP port {Port}.");
    }

    public async Task<string> GetBrowserWebSocketUrlAsync()
    {
        using var response = await AppServices.DirectHttp.GetAsync($"http://127.0.0.1:{Port}/json/version");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("webSocketDebuggerUrl").GetString()
               ?? throw new InvalidOperationException("CDP /json/version thieu browser WebSocket.");
    }

    public async Task<string?> FindPageWebSocketUrlAsync(Func<string, bool> urlMatches)
    {
        using var response = await AppServices.DirectHttp.GetAsync($"http://127.0.0.1:{Port}/json/list");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var type = item.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (!string.Equals(type, "page", StringComparison.OrdinalIgnoreCase))
                continue;

            var url = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            if (!urlMatches(url))
                continue;

            var ws = item.TryGetProperty("webSocketDebuggerUrl", out var wsProp)
                ? wsProp.GetString()
                : null;
            if (!string.IsNullOrWhiteSpace(ws))
                return ws;
        }

        return null;
    }

    public async Task<string> EnsurePageTargetAsync(Func<string, bool> urlMatches, string createUrl)
    {
        var existing = await FindPageWebSocketUrlAsync(urlMatches).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(existing))
            return existing;

        using var browser = new ClientWebSocket();
        await browser.ConnectAsync(new Uri(await GetBrowserWebSocketUrlAsync().ConfigureAwait(false)), CancellationToken.None);
        await SendAsync(browser, 90, "Target.createTarget", new
        {
            url = createUrl,
            background = true,
        }).ConfigureAwait(false);

        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(300).ConfigureAwait(false);
            var ws = await FindPageWebSocketUrlAsync(urlMatches).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(ws))
                return ws;
        }

        return await GetPageWebSocketUrlAsync().ConfigureAwait(false);
    }

    public async Task ReloadPageTargetsAsync(Func<string, bool> urlMatches)
    {
        using var response = await AppServices.DirectHttp.GetAsync($"http://127.0.0.1:{Port}/json/list");
        if (!response.IsSuccessStatusCode)
            return;

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var type = item.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (!string.Equals(type, "page", StringComparison.OrdinalIgnoreCase))
                continue;

            var url = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            if (!urlMatches(url))
                continue;

            var ws = item.TryGetProperty("webSocketDebuggerUrl", out var wsProp)
                ? wsProp.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(ws))
                continue;

            using var page = new ClientWebSocket();
            await page.ConnectAsync(new Uri(ws), CancellationToken.None);
            await SendAsync(page, 91, "Page.reload", new { ignoreCache = true }).ConfigureAwait(false);
        }
    }

    public static async Task<JsonElement> SendAsync(ClientWebSocket socket, int id, string method, object? @params)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { id, method, @params }));
        await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);

        var buffer = new byte[1024 * 512];
        while (true)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult recv;
            do
            {
                recv = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (recv.MessageType == WebSocketMessageType.Close)
                    throw new InvalidOperationException("CDP socket dong.");
                ms.Write(buffer, 0, recv.Count);
            } while (!recv.EndOfMessage);

            using var doc = JsonDocument.Parse(ms.ToArray());
            var root = doc.RootElement;
            if (!root.TryGetProperty("id", out var idProp) || idProp.GetInt32() != id)
                continue;
            if (root.TryGetProperty("error", out var err))
                throw new InvalidOperationException($"CDP error: {err}");
            if (!root.TryGetProperty("result", out var result))
                throw new InvalidOperationException("CDP result thieu.");
            return result.Clone();
        }
    }
}
