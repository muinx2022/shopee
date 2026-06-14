namespace ShopeeStatApp.Services;

/// <summary>
/// Executes trusted browser input via the Chrome DevTools Protocol (CDP) Input domain,
/// driven by gesture-level requests the extension sends over the app WebSocket.
///
/// The extension stays in charge of orchestration (it resolves DOM coordinates with
/// getBoundingClientRect), but instead of dispatching synthetic JS events (isTrusted=false)
/// it sends a single message per gesture — {kind:"cdpInput", id, op, ...} — and awaits an
/// ack {kind:"cdpInputAck", id, ok}. Human-like motion (easing, jitter, per-step delays) is
/// interpolated here so each gesture is one round-trip, not one-per-micro-step.
/// </summary>
public sealed class CdpInputController : IAsyncDisposable
{
    private readonly WebSocketServer _ws;
    private readonly int _cdpPort;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Random _rng = new();

    private CdpSession? _cdp;
    private double _mouseX;
    private double _mouseY;
    private bool _disposed;
    private bool _dprLogged;

    public event Action<string>? Log;

    public CdpInputController(WebSocketServer ws, int cdpPort)
    {
        _ws = ws;
        _cdpPort = cdpPort;
        _mouseX = 200 + _rng.Next(0, 400);
        _mouseY = 150 + _rng.Next(0, 300);
    }

    /// <summary>Attaches a CDP session to the Shopee search tab and starts listening for gestures.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        await EnsureSessionAsync(ct);
        _ws.MessageReceived += OnMessage;
    }

    private async Task<bool> EnsureSessionAsync(CancellationToken ct = default)
    {
        if (_cdp is { IsOpen: true }) return true;
        try
        {
            if (_cdp is not null) await _cdp.DisposeAsync();
            _cdp = await CdpSession.ConnectToPageMatchingAsync(
                _cdpPort,
                url => url.Contains("shopee.vn", StringComparison.OrdinalIgnoreCase)
                       && !url.Contains("shopee.vn/api/", StringComparison.OrdinalIgnoreCase),
                ct);
            return true;
        }
        catch (Exception ex)
        {
            Log?.Invoke("CDP input connect failed: " + ex.Message);
            return false;
        }
    }

    // Read every needed field synchronously: the JsonDocument is disposed once this
    // handler returns, so we must not touch the JsonElement across the await below.
    private void OnMessage(JsonDocument doc)
    {
        var root = doc.RootElement;
        if (!root.TryGetProperty("kind", out var kindProp) || kindProp.GetString() != "cdpInput")
            return;

        var id = root.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var n) ? n : 0;
        var op = root.TryGetProperty("op", out var opEl) ? opEl.GetString() ?? "" : "";
        var x = GetDouble(root, "x");
        var y = GetDouble(root, "y");
        var deltaY = GetDouble(root, "deltaY");
        var deltaX = GetDouble(root, "deltaX");
        var text = root.TryGetProperty("text", out var tEl) ? tEl.GetString() ?? "" : "";
        var key = root.TryGetProperty("key", out var kEl) ? kEl.GetString() ?? "" : "";
        var clearFirst = root.TryGetProperty("clearFirst", out var cEl) && cEl.ValueKind == JsonValueKind.True;
        var clickCount = root.TryGetProperty("clickCount", out var ccEl) && ccEl.TryGetInt32(out var cc) ? cc : 1;
        var dpr = root.TryGetProperty("dpr", out var dEl) && dEl.TryGetDouble(out var d) ? d : 1.0;

        // dpr != 1 do Windows scale (màn 4K) là bình thường — tọa độ CDP tính bằng CSS px
        // nên không bị lệch. Chỉ log một lần để tham khảo, không phải cảnh báo lỗi.
        if (Math.Abs(dpr - 1.0) > 0.01 && !_dprLogged)
        {
            _dprLogged = true;
            Log?.Invoke($"devicePixelRatio={dpr} (Windows scale/zoom). Tọa độ CDP dùng CSS px nên không ảnh hưởng; chỉ lệch nếu zoom trình duyệt khác 100%.");
        }

        _ = ExecuteAsync(id, op, x, y, deltaX, deltaY, text, key, clearFirst, clickCount);
    }

    private async Task ExecuteAsync(
        int id, string op, double x, double y, double deltaX, double deltaY,
        string text, string key, bool clearFirst, int clickCount)
    {
        var ok = false;
        string? error = null;

        await _gate.WaitAsync();
        try
        {
            if (!await EnsureSessionAsync())
            {
                error = "CDP session not available";
            }
            else
            {
                switch (op)
                {
                    case "moveTo": await MoveMouseToAsync(x, y); ok = true; break;
                    case "click":  await ClickAsync(x, y, clickCount); ok = true; break;
                    case "wheel":  await WheelAsync(x, y, deltaX, deltaY); ok = true; break;
                    case "type":   await TypeAsync(text, clearFirst); ok = true; break;
                    case "pressKey": await PressKeyAsync(string.IsNullOrEmpty(key) ? "Enter" : key); ok = true; break;
                    default: error = "unknown op: " + op; break;
                }
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log?.Invoke($"CDP gesture '{op}' failed: {ex.Message}");
            // Phiên CDP có thể đã hỏng (vd timeout do socket nửa-chết). Bỏ phiên để gesture KẾ
            // kết nối lại thay vì dùng lại phiên hỏng → tránh kẹt fallback synthetic cả từ khóa.
            try { if (_cdp is not null) { await _cdp.DisposeAsync(); _cdp = null; } } catch { }
        }
        finally
        {
            _gate.Release();
        }

        try { await _ws.SendAsync(new { kind = "cdpInputAck", id, ok, error }); }
        catch { }
    }

    // â”€â”€ Trusted input primitives â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private async Task MoveMouseToAsync(double tx, double ty)
    {
        var sx = _mouseX;
        var sy = _mouseY;
        // Intermediate moves are fire-and-forget: awaiting each CDP reply while the
        // renderer is busy pushes the whole gesture past the extension's ack timeout.
        var steps = _rng.Next(10, 20);
        for (var i = 1; i < steps; i++)
        {
            var t = (double)i / steps;
            var ease = t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;
            var x = sx + (tx - sx) * ease + Math.Sin(t * Math.PI * 3) * Rand(-4, 4);
            var y = sy + (ty - sy) * ease + Math.Cos(t * Math.PI * 2) * Rand(-3, 3);
            await MouseNoReply("mouseMoved", x, y, button: "none", buttons: 0);
            await Delay(8, 24);
        }
        // Final move is awaited so the cursor is guaranteed at the target before click.
        await Mouse("mouseMoved", tx, ty, button: "none", buttons: 0);
        _mouseX = tx;
        _mouseY = ty;
    }

    private async Task ClickAsync(double tx, double ty, int clickCount)
    {
        await MoveMouseToAsync(tx, ty);
        await Delay(180, 520);
        await Mouse("mousePressed", tx, ty, button: "left", buttons: 1, clickCount: clickCount);
        await Delay(55, 150);
        await Mouse("mouseReleased", tx, ty, button: "left", buttons: 0, clickCount: clickCount);
    }

    // Wheel = fire-and-forget: KHÔNG chờ phản hồi CDP. Trang nặng làm phản hồi tới chậm > timeout
    // → trước đây "A task was canceled" → fallback synthetic dù trang vẫn cuộn. Extension tự đọc lại
    // scrollY sau mỗi wheel để biết đã cuộn chưa, nên không cần ack giá trị từ CDP.
    private Task WheelAsync(double x, double y, double deltaX, double deltaY) =>
        _cdp!.SendNoReplyAsync("Input.dispatchMouseEvent", new
        {
            type = "mouseWheel", x, y, button = "none", buttons = 0, clickCount = 0, deltaX, deltaY,
        });

    private async Task TypeAsync(string text, bool clearFirst)
    {
        if (clearFirst) await SelectAllAndDeleteAsync();
        if (string.IsNullOrEmpty(text)) return;

        // ASCII printable → type char-by-char with key events (most human-like).
        // Anything with accents/unicode → insertText for the whole string (reliable + trusted).
        var isAscii = text.All(ch => ch >= 0x20 && ch <= 0x7E);
        if (!isAscii)
        {
            await Delay(120, 260);
            await _cdp!.SendAsync("Input.insertText", new { text });
            return;
        }

        foreach (var ch in text)
        {
            var (code, vk) = KeyInfo(ch);
            var s = ch.ToString();
            await _cdp!.SendAsync("Input.dispatchKeyEvent",
                new { type = "keyDown", text = s, key = s, code, windowsVirtualKeyCode = vk });
            await _cdp.SendAsync("Input.dispatchKeyEvent",
                new { type = "keyUp", key = s, code, windowsVirtualKeyCode = vk });
            await Delay(45, 120);
        }
    }

    private async Task SelectAllAndDeleteAsync()
    {
        // Ctrl+A then Delete (modifiers bitmask: 2 = Ctrl).
        await _cdp!.SendAsync("Input.dispatchKeyEvent",
            new { type = "keyDown", key = "Control", code = "ControlLeft", windowsVirtualKeyCode = 17, modifiers = 2 });
        await _cdp.SendAsync("Input.dispatchKeyEvent",
            new { type = "keyDown", key = "a", code = "KeyA", windowsVirtualKeyCode = 65, modifiers = 2 });
        await _cdp.SendAsync("Input.dispatchKeyEvent",
            new { type = "keyUp", key = "a", code = "KeyA", windowsVirtualKeyCode = 65, modifiers = 2 });
        await _cdp.SendAsync("Input.dispatchKeyEvent",
            new { type = "keyUp", key = "Control", code = "ControlLeft", windowsVirtualKeyCode = 17 });
        await Delay(40, 110);
        await PressKeyAsync("Delete");
    }

    private async Task PressKeyAsync(string key)
    {
        var (code, vk) = SpecialKeyInfo(key);
        // Enter phải là "keyDown" kèm text "\r" để renderer sinh đủ keydown+keypress —
        // form Shopee không submit với rawKeyDown (thiếu keypress).
        if (key == "Enter")
            await _cdp!.SendAsync("Input.dispatchKeyEvent",
                new { type = "keyDown", key, code, windowsVirtualKeyCode = vk, text = "\r" });
        else
            await _cdp!.SendAsync("Input.dispatchKeyEvent",
                new { type = "rawKeyDown", key, code, windowsVirtualKeyCode = vk });
        await Delay(40, 110);
        await _cdp.SendAsync("Input.dispatchKeyEvent",
            new { type = "keyUp", key, code, windowsVirtualKeyCode = vk });
    }

    private Task Mouse(string type, double x, double y, string button, int buttons,
        int clickCount = 0, double deltaX = 0, double deltaY = 0) =>
        _cdp!.SendAsync("Input.dispatchMouseEvent", new
        {
            type, x, y, button, buttons, clickCount, deltaX, deltaY,
        });

    private Task MouseNoReply(string type, double x, double y, string button, int buttons) =>
        _cdp!.SendNoReplyAsync("Input.dispatchMouseEvent", new
        {
            type, x, y, button, buttons, clickCount = 0, deltaX = 0d, deltaY = 0d,
        });

    // â”€â”€ helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private double Rand(double min, double max) => min + _rng.NextDouble() * (max - min);

    private Task Delay(int minMs, int maxMs) => Task.Delay(_rng.Next(minMs, maxMs + 1));

    private static double GetDouble(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.TryGetDouble(out var d) ? d : 0;

    private static (string code, int vk) KeyInfo(char ch)
    {
        if (ch >= '0' && ch <= '9') return ("Digit" + ch, ch);
        if (ch >= 'a' && ch <= 'z') return ("Key" + char.ToUpperInvariant(ch), char.ToUpperInvariant(ch));
        if (ch >= 'A' && ch <= 'Z') return ("Key" + ch, ch);
        if (ch == ' ') return ("Space", 32);
        return ("", 0);
    }

    private static (string code, int vk) SpecialKeyInfo(string key) => key switch
    {
        "Enter" => ("Enter", 13),
        "Delete" => ("Delete", 46),
        "Backspace" => ("Backspace", 8),
        "Escape" => ("Escape", 27),
        "Tab" => ("Tab", 9),
        _ => ("", 0),
    };

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _ws.MessageReceived -= OnMessage;
        if (_cdp is not null) await _cdp.DisposeAsync();
        _gate.Dispose();
    }
}
