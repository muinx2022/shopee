using System.Net.WebSockets;
using System.Text;
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

    /// <summary>
    /// Dấu vân tay của token đăng nhập hiện tại — đổi nghĩa là BigSeller vừa xoay token,
    /// lúc đó mới đáng ghi cookie ngược lại file account.
    /// </summary>
    public static string BuildAuthStamp(IEnumerable<Dictionary<string, object?>> cookies) =>
        string.Join("\n", cookies
            .Where(c =>
                IsBigSellerCookie(c) &&
                string.Equals(c.GetValueOrDefault("name") as string, AuthCookieName, StringComparison.OrdinalIgnoreCase))
            .Select(c => $"{c.GetValueOrDefault("domain")}|{c.GetValueOrDefault("value")}")
            .OrderBy(s => s, StringComparer.Ordinal));

    public static async Task<List<Dictionary<string, object?>>> GetBigSellerCookiesAsync(int debugPort)
    {
        var cookies = await new CookieService(new CdpClient(debugPort))
            .GetShopeeAndBigSellerCookiesAsync().ConfigureAwait(false);
        return cookies.Where(IsBigSellerCookie).ToList();
    }

    public static bool TryWriteCookieFile(
        string cookieFile,
        IReadOnlyCollection<Dictionary<string, object?>> bigSellerCookies,
        Action<string>? log = null)
    {
        // Tên tmp phải unique: nhiều instance cùng account (nhiều shop song song) có thể
        // write-back đồng thời — tmp cố định sẽ ghi đè lẫn nhau giữa chừng.
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

            // Move fail khi đúng lúc có instance khác đang đọc file đích — chờ ngắn rồi thử lại.
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
            log?.Invoke($"Cookie: khong luu duoc cookie BigSeller ra file: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Kiểm tra phiên BigSeller trong browser còn đăng nhập THẬT không — có token trong cookie
    /// chưa đủ vì token có thể đã bị server thu hồi. Điều hướng tới trang app rồi xem có bị
    /// đá khỏi khu /web/ (về trang login/trang chủ) không.
    /// Trả về null khi không xác định được (CDP lỗi / trang load quá lâu) — caller tự quyết fallback.
    /// </summary>
    public static async Task<bool?> ProbeLoggedInAsync(
        int debugPort,
        string? probeUrl = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var url = string.IsNullOrWhiteSpace(probeUrl) ? DefaultListingUrl : probeUrl;
        try
        {
            var wsUrl = await new CdpClient(debugPort)
                .EnsurePageTargetAsync(IsBigSellerUrl, url).ConfigureAwait(false);
            using var page = new ClientWebSocket();
            await page.ConnectAsync(new Uri(wsUrl), cancellationToken).ConfigureAwait(false);
            await SendCdpAsync(page, 60, "Page.navigate", new { url }).ConfigureAwait(false);

            var stableOkPolls = 0;
            for (var i = 0; i < 40; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);

                string href;
                string ready;
                try
                {
                    var result = await SendCdpAsync(page, 61 + i, "Runtime.evaluate", new
                    {
                        expression = "JSON.stringify({href: location.href, ready: document.readyState})",
                        returnByValue = true,
                    }).ConfigureAwait(false);
                    if (!result.TryGetProperty("result", out var rv) ||
                        !rv.TryGetProperty("value", out var vv))
                        continue;

                    using var doc = JsonDocument.Parse(vv.GetString() ?? "{}");
                    href = doc.RootElement.TryGetProperty("href", out var h) ? h.GetString() ?? "" : "";
                    ready = doc.RootElement.TryGetProperty("ready", out var r) ? r.GetString() ?? "" : "";
                }
                catch
                {
                    continue; // dang redirect, context bi huy — poll tiep
                }

                if (IsLoginUrl(href))
                    return false;

                if (!string.Equals(ready, "complete", StringComparison.OrdinalIgnoreCase))
                {
                    stableOkPolls = 0;
                    continue;
                }

                // Load xong nhưng đã bị đá khỏi khu app /web/ (về trang chủ) -> mất phiên.
                if (!href.Contains("/web/", StringComparison.OrdinalIgnoreCase))
                    return false;

                // SPA có thể redirect về login SAU khi readyState=complete — chờ ổn định vài nhịp.
                if (++stableOkPolls >= 3)
                    return true;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log?.Invoke($"Cookie: khong probe duoc trang BigSeller: {ex.Message}");
        }

        return null;
    }

    private static bool IsLoginUrl(string url) =>
        url.Contains("login", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("passport", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("signin", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Lưu cookie BigSeller đang sống trong browser ngược lại file account để file luôn "tươi".
    /// Chỉ ghi khi browser thật sự còn token đăng nhập — tránh ghi đè file bằng phiên đã chết.
    /// </summary>
    public static async Task<bool> TryExportProfileCookiesToFileAsync(
        int debugPort,
        string? cookieFile,
        Action<string>? log = null,
        bool verifySessionAlive = false)
    {
        var file = (cookieFile ?? "").Trim();
        if (string.IsNullOrWhiteSpace(file))
            return false;

        try
        {
            var bigseller = await GetBigSellerCookiesAsync(debugPort).ConfigureAwait(false);
            if (!HasAuthCookie(bigseller))
                return false;

            // Token chết vẫn nằm trong jar sau khi import — export mù sẽ "đóng dấu tươi"
            // cho file account bằng cookie chết. Khi caller chưa probe thì bắt buộc probe ở đây.
            if (verifySessionAlive &&
                await ProbeLoggedInAsync(debugPort, log: log).ConfigureAwait(false) != true)
            {
                log?.Invoke("Cookie: phien BigSeller khong con song — bo qua luu cookie ra file.");
                return false;
            }

            if (!TryWriteCookieFile(file, bigseller, log))
                return false;

            log?.Invoke($"Cookie: da luu {bigseller.Count} cookie BigSeller moi vao file account ({Path.GetFileName(file)}).");
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"Cookie: khong luu duoc cookie BigSeller ra file: {ex.Message}");
            return false;
        }
    }

    public static async Task<int> ImportFromFileAsync(
        int debugPort,
        string cookieFile,
        Action<string>? log = null,
        bool reloadBigSellerTabs = true,
        string? navigateUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cookieFile))
        {
            log?.Invoke("Account chua co BigSeller cookie, bo qua import cookie.");
            return 0;
        }

        if (!File.Exists(cookieFile))
        {
            log?.Invoke($"Khong tim thay BigSeller cookie: {cookieFile}");
            return 0;
        }

        if (!await WaitForCdpReadyAsync(debugPort, cancellationToken).ConfigureAwait(false))
        {
            log?.Invoke($"CDP port {debugPort} chua san sang de import cookie.");
            return 0;
        }

        var json = await ReadCookieJsonAsync(cookieFile, cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var cookiesEl = doc.RootElement.TryGetProperty("cookies", out var cp) ? cp : doc.RootElement;
        if (cookiesEl.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("File cookie BigSeller khong hop le.");

        log?.Invoke($"Dang nap cookie BigSeller tu account: {cookieFile}");
        var count = await SetCookiesToBraveAsync(debugPort, cookiesEl, log).ConfigureAwait(false);

        if (count > 0)
        {
            if (!string.IsNullOrWhiteSpace(navigateUrl))
            {
                await NavigateBigSellerTabsAsync(debugPort, navigateUrl).ConfigureAwait(false);
                log?.Invoke($"Da dieu huong BigSeller toi: {navigateUrl}");
            }
            else if (reloadBigSellerTabs)
            {
                await ReloadBigSellerTabsAsync(debugPort).ConfigureAwait(false);
            }

            await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
        }

        log?.Invoke($"Da import {count} cookie BigSeller tu account.");
        return count;
    }

    private static async Task<string> ReadCookieJsonAsync(string cookieFile, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(cookieFile);
        using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> WaitForCdpReadyAsync(int debugPort, CancellationToken cancellationToken)
    {
        for (var i = 0; i < 40; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var response = await AppServices.DirectHttp
                    .GetAsync($"http://127.0.0.1:{debugPort}/json/version", cancellationToken)
                    .ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    return true;
            }
            catch
            {
                // retry
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static async Task<int> SetCookiesToBraveAsync(int debugPort, JsonElement cookiesArray, Action<string>? log)
    {
        var wsUrl = await GetCdpWebSocketUrlAsync(debugPort).ConfigureAwait(false);
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(wsUrl), CancellationToken.None).ConfigureAwait(false);
        await SendCdpAsync(socket, 1, "Network.enable", new { }).ConfigureAwait(false);

        var attempted = 0;
        var succeeded = 0;
        var cmdId = 1000;

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
                var storageOk = await TrySetCookieWithBrowserStorageAsync(debugPort, payload).ConfigureAwait(false);
                var result = await SendCdpAsync(socket, cmdId++, "Network.setCookie", payload).ConfigureAwait(false);
                var ok = result.TryGetProperty("success", out var sp) && sp.GetBoolean();
                if (!ok)
                {
                    var fb = new Dictionary<string, object?>(payload);
                    fb.Remove("sourceScheme");
                    fb.Remove("sourcePort");
                    var fbResult = await SendCdpAsync(socket, cmdId++, "Network.setCookie", fb).ConfigureAwait(false);
                    ok = fbResult.TryGetProperty("success", out var fp) && fp.GetBoolean();
                }

                if (!ok && storageOk)
                    ok = true;

                if (TryBuildBigSellerProPayload(payload, out var proPayload))
                {
                    try
                    {
                        var proStorageOk = await TrySetCookieWithBrowserStorageAsync(debugPort, proPayload).ConfigureAwait(false);
                        var proResult = await SendCdpAsync(socket, cmdId++, "Network.setCookie", proPayload).ConfigureAwait(false);
                        var proOk = proResult.TryGetProperty("success", out var psp) && psp.GetBoolean();
                        if (!proOk)
                        {
                            var fb = new Dictionary<string, object?>(proPayload);
                            fb.Remove("sourceScheme");
                            fb.Remove("sourcePort");
                            var fbResult = await SendCdpAsync(socket, cmdId++, "Network.setCookie", fb).ConfigureAwait(false);
                            proOk = fbResult.TryGetProperty("success", out var pfp) && pfp.GetBoolean();
                        }

                        _ = proOk || proStorageOk;
                    }
                    catch
                    {
                        // compatibility copy only
                    }
                }

                if (ok)
                    succeeded++;
            }
            catch (Exception ex)
            {
                var cookieName = payload.TryGetValue("name", out var nv) ? nv as string ?? "" : "";
                log?.Invoke($"Cookie loi {cookieName}: {ex.Message}");
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

        var cookieName = payload.TryGetValue("name", out var nv) ? nv as string ?? "" : "";
        if (cookieName.StartsWith("__Host-", StringComparison.OrdinalIgnoreCase))
        {
            payload.Remove("domain");
            payload["path"] = "/";
        }

        SanitizeCookiePayloadForCdp(payload, persistSessionCookie: true);
        return payload;
    }

    private static async Task<bool> TrySetCookieWithBrowserStorageAsync(
        int debugPort,
        Dictionary<string, object?> payload)
    {
        try
        {
            using var browser = new ClientWebSocket();
            await browser.ConnectAsync(new Uri(await GetBrowserWebSocketUrlAsync(debugPort).ConfigureAwait(false)), CancellationToken.None)
                .ConfigureAwait(false);
            await SendCdpAsync(browser, 700, "Storage.setCookies", new { cookies = new[] { payload } })
                .ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task ReloadBigSellerTabsAsync(int debugPort) =>
        await NavigateBigSellerTabsAsync(debugPort, reloadOnly: true).ConfigureAwait(false);

    private static async Task NavigateBigSellerTabsAsync(
        int debugPort,
        string? targetUrl = null,
        bool reloadOnly = false)
    {
        try
        {
            using var response = await AppServices.DirectHttp
                .GetAsync($"http://127.0.0.1:{debugPort}/json/list")
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            var navigated = false;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var type = item.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (!string.Equals(type, "page", StringComparison.OrdinalIgnoreCase))
                    continue;

                var url = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                if (!IsBigSellerUrl(url))
                    continue;

                var ws = item.TryGetProperty("webSocketDebuggerUrl", out var wsProp)
                    ? wsProp.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(ws))
                    continue;

                using var page = new ClientWebSocket();
                await page.ConnectAsync(new Uri(ws), CancellationToken.None).ConfigureAwait(false);
                if (reloadOnly || string.IsNullOrWhiteSpace(targetUrl))
                    await SendCdpAsync(page, 91, "Page.reload", new { ignoreCache = true }).ConfigureAwait(false);
                else
                    await SendCdpAsync(page, 92, "Page.navigate", new { url = targetUrl }).ConfigureAwait(false);
                navigated = true;
            }

            if (!navigated && !reloadOnly && !string.IsNullOrWhiteSpace(targetUrl))
                await CreateBigSellerTabAndNavigateAsync(debugPort, targetUrl).ConfigureAwait(false);
        }
        catch
        {
            // navigation is best-effort
        }
    }

    private static async Task CreateBigSellerTabAndNavigateAsync(int debugPort, string targetUrl)
    {
        using var browser = new ClientWebSocket();
        await browser.ConnectAsync(new Uri(await GetBrowserWebSocketUrlAsync(debugPort).ConfigureAwait(false)), CancellationToken.None)
            .ConfigureAwait(false);
        await SendCdpAsync(browser, 90, "Target.createTarget", new { url = targetUrl }).ConfigureAwait(false);
    }

    private static Task<string> GetCdpWebSocketUrlAsync(int debugPort) =>
        new CdpClient(debugPort).GetPageWebSocketUrlAsync();
    private static Task<string> GetBrowserWebSocketUrlAsync(int debugPort) =>
        new CdpClient(debugPort).GetBrowserWebSocketUrlAsync();
    private static Task<JsonElement> SendCdpAsync(ClientWebSocket socket, int id, string method, object? @params) =>
        CdpClient.SendAsync(socket, id, method, @params);
    private static bool TryBuildBigSellerProPayload(
        Dictionary<string, object?> source,
        out Dictionary<string, object?> payload)
    {
        payload = new Dictionary<string, object?>(source);
        var changed = false;

        if (payload.TryGetValue("domain", out var domainValue) &&
            domainValue is string domain &&
            domain.Contains("bigseller.com", StringComparison.OrdinalIgnoreCase))
        {
            payload["domain"] = domain.Replace("bigseller.com", "bigseller.pro", StringComparison.OrdinalIgnoreCase);
            changed = true;
        }

        if (payload.TryGetValue("url", out var urlValue) &&
            urlValue is string url &&
            url.Contains("bigseller.com", StringComparison.OrdinalIgnoreCase))
        {
            payload["url"] = url.Replace("bigseller.com", "bigseller.pro", StringComparison.OrdinalIgnoreCase);
            changed = true;
        }

        return changed;
    }

    private static void SanitizeCookiePayloadForCdp(
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

    private static bool IsBigSellerUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Host.Contains("bigseller", StringComparison.OrdinalIgnoreCase);
}
