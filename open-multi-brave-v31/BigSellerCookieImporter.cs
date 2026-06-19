using System.Net.WebSockets;
using System.Text.Json;

namespace OpenMultiBraveLauncherV3;

internal static class BigSellerCookieImporter
{
    public const string DefaultListingUrl =
        "https://www.bigseller.com/web/listing/shopee/index.htm?bsStatus=1";

    /// <summary>Cookie giữ phiên đăng nhập BigSeller — còn giá trị nghĩa là browser đang có phiên sống.</summary>
    public const string AuthCookieName = "muc_token";

    public static bool IsBigSellerCookie(Dictionary<string, object?> cookie) =>
        (cookie.GetValueOrDefault("domain") as string ?? "")
            .Contains("bigseller", StringComparison.OrdinalIgnoreCase);

    public static bool HasAuthCookie(IEnumerable<Dictionary<string, object?>> cookies) =>
        cookies.Any(c =>
            IsBigSellerCookie(c) &&
            string.Equals(c.GetValueOrDefault("name") as string, AuthCookieName, StringComparison.OrdinalIgnoreCase) &&
            (c.GetValueOrDefault("value") as string ?? "").Length > 5);

    public static async Task<List<Dictionary<string, object?>>> GetBigSellerCookiesAsync(int debugPort)
    {
        var all = await GetAllCookiesFromBrowserAsync(debugPort).ConfigureAwait(false);
        return all.Where(IsBigSellerCookie).ToList();
    }

    public static bool TryWriteCookieFile(
        string cookieFile,
        IReadOnlyCollection<Dictionary<string, object?>> bigSellerCookies,
        Action<string>? log = null)
    {
        // Tên tmp unique để tránh race khi nhiều instance write đồng thời.
        var tmp = $"{cookieFile}.{Environment.ProcessId}-{Guid.NewGuid():N}.tmp";
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(cookieFile));
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(
                new { exportedAt = DateTimeOffset.Now, cookies = bigSellerCookies },
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(tmp, json);

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(tmp, cookieFile, overwrite: true);
                    return true;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(150);
                }
            }
        }
        catch (Exception ex)
        {
            try { File.Delete(tmp); } catch { }
            log?.Invoke($"BigSeller cookie: không lưu được ra file: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Import BigSeller cookie từ file vào browser đang chạy qua CDP.
    /// KHÔNG dùng proxy của instance — CDP là kết nối local đến browser.
    /// </summary>
    public static async Task<int> ImportFromFileAsync(
        int debugPort,
        string cookieFile,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cookieFile))
        {
            log?.Invoke("Account chưa cấu hình BigSeller cookie file — bỏ qua.");
            return 0;
        }

        if (!File.Exists(cookieFile))
        {
            log?.Invoke($"BigSeller cookie file không tìm thấy: {cookieFile}");
            return 0;
        }

        var json = await File.ReadAllTextAsync(cookieFile, cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var cookiesEl = doc.RootElement.TryGetProperty("cookies", out var cp) ? cp : doc.RootElement;
        if (cookiesEl.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("File cookie BigSeller không hợp lệ (mảng cookies không tìm thấy).");

        log?.Invoke("Đang nạp cookie BigSeller vào browser...");
        var count = await SetBigSellerCookiesToBraveAsync(debugPort, cookiesEl, log).ConfigureAwait(false);
        log?.Invoke($"BigSeller: đã import {count} cookie.");
        return count;
    }

    private static async Task<List<Dictionary<string, object?>>> GetAllCookiesFromBrowserAsync(int debugPort)
    {
        var wsUrl = await new CdpClient(debugPort).GetBrowserWebSocketUrlAsync().ConfigureAwait(false);
        using var browser = new ClientWebSocket();
        await browser.ConnectAsync(new Uri(wsUrl), CancellationToken.None).ConfigureAwait(false);
        var result = await CdpClient.SendAsync(browser, 1, "Storage.getCookies", null).ConfigureAwait(false);
        if (!result.TryGetProperty("cookies", out var cookiesEl) || cookiesEl.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<Dictionary<string, object?>>();
        foreach (var cookie in cookiesEl.EnumerateArray())
        {
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

    private static async Task<int> SetBigSellerCookiesToBraveAsync(
        int debugPort,
        JsonElement cookiesArray,
        Action<string>? log)
    {
        var wsUrl = await new CdpClient(debugPort).GetPageWebSocketUrlAsync().ConfigureAwait(false);
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(wsUrl), CancellationToken.None).ConfigureAwait(false);
        await CdpClient.SendAsync(socket, 1, "Network.enable", new { }).ConfigureAwait(false);

        var attempted = 0;
        var succeeded = 0;
        var cmdId = 2000;

        foreach (var cookie in cookiesArray.EnumerateArray())
        {
            if (cookie.ValueKind != JsonValueKind.Object)
                continue;

            var domain = cookie.TryGetProperty("domain", out var dp) ? (dp.GetString() ?? "") : "";
            if (!domain.Contains("bigseller", StringComparison.OrdinalIgnoreCase))
                continue;

            var payload = BuildCookiePayload(cookie);
            if (payload is null)
                continue;

            attempted++;
            try
            {
                await TrySetCookieWithBrowserStorageAsync(debugPort, payload).ConfigureAwait(false);

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

                // Copy sang bigseller.pro cho compatibility
                if (TryBuildProPayload(payload, out var proPayload))
                {
                    await TrySetCookieWithBrowserStorageAsync(debugPort, proPayload).ConfigureAwait(false);
                    try { await CdpClient.SendAsync(socket, cmdId++, "Network.setCookie", proPayload).ConfigureAwait(false); } catch { }
                }

                if (ok) succeeded++;
            }
            catch (Exception ex)
            {
                var name = payload.TryGetValue("name", out var nv) ? nv as string ?? "" : "";
                log?.Invoke($"Cookie {name}: {ex.Message}");
            }
        }

        return succeeded;
    }

    private static Dictionary<string, object?>? BuildCookiePayload(JsonElement cookie)
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

        if (!payload.ContainsKey("url") && payload.TryGetValue("domain", out var dv))
        {
            var ds = (dv as string ?? "").TrimStart('.');
            if (!string.IsNullOrEmpty(ds))
                payload["url"] = $"https://{ds}/";
        }

        if (!payload.ContainsKey("url") && !payload.ContainsKey("domain"))
            return null;

        CookieCdpWriter.SanitizeCookiePayloadForCdp(payload, persistSessionCookie: true);
        return payload;
    }

    private static bool TryBuildProPayload(
        Dictionary<string, object?> source,
        out Dictionary<string, object?> payload)
    {
        payload = new Dictionary<string, object?>(source);
        var changed = false;
        if (payload.TryGetValue("domain", out var d) && d is string domain &&
            domain.Contains("bigseller.com", StringComparison.OrdinalIgnoreCase))
        {
            payload["domain"] = domain.Replace("bigseller.com", "bigseller.pro", StringComparison.OrdinalIgnoreCase);
            changed = true;
        }
        if (payload.TryGetValue("url", out var u) && u is string url &&
            url.Contains("bigseller.com", StringComparison.OrdinalIgnoreCase))
        {
            payload["url"] = url.Replace("bigseller.com", "bigseller.pro", StringComparison.OrdinalIgnoreCase);
            changed = true;
        }
        return changed;
    }

    private static async Task<bool> TrySetCookieWithBrowserStorageAsync(int debugPort, Dictionary<string, object?> payload)
    {
        try
        {
            using var browser = new ClientWebSocket();
            await browser.ConnectAsync(
                new Uri(await new CdpClient(debugPort).GetBrowserWebSocketUrlAsync().ConfigureAwait(false)),
                CancellationToken.None).ConfigureAwait(false);
            await CdpClient.SendAsync(browser, 700, "Storage.setCookies", new { cookies = new[] { payload } })
                .ConfigureAwait(false);
            return true;
        }
        catch { return false; }
    }
}
