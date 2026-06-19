using System.Net.WebSockets;
using System.Text.Json;

namespace UpdateProduct;

internal static class BigSellerCookieImporter
{
    public const string DefaultListingUrl =
        "https://www.bigseller.com/web/listing/shopee/index.htm?bsStatus=1";

    public const string AuthCookieName = "muc_token";

    public static bool IsBigSellerCookie(Dictionary<string, object?> cookie) =>
        (cookie.GetValueOrDefault("domain") as string ?? "")
            .Contains("bigseller", StringComparison.OrdinalIgnoreCase);

    public static bool HasAuthCookie(IEnumerable<Dictionary<string, object?>> cookies) =>
        cookies.Any(c =>
            IsBigSellerCookie(c) &&
            string.Equals(c.GetValueOrDefault("name") as string, AuthCookieName, StringComparison.OrdinalIgnoreCase) &&
            (c.GetValueOrDefault("value") as string ?? "").Length > 5);

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
        Action<string>? log = null) =>
        CookieFileHelper.TryWriteCookieFile(cookieFile, bigSellerCookies, log);

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
            await CdpClient.SendAsync(page, 60, "Page.navigate", new { url }).ConfigureAwait(false);

            var stableOkPolls = 0;
            for (var i = 0; i < 40; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);

                string href;
                string ready;
                try
                {
                    var result = await CdpClient.SendAsync(page, 61 + i, "Runtime.evaluate", new
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
                    continue;
                }

                if (IsLoginUrl(href))
                    return false;

                if (!string.Equals(ready, "complete", StringComparison.OrdinalIgnoreCase))
                {
                    stableOkPolls = 0;
                    continue;
                }

                if (!href.Contains("/web/", StringComparison.OrdinalIgnoreCase))
                    return false;

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

        var client = new CdpClient(debugPort);
        if (!await client.WaitForReadyAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            log?.Invoke($"CDP port {debugPort} chua san sang de import cookie.");
            return 0;
        }

        var cookiesEl = await CookieFileHelper.ParseCookiesRootFromFileAsync(cookieFile, cancellationToken)
            .ConfigureAwait(false);
        CookieFileHelper.ValidateCookiesArray(cookiesEl);

        log?.Invoke($"Dang nap cookie BigSeller tu account: {cookieFile}");
        var count = await CookieCdpWriter.SetCookiesFromJsonAsync(
            client,
            cookiesEl,
            new CookieImportFilter(IncludeShopee: false, IncludeBigSeller: true),
            log,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (count > 0)
        {
            if (!string.IsNullOrWhiteSpace(navigateUrl))
            {
                await NavigateBigSellerTabsAsync(debugPort, navigateUrl).ConfigureAwait(false);
                log?.Invoke($"Da dieu huong BigSeller toi: {navigateUrl}");
            }
            else if (reloadBigSellerTabs)
            {
                await client.ReloadPageTargetsAsync(IsBigSellerUrl).ConfigureAwait(false);
            }

            await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
        }

        log?.Invoke($"Da import {count} cookie BigSeller tu account.");
        return count;
    }

    private static async Task NavigateBigSellerTabsAsync(int debugPort, string targetUrl)
    {
        try
        {
            var client = new CdpClient(debugPort);
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
                await CdpClient.SendAsync(page, 92, "Page.navigate", new { url = targetUrl }).ConfigureAwait(false);
                navigated = true;
            }

            if (!navigated)
                await client.EnsurePageTargetAsync(IsBigSellerUrl, targetUrl).ConfigureAwait(false);
        }
        catch
        {
            // navigation is best-effort
        }
    }

    private static bool IsBigSellerUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Host.Contains("bigseller", StringComparison.OrdinalIgnoreCase);
}
