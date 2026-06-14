using System.Collections.Concurrent;
using System.Net.Sockets;

namespace ShopeeStatApp.Services;

/// <summary>
/// Minimal Chrome DevTools Protocol (CDP) client over WebSocket.
/// Connects to a single page/browser target and sends JSON-RPC commands.
/// </summary>
public sealed class CdpSession : IAsyncDisposable
{
    private ClientWebSocket? _ws;
    private int _nextId;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<byte> _recvBuf = [];

    public bool IsOpen => _ws?.State == WebSocketState.Open;

    /// <summary>Polls until Brave's CDP HTTP endpoint responds, then connects to the first page.</summary>
    public static async Task<CdpSession> ConnectToPageAsync(int cdpPort, CancellationToken ct = default)
    {
        // Wait for browser to start
        var wsUrl = await WaitForPageWsUrlAsync(cdpPort, ct);
        var session = new CdpSession();
        await session.ConnectAsync(wsUrl, ct);
        return session;
    }

    /// <summary>
    /// Polls until a "page" target whose URL satisfies <paramref name="urlPredicate"/> appears,
    /// then connects to it. Use this to attach to the Shopee search tab specifically rather than
    /// whatever page target happens to be first.
    /// </summary>
    public static async Task<CdpSession> ConnectToPageMatchingAsync(
        int cdpPort, Func<string, bool> urlPredicate, CancellationToken ct = default)
    {
        var wsUrl = await WaitForPageWsUrlAsync(cdpPort, ct, urlPredicate: urlPredicate);
        var session = new CdpSession();
        await session.ConnectAsync(wsUrl, ct);
        return session;
    }

    public static async Task<string> WaitForPageWsUrlAsync(
        int cdpPort, CancellationToken ct = default, int timeoutMs = 15_000,
        Func<string, bool>? urlPredicate = null)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var json = await http.GetStringAsync($"http://localhost:{cdpPort}/json", ct);
                using var doc = JsonDocument.Parse(json);
                foreach (var target in doc.RootElement.EnumerateArray())
                {
                    if (target.TryGetProperty("type", out var t) && t.GetString() == "page" &&
                        target.TryGetProperty("webSocketDebuggerUrl", out var u))
                    {
                        var url = u.GetString();
                        if (string.IsNullOrWhiteSpace(url)) continue;
                        if (urlPredicate is not null)
                        {
                            var pageUrl = target.TryGetProperty("url", out var pu) ? pu.GetString() ?? "" : "";
                            if (!urlPredicate(pageUrl)) continue;
                        }
                        return url!;
                    }
                }
            }
            catch { }

            await Task.Delay(500, ct);
        }

        throw new TimeoutException($"CDP on port {cdpPort} did not respond within {timeoutMs / 1000}s.");
    }

    private async Task ConnectAsync(string wsUrl, CancellationToken ct)
    {
        _ws = new ClientWebSocket();
        await _ws.ConnectAsync(new Uri(wsUrl), ct);
        _ = ReadLoopAsync(_cts.Token);
    }

    /// <summary>
    /// Sends a command without waiting for the browser's response. Dùng cho các sự kiện
    /// trung gian (mouseMoved interpolation) — chờ phản hồi từng bước khiến gesture chậm
    /// hơn 20s khi renderer bận và extension sẽ timeout.
    /// </summary>
    public async Task SendNoReplyAsync(string method, object? @params = null, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var msg = JsonSerializer.Serialize(new { id, method, @params }, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        });
        var bytes = Encoding.UTF8.GetBytes(msg);
        await _ws!.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    public async Task<JsonElement> SendAsync(
        string method, object? @params = null, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var msg = @params is null
            ? $"{{\"id\":{id},\"method\":\"{method}\"}}"
            : JsonSerializer.Serialize(new { id, method, @params }, new JsonSerializerOptions
              {
                  PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                  DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
              });

        var bytes = Encoding.UTF8.GetBytes(msg);
        await _ws!.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);

        using var timeout = new CancellationTokenSource(20_000);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        linked.Token.Register(() => tcs.TrySetCanceled());

        return await tcs.Task;
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buf = new byte[128 * 1024];
        while (_ws?.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
            }
            catch (OperationCanceledException) { break; }
            catch { break; } // socket lỗi/đóng → thoát hẳn (IsOpen sẽ false → phiên sau kết nối lại)

            if (result.MessageType == WebSocketMessageType.Close) break;
            _recvBuf.AddRange(new ArraySegment<byte>(buf, 0, result.Count));
            if (!result.EndOfMessage) continue;

            var json = Encoding.UTF8.GetString([.. _recvBuf]);
            _recvBuf.Clear();

            // Parse/dispatch lỗi 1 message thì BỎ QUA message đó và đọc tiếp — KHÔNG thoát vòng,
            // nếu không 1 message hỏng sẽ làm chết toàn bộ phản hồi → mọi gesture sau timeout 10s.
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out var id)
                    && _pending.TryRemove(id, out var tcs))
                {
                    // Clone so it survives after doc.Dispose()
                    tcs.TrySetResult(root.TryGetProperty("result", out var res) ? res.Clone() : root.Clone());
                }
            }
            catch { /* message lỗi → bỏ qua, vẫn tiếp tục đọc */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        foreach (var tcs in _pending.Values)
            tcs.TrySetCanceled();
        _pending.Clear();

        if (_ws is { State: WebSocketState.Open })
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); }
            catch { }
        }
        _ws?.Dispose();
        _cts.Dispose();
    }
}
