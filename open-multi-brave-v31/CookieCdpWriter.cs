using System.Net.WebSockets;
using System.Text.Json;

namespace OpenMultiBraveLauncherV3;

internal readonly record struct CookieImportFilter(bool IncludeShopee);

/// <summary>Ghi cookie Shopee vào Brave qua CDP.</summary>
internal static class CookieCdpWriter
{
    public static async Task<int> SetCookiesFromJsonAsync(
        CdpClient client,
        JsonElement cookiesArray,
        CookieImportFilter filter,
        Action<string>? log = null,
        string? preferredPageWsUrl = null,
        CancellationToken cancellationToken = default)
    {
        var wsUrl = string.IsNullOrWhiteSpace(preferredPageWsUrl)
            ? await client.GetPageWebSocketUrlAsync().ConfigureAwait(false)
            : preferredPageWsUrl;
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(wsUrl), cancellationToken).ConfigureAwait(false);
        await CdpClient.SendAsync(socket, 1, "Network.enable", new { }).ConfigureAwait(false);

        var attempted = 0;
        var succeeded = 0;
        var cmdId = 1000;

        foreach (var cookie in cookiesArray.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (cookie.ValueKind != JsonValueKind.Object)
                continue;

            var domain = cookie.TryGetProperty("domain", out var dp) ? (dp.GetString() ?? "") : "";
            var lower = domain.ToLowerInvariant();
            var isShopee = lower.Contains("shopee");
            if (isShopee && !filter.IncludeShopee)
                continue;
            if (!isShopee)
                continue;

            var payload = BuildCookiePayload(cookie, persistSessionCookie: false);
            if (payload is null)
                continue;

            attempted++;
            try
            {
                var setResult = await TrySetSingleCookieAsync(client, socket, cmdId, payload).ConfigureAwait(false);
                cmdId = setResult.NextCmdId;
                if (setResult.Ok)
                    succeeded++;
            }
            catch (Exception ex)
            {
                var cookieName = payload.TryGetValue("name", out var nv) ? nv as string ?? "" : "";
                log?.Invoke($"Cookie lỗi {cookieName}: {ex.Message}");
            }
        }

        if (attempted > 0)
            log?.Invoke($"Cookie import: thử {attempted}, thành công {succeeded}.");
        return succeeded;
    }

    private static async Task<(bool Ok, int NextCmdId)> TrySetSingleCookieAsync(
        CdpClient client,
        ClientWebSocket socket,
        int cmdId,
        Dictionary<string, object?> payload)
    {
        var storageOk = await TrySetCookieWithBrowserStorageAsync(client, payload).ConfigureAwait(false);
        var result = await CdpClient.SendAsync(socket, cmdId++, "Network.setCookie", payload).ConfigureAwait(false);
        var ok = result.TryGetProperty("success", out var sp) && sp.GetBoolean();
        if (!ok)
        {
            var fb = new Dictionary<string, object?>(payload);
            fb.Remove("sourceScheme");
            fb.Remove("sourcePort");
            var fbResult = await CdpClient.SendAsync(socket, cmdId++, "Network.setCookie", fb).ConfigureAwait(false);
            ok = fbResult.TryGetProperty("success", out var fp) && fp.GetBoolean();
        }

        if (!ok && storageOk)
            ok = true;

        return (ok, cmdId);
    }

    private static Dictionary<string, object?>? BuildCookiePayload(JsonElement cookie, bool persistSessionCookie)
    {
        var payload = new Dictionary<string, object?>();
        foreach (var k in new[]
        {
            "name", "value", "url", "domain", "path",
            "secure", "httpOnly", "sameSite", "expires",
            "priority", "sourceScheme", "sourcePort",
        })
        {
            if (!cookie.TryGetProperty(k, out var v))
                continue;

            payload[k] = v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.TryGetInt64(out var i) ? i : v.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            };
        }

        if (!payload.ContainsKey("name") || !payload.ContainsKey("value"))
            return null;

        if (!payload.ContainsKey("url") && !payload.ContainsKey("domain"))
            return null;

        if (!payload.ContainsKey("url") && payload.TryGetValue("domain", out var dv))
        {
            var ds = (dv as string ?? "").TrimStart('.');
            if (!string.IsNullOrEmpty(ds))
                payload["url"] = $"https://{ds}/";
        }

        var cookieName = payload.TryGetValue("name", out var nv) ? nv as string ?? "" : "";
        if (cookieName.StartsWith("__Host-", StringComparison.OrdinalIgnoreCase))
        {
            payload.Remove("domain");
            payload["path"] = "/";
        }

        SanitizeCookiePayloadForCdp(payload, persistSessionCookie);
        return payload;
    }

    private static async Task<bool> TrySetCookieWithBrowserStorageAsync(
        CdpClient client,
        Dictionary<string, object?> payload)
    {
        try
        {
            using var browser = new ClientWebSocket();
            await browser.ConnectAsync(
                new Uri(await client.GetBrowserWebSocketUrlAsync().ConfigureAwait(false)),
                CancellationToken.None).ConfigureAwait(false);
            await CdpClient.SendAsync(browser, 700, "Storage.setCookies", new { cookies = new[] { payload } })
                .ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static void SanitizeCookiePayloadForCdp(
        Dictionary<string, object?> payload,
        bool persistSessionCookie)
    {
        foreach (var key in payload.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList())
            payload.Remove(key);

        foreach (var key in new[] { "name", "value", "url", "domain", "path", "sameSite", "priority", "sourceScheme" })
        {
            if (payload.TryGetValue(key, out var value) &&
                value is string s &&
                string.IsNullOrWhiteSpace(s))
                payload.Remove(key);
        }

        if (payload.TryGetValue("sameSite", out var sameSite) && sameSite is string ss)
        {
            var normalized = ss.Trim();
            if (normalized.Equals("no_restriction", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("none", StringComparison.OrdinalIgnoreCase))
                payload["sameSite"] = "None";
            else if (normalized.Equals("lax", StringComparison.OrdinalIgnoreCase))
                payload["sameSite"] = "Lax";
            else if (normalized.Equals("strict", StringComparison.OrdinalIgnoreCase))
                payload["sameSite"] = "Strict";
            else
                payload.Remove("sameSite");
        }

        if (payload.TryGetValue("expires", out var expires))
        {
            var value = expires switch
            {
                long l => l,
                int i => i,
                double d => d,
                _ => 0,
            };
            if (value <= 0)
            {
                if (persistSessionCookie)
                    payload["expires"] = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
                else
                    payload.Remove("expires");
            }
        }
        else if (persistSessionCookie)
        {
            payload["expires"] = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
        }

        if (payload.TryGetValue("sourcePort", out var sourcePort))
        {
            var value = sourcePort switch
            {
                long l => l,
                int i => i,
                double d => d,
                _ => 0,
            };
            if (value < 0)
                payload.Remove("sourcePort");
        }
    }
}
