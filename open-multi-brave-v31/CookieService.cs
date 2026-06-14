using System.Net.WebSockets;
using System.Text.Json;

namespace OpenMultiBraveLauncherV3;

internal sealed class CookieService(CdpClient cdpClient)
{
    public async Task<List<Dictionary<string, object?>>> GetShopeeAndBigSellerCookiesAsync()
    {
        var wsUrl = await cdpClient.GetPageWebSocketUrlAsync().ConfigureAwait(false);
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(wsUrl), CancellationToken.None);

        var result = await CdpClient.SendAsync(socket, 1, "Network.getAllCookies", null).ConfigureAwait(false);
        if (!result.TryGetProperty("cookies", out var cookiesEl) || cookiesEl.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<Dictionary<string, object?>>();
        foreach (var cookie in cookiesEl.EnumerateArray())
        {
            var domain = cookie.TryGetProperty("domain", out var dp) ? (dp.GetString() ?? "") : "";
            var lower = domain.ToLowerInvariant();
            if (!lower.Contains("shopee") && !lower.Contains("bigseller"))
                continue;

            var map = new Dictionary<string, object?>();
            foreach (var p in cookie.EnumerateObject())
            {
                map[p.Name] = p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString(),
                    JsonValueKind.Number => p.Value.TryGetInt64(out var i) ? i : p.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => p.Value.ToString(),
                };
            }
            list.Add(map);
        }
        return list;
    }

    public async Task<int> CountDomainCookiesAsync(string domainPart)
    {
        try
        {
            var cookies = await GetShopeeAndBigSellerCookiesAsync().ConfigureAwait(false);
            return cookies.Count(c =>
                c.TryGetValue("domain", out var domain) &&
                (domain?.ToString() ?? "").Contains(domainPart, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return 0;
        }
    }
}
