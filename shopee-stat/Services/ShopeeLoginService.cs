namespace ShopeeStatApp.Services;

/// <summary>
/// Automates Shopee login via CDP — mirrors the v31 EnsureShopeeLoggedInAsync flow.
/// </summary>
public sealed class ShopeeLoginService(AppSettingsService appSettings)
{
    private const string LoginUrl = "https://shopee.vn/buyer/login?next=https%3A%2F%2Fshopee.vn";

    /// <summary>
    /// Full login flow. Returns true if the account is (or becomes) logged in.
    /// On success clears OpenWithShopeeAccount. On proxy/CDP failure returns false.
    /// </summary>
    public async Task<bool> EnsureLoggedInAsync(
        InstanceConfig config, int cdpPort, Action<string> log, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.ShopeeAccountLogin))
        {
            log("Không có thông tin tài khoản — bỏ qua bước đăng nhập.");
            return true;
        }

        log("Chờ browser khởi động…");
        CdpSession? cdp = null;
        try
        {
            cdp = await CdpSession.ConnectToPageAsync(cdpPort, ct);
        }
        catch (Exception ex)
        {
            log($"CDP không kết nối được: {ex.Message}");
            return false;
        }

        await using var _ = cdp;

        // Enable Network domain for cookie operations
        try { await cdp.SendAsync("Network.enable", null, ct); } catch { }

        // Check if already logged in
        if (await IsLoggedInAsync(cdp, ct))
        {
            log("Đã đăng nhập sẵn.");
            ClearLoginFlag(config);
            return true;
        }

        // Parse login string
        if (!TryParseLoginLine(config.ShopeeAccountLogin,
            out var username, out var password, out var cookieDomain, out var spcF))
        {
            log("Chuỗi tài khoản sai định dạng (cần: user|pass|.shopee.vn=SPC_F=value).");
            return false;
        }

        log($"Thiết lập cookie SPC_F cho {username}…");
        await SetSpcFCookieAsync(cdp, cookieDomain, spcF, ct);

        log("Mở trang đăng nhập…");
        await NavigateAsync(cdp, LoginUrl, ct);
        await Task.Delay(2000, ct);

        log("Điền form đăng nhập…");
        for (var attempt = 0; attempt < 8; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var fillResult = await FillLoginFormAsync(cdp, username, password, ct);
            if (fillResult == "OK") break;
            await Task.Delay(900, ct);
        }

        log("Chờ đăng nhập (tối đa 90 giây)…");
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(3000, ct);

            if (await IsLoggedInAsync(cdp, ct))
            {
                log($"Đăng nhập thành công: {username}");
                ClearLoginFlag(config);
                appSettings.SaveSettings();
                return true;
            }
        }

        log("Đăng nhập thất bại (timeout 90s). Vui lòng thử lại.");
        return false;
    }

    // ── CDP helpers ───────────────────────────────────────────────────────────

    private static async Task<bool> IsLoggedInAsync(CdpSession cdp, CancellationToken ct)
    {
        try
        {
            var result = await cdp.SendAsync("Network.getAllCookies", null, ct);
            if (!result.TryGetProperty("cookies", out var cookies)) return false;

            foreach (var cookie in cookies.EnumerateArray())
            {
                var domain = cookie.TryGetProperty("domain", out var d) ? d.GetString() ?? "" : "";
                var name = cookie.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var value = cookie.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "";

                if (!domain.Contains("shopee", StringComparison.OrdinalIgnoreCase)) continue;
                if (name is not ("SPC_ST" or "SPC_EC")) continue;
                if (value.Length > 5 && value != "-") return true;
            }
        }
        catch { }
        return false;
    }

    private static async Task SetSpcFCookieAsync(
        CdpSession cdp, string domain, string spcF, CancellationToken ct)
    {
        var expires = (long)DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
        await cdp.SendAsync("Network.setCookie", new
        {
            name = "SPC_F",
            value = spcF,
            domain,
            path = "/",
            secure = true,
            httpOnly = false,
            sameSite = "Lax",
            expires,
        }, ct);
    }

    private static async Task NavigateAsync(CdpSession cdp, string url, CancellationToken ct)
    {
        try { await cdp.SendAsync("Page.navigate", new { url }, ct); }
        catch { }
    }

    private static async Task<string> FillLoginFormAsync(
        CdpSession cdp, string username, string password, CancellationToken ct)
    {
        // Language-safe value setter that bypasses React's synthetic event validation
        var js = $$"""
            (async () => {
                const setVal = (el, v) => {
                    const setter = Object.getOwnPropertyDescriptor(
                        window.HTMLInputElement.prototype, 'value').set;
                    setter.call(el, v);
                    el.dispatchEvent(new Event('input', { bubbles: true }));
                    el.dispatchEvent(new Event('change', { bubbles: true }));
                };
                const loginKey = document.querySelector('input[name="loginKey"]')
                               || document.querySelector('input[type="text"]');
                const pw = document.querySelector('input[name="password"]')
                         || document.querySelector('input[type="password"]');
                if (!loginKey || !pw) return 'NO_FORM';
                setVal(loginKey, {{JsonSerializer.Serialize(username)}});
                await new Promise(r => setTimeout(r, 300));
                setVal(pw, {{JsonSerializer.Serialize(password)}});
                await new Promise(r => setTimeout(r, 500));
                const btn = Array.from(document.querySelectorAll('button'))
                    .find(b => /log\s*in|đăng\s*nhập/i.test(b.textContent?.trim()))
                    || document.querySelector('button[type="submit"]');
                if (btn) { btn.click(); return 'OK'; }
                return 'NO_BTN';
            })()
            """;

        try
        {
            var result = await cdp.SendAsync("Runtime.evaluate", new
            {
                expression = js,
                awaitPromise = true,
                returnByValue = true,
            }, ct);

            if (result.TryGetProperty("result", out var r) &&
                r.TryGetProperty("value", out var v))
                return v.GetString() ?? "UNKNOWN";
        }
        catch { }
        return "ERROR";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool TryParseLoginLine(string line,
        out string username, out string password, out string cookieDomain, out string spcF)
    {
        username = password = cookieDomain = spcF = "";
        if (string.IsNullOrWhiteSpace(line)) return false;

        var parts = line.Split('|');
        if (parts.Length < 3) return false;

        username = parts[0].Trim();
        password = parts[1].Trim();
        var cookiePart = parts[2].Trim(); // e.g. ".shopee.vn=SPC_F=abc123..."

        var eqIdx = cookiePart.IndexOf('=');
        if (eqIdx < 0) return false;

        cookieDomain = cookiePart[..eqIdx];         // ".shopee.vn"
        var rest = cookiePart[(eqIdx + 1)..];        // "SPC_F=abc123..."

        var spcIdx = rest.IndexOf('=');
        if (spcIdx < 0) return false;

        spcF = rest[(spcIdx + 1)..];                 // "abc123..."

        return !string.IsNullOrWhiteSpace(username)
            && !string.IsNullOrWhiteSpace(spcF)
            && !string.IsNullOrWhiteSpace(cookieDomain);
    }

    private static void ClearLoginFlag(InstanceConfig config)
    {
        config.OpenWithShopeeAccount = false;
    }
}
