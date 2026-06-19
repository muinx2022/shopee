namespace CheckShopeeAccount;

public enum CheckOutcome
{
    /// <summary>Đăng nhập thành công (đã có cookie phiên SPC_ST/SPC_EC).</summary>
    Success,
    /// <summary>Sai tài khoản/mật khẩu (Shopee báo "incorrect").</summary>
    WrongPassword,
    /// <summary>Cần xử lý tay: captcha/OTP/verify, hoặc không xác định được trong thời gian chờ.</summary>
    NeedsManual,
    /// <summary>Lỗi kỹ thuật (proxy/CDP/không thấy form).</summary>
    Error,
}

public readonly record struct CheckResult(CheckOutcome Outcome, string Message);

/// <summary>
/// Mở Edge với kiotproxy, điền form đăng nhập Shopee theo kiểu human (di chuột có quán
/// tính, gõ từng ký tự, độ trễ ngẫu nhiên) rồi phân loại kết quả: thành công / sai mật
/// khẩu / cần xử lý tay.
/// </summary>
public sealed class ShopeeAccountChecker
{
    private const string LoginUrl = "https://shopee.vn/buyer/login?next=https%3A%2F%2Fshopee.vn";

    private readonly Random _rng = new();
    private double _mouseX;
    private double _mouseY;

    public event Action<string>? Log;

    public ShopeeAccountChecker()
    {
        _mouseX = 220 + _rng.Next(0, 380);
        _mouseY = 160 + _rng.Next(0, 260);
    }

    /// <summary>
    /// <paramref name="proxy"/> là proxy đã resolve sẵn (đã xoay vòng ở ngoài); null = không proxy.
    /// <paramref name="profileDir"/>: thư mục user-data-dir (Edge) cho tài khoản này — KHÔNG bị xoá
    /// ở đây; caller quyết định giữ (login OK, để copy sang shopee-stat/v31) hay xoá.
    /// <paramref name="holdMs"/>: giữ trình duyệt mở thêm bấy nhiêu ms sau khi có kết quả (bất kể
    /// thành công/thất bại) rồi mới đóng — giả lập người dùng, tránh login dồn dập.
    /// </summary>
    public async Task<CheckResult> CheckAsync(
        string accountLine, string? proxy, string profileDir, int holdMs, CancellationToken ct)
    {
        if (!TryParse(accountLine, out var username, out var password, out var cookieDomain, out var spcF))
            return new CheckResult(CheckOutcome.Error, "Sai định dạng (cần tối thiểu user|pass).");

        // Mở Edge với profile của tài khoản (caller chuẩn bị & quyết định giữ/xoá thư mục)
        var launcher = new BrowserLauncher();
        CdpSession? cdp = null;
        try
        {
            launcher.Launch(profileDir, proxy, LoginUrl);

            cdp = await CdpSession.ConnectToPageAsync(launcher.CdpPort, ct);
            await TrySend(cdp, "Network.enable", null, ct);
            await TrySend(cdp, "Page.enable", null, ct);

            if (!string.IsNullOrWhiteSpace(spcF))
                await SetCookieAsync(cdp, "SPC_F", spcF, cookieDomain, ct);

            // Điều hướng lại cho chắc (cookie SPC_F đã set trước khi tải form)
            await TrySend(cdp, "Page.navigate", new { url = LoginUrl }, ct);

            // 3) Chờ form xuất hiện
            if (!await WaitForFormAsync(cdp, ct))
                return new CheckResult(CheckOutcome.NeedsManual, "Không thấy form đăng nhập (có thể đã chặn/đổi giao diện).");

            // 4) Điền form kiểu human
            await HumanFillAsync(cdp, username, password, ct);

            // 5) Chờ & phân loại kết quả
            var result = await WaitOutcomeAsync(cdp, username, ct);

            // Giữ trình duyệt mở thêm holdMs (bất kể thành công/thất bại) rồi mới đóng profile.
            if (holdMs > 0)
            {
                Log?.Invoke($"  giữ trình duyệt {holdMs / 1000.0:0.#}s rồi đóng…");
                try { await Task.Delay(holdMs, ct); } catch { }
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            return new CheckResult(CheckOutcome.Error, "Đã dừng.");
        }
        catch (Exception ex)
        {
            return new CheckResult(CheckOutcome.Error, ex.Message);
        }
        finally
        {
            if (cdp is not null) await cdp.DisposeAsync();
            launcher.Kill();                       // đóng sạch Edge của profile này (không xoá thư mục)
            await Task.Delay(400, CancellationToken.None);
        }
    }

    // ── Bước điền form (human) ─────────────────────────────────────────────────

    private async Task HumanFillAsync(CdpSession cdp, string username, string password, CancellationToken ct)
    {
        Log?.Invoke("  điền tài khoản…");
        await ClickSelectorAsync(cdp, "input[name=\"loginKey\"], input[type=\"text\"]", ct);
        await TypeHumanAsync(cdp, username, ct);
        await DelayAsync(280, 620, ct);

        Log?.Invoke("  điền mật khẩu…");
        await ClickSelectorAsync(cdp, "input[name=\"password\"], input[type=\"password\"]", ct);
        await TypeHumanAsync(cdp, password, ct);
        await DelayAsync(350, 760, ct);

        Log?.Invoke("  bấm đăng nhập…");
        var btn = await GetButtonRectAsync(cdp, ct);
        if (btn is { } b)
        {
            await ClickAtAsync(cdp, b.X, b.Y, ct);
        }
        else
        {
            // Fallback: Enter trong ô mật khẩu
            await PressEnterAsync(cdp, ct);
        }
    }

    private async Task<bool> WaitForFormAsync(CdpSession cdp, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var has = await EvalBoolAsync(cdp,
                "!!(document.querySelector('input[name=\"loginKey\"]') || document.querySelector('input[type=\"password\"]'))",
                ct);
            if (has) { await DelayAsync(500, 1100, ct); return true; }
            await Task.Delay(700, ct);
        }
        return false;
    }

    private async Task<CheckResult> WaitOutcomeAsync(CdpSession cdp, string username, CancellationToken ct)
    {
        Log?.Invoke("  chờ kết quả…");
        var deadline = DateTime.UtcNow.AddSeconds(70);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(2000, ct);

            // a) Đăng nhập thành công?
            if (await IsLoggedInAsync(cdp, ct))
                return new CheckResult(CheckOutcome.Success, $"OK: {username}");

            // b) Sai mật khẩu / verify / captcha?
            var (url, alert) = await ReadPageStateAsync(cdp, ct);
            var a = alert.ToLowerInvariant();
            if (a.Contains("incorrect") || a.Contains("không chính xác") || a.Contains("khong chinh xac")
                || a.Contains("sai") || a.Contains("không đúng") || a.Contains("khong dung"))
                return new CheckResult(CheckOutcome.WrongPassword, "Sai tài khoản/mật khẩu.");

            var u = url.ToLowerInvariant();
            if (u.Contains("/verify") || u.Contains("captcha")
                || a.Contains("otp") || a.Contains("mã xác") || a.Contains("xác minh") || a.Contains("xac minh"))
                return new CheckResult(CheckOutcome.NeedsManual, "Cần OTP/verify/captcha — xử lý tay.");
        }

        return new CheckResult(CheckOutcome.NeedsManual, "Quá thời gian chờ, không rõ kết quả — xử lý tay.");
    }

    // ── CDP helpers ────────────────────────────────────────────────────────────

    private async Task<bool> IsLoggedInAsync(CdpSession cdp, CancellationToken ct)
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

    private async Task<(string Url, string Alert)> ReadPageStateAsync(CdpSession cdp, CancellationToken ct)
    {
        var js = """
            (() => {
                const alerts = Array.from(document.querySelectorAll('div[role="alert"]'));
                const alertText = alerts.map(a => a.innerText || '').join(' | ');
                return JSON.stringify({ url: location.href, alert: alertText });
            })()
            """;
        var raw = await EvalStringAsync(cdp, js, ct);
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            return (
                root.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
                root.TryGetProperty("alert", out var a) ? a.GetString() ?? "" : "");
        }
        catch { return ("", ""); }
    }

    private static async Task SetCookieAsync(CdpSession cdp, string name, string value, string domain, CancellationToken ct)
    {
        var expires = (long)DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
        try
        {
            await cdp.SendAsync("Network.setCookie", new
            {
                name, value,
                domain = string.IsNullOrWhiteSpace(domain) ? ".shopee.vn" : domain,
                path = "/", secure = true, httpOnly = false, sameSite = "Lax", expires,
            }, ct);
        }
        catch { }
    }

    private async Task ClickSelectorAsync(CdpSession cdp, string selector, CancellationToken ct)
    {
        var rect = await GetRectAsync(cdp, selector, ct);
        if (rect is { } r) await ClickAtAsync(cdp, r.X, r.Y, ct);
    }

    private async Task<(double X, double Y)?> GetRectAsync(CdpSession cdp, string selector, CancellationToken ct)
    {
        var js = $$"""
            (() => {
                const el = document.querySelector({{JsonSerializer.Serialize(selector)}});
                if (!el) return 'null';
                const r = el.getBoundingClientRect();
                if (r.width <= 0 || r.height <= 0) return 'null';
                return JSON.stringify({ x: r.left + r.width / 2, y: r.top + r.height / 2 });
            })()
            """;
        return ParseRect(await EvalStringAsync(cdp, js, ct));
    }

    private async Task<(double X, double Y)?> GetButtonRectAsync(CdpSession cdp, CancellationToken ct)
    {
        var js = """
            (() => {
                const btns = Array.from(document.querySelectorAll('button'));
                const b = btns.find(b => /log\s*in|đăng\s*nhập|dang\s*nhap/i.test((b.textContent || '').trim()))
                        || document.querySelector('button[type="submit"]');
                if (!b) return 'null';
                const r = b.getBoundingClientRect();
                if (r.width <= 0 || r.height <= 0) return 'null';
                return JSON.stringify({ x: r.left + r.width / 2, y: r.top + r.height / 2 });
            })()
            """;
        return ParseRect(await EvalStringAsync(cdp, js, ct));
    }

    private static (double X, double Y)? ParseRect(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "null") return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.TryGetProperty("x", out var x) && root.TryGetProperty("y", out var y))
                return (x.GetDouble(), y.GetDouble());
        }
        catch { }
        return null;
    }

    private async Task<string> EvalStringAsync(CdpSession cdp, string expression, CancellationToken ct)
    {
        try
        {
            var result = await cdp.SendAsync("Runtime.evaluate", new
            {
                expression, awaitPromise = true, returnByValue = true,
            }, ct);
            if (result.TryGetProperty("result", out var r) && r.TryGetProperty("value", out var v))
                return v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString();
        }
        catch { }
        return "";
    }

    private async Task<bool> EvalBoolAsync(CdpSession cdp, string expression, CancellationToken ct)
    {
        try
        {
            var result = await cdp.SendAsync("Runtime.evaluate", new
            {
                expression, awaitPromise = true, returnByValue = true,
            }, ct);
            if (result.TryGetProperty("result", out var r) && r.TryGetProperty("value", out var v))
                return v.ValueKind == JsonValueKind.True;
        }
        catch { }
        return false;
    }

    // ── Trusted input (human-like) ──────────────────────────────────────────────

    private async Task ClickAtAsync(CdpSession cdp, double tx, double ty, CancellationToken ct)
    {
        await MoveMouseToAsync(cdp, tx, ty, ct);
        await DelayAsync(160, 480, ct);
        await MouseAsync(cdp, "mousePressed", tx, ty, "left", 1, 1, ct);
        await DelayAsync(50, 140, ct);
        await MouseAsync(cdp, "mouseReleased", tx, ty, "left", 0, 1, ct);
    }

    private async Task MoveMouseToAsync(CdpSession cdp, double tx, double ty, CancellationToken ct)
    {
        var sx = _mouseX;
        var sy = _mouseY;
        var steps = _rng.Next(10, 20);
        for (var i = 1; i < steps; i++)
        {
            ct.ThrowIfCancellationRequested();
            var t = (double)i / steps;
            var ease = t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;
            var x = sx + (tx - sx) * ease + Math.Sin(t * Math.PI * 3) * Rand(-4, 4);
            var y = sy + (ty - sy) * ease + Math.Cos(t * Math.PI * 2) * Rand(-3, 3);
            await cdp.SendNoReplyAsync("Input.dispatchMouseEvent", new
            {
                type = "mouseMoved", x, y, button = "none", buttons = 0,
            }, ct);
            await Task.Delay(_rng.Next(8, 24), ct);
        }
        await MouseAsync(cdp, "mouseMoved", tx, ty, "none", 0, 0, ct);
        _mouseX = tx;
        _mouseY = ty;
    }

    private static Task MouseAsync(CdpSession cdp, string type, double x, double y,
        string button, int buttons, int clickCount, CancellationToken ct) =>
        cdp.SendAsync("Input.dispatchMouseEvent", new { type, x, y, button, buttons, clickCount }, ct);

    private async Task TypeHumanAsync(CdpSession cdp, string text, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(text)) return;

        var isAscii = text.All(ch => ch >= 0x20 && ch <= 0x7E);
        if (!isAscii)
        {
            await DelayAsync(120, 260, ct);
            await cdp.SendAsync("Input.insertText", new { text }, ct);
            return;
        }

        foreach (var ch in text)
        {
            ct.ThrowIfCancellationRequested();
            var (code, vk) = KeyInfo(ch);
            var s = ch.ToString();
            await cdp.SendAsync("Input.dispatchKeyEvent",
                new { type = "keyDown", text = s, key = s, code, windowsVirtualKeyCode = vk }, ct);
            await cdp.SendAsync("Input.dispatchKeyEvent",
                new { type = "keyUp", key = s, code, windowsVirtualKeyCode = vk }, ct);
            await Task.Delay(_rng.Next(55, 150), ct);
        }
    }

    private async Task PressEnterAsync(CdpSession cdp, CancellationToken ct)
    {
        await cdp.SendAsync("Input.dispatchKeyEvent",
            new { type = "keyDown", key = "Enter", code = "Enter", windowsVirtualKeyCode = 13, text = "\r" }, ct);
        await DelayAsync(40, 110, ct);
        await cdp.SendAsync("Input.dispatchKeyEvent",
            new { type = "keyUp", key = "Enter", code = "Enter", windowsVirtualKeyCode = 13 }, ct);
    }

    private static (string code, int vk) KeyInfo(char ch)
    {
        if (ch >= '0' && ch <= '9') return ("Digit" + ch, ch);
        if (ch >= 'a' && ch <= 'z') return ("Key" + char.ToUpperInvariant(ch), char.ToUpperInvariant(ch));
        if (ch >= 'A' && ch <= 'Z') return ("Key" + ch, ch);
        if (ch == ' ') return ("Space", 32);
        return ("", 0);
    }

    private static async Task TrySend(CdpSession cdp, string method, object? @params, CancellationToken ct)
    {
        try { await cdp.SendAsync(method, @params, ct); } catch { }
    }

    private double Rand(double min, double max) => min + _rng.NextDouble() * (max - min);

    private Task DelayAsync(int minMs, int maxMs, CancellationToken ct) => Task.Delay(_rng.Next(minMs, maxMs + 1), ct);

    // ── Parse chuỗi tài khoản ────────────────────────────────────────────────────

    /// <summary>
    /// Hỗ trợ "user|pass" hoặc "user|pass|.shopee.vn=SPC_F=value". Phần cookie là tuỳ chọn.
    /// </summary>
    public static bool TryParse(string line, out string username, out string password,
        out string cookieDomain, out string spcF)
    {
        username = password = cookieDomain = spcF = "";
        if (string.IsNullOrWhiteSpace(line)) return false;

        var parts = line.Split('|');
        if (parts.Length < 2) return false;

        username = parts[0].Trim();
        password = parts[1].Trim();
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)) return false;

        if (parts.Length >= 3)
        {
            var cookiePart = parts[2].Trim(); // ".shopee.vn=SPC_F=abc..."
            var eqIdx = cookiePart.IndexOf('=');
            if (eqIdx > 0)
            {
                cookieDomain = cookiePart[..eqIdx];
                var rest = cookiePart[(eqIdx + 1)..];      // "SPC_F=abc..."
                var spcIdx = rest.IndexOf('=');
                spcF = spcIdx >= 0 ? rest[(spcIdx + 1)..] : rest;
            }
        }

        return true;
    }
}
