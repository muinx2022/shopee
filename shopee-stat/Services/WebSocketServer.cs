namespace ShopeeStatApp.Services;

public sealed class WebSocketServer : IDisposable
{
    private readonly int _port;
    private HttpListener? _listener;
    private WebSocket? _socket;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public event Action<JsonDocument>? MessageReceived;
    public event Action? Connected;
    public event Action? Disconnected;

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    public WebSocketServer(int port = 9111) => _port = port;

    public void Start()
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{_port}/");
        _listener.Start();
        _ = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var ctx = await _listener!.GetContextAsync().WaitAsync(ct);
                if (!ctx.Request.IsWebSocketRequest)
                {
                    ctx.Response.StatusCode = 400;
                    ctx.Response.Close();
                    continue;
                }
                var wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null);
                _ = HandleConnectionAsync(wsCtx.WebSocket, ct);
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    private async Task HandleConnectionAsync(WebSocket ws, CancellationToken ct)
    {
        // Only the newest connection is live: close the previous one so its receive
        // loop exits instead of lingering and racing this one.
        var old = Interlocked.Exchange(ref _socket, ws);
        if (old is not null && old.State == WebSocketState.Open)
        {
            try { await old.CloseAsync(WebSocketCloseStatus.NormalClosure, "replaced", CancellationToken.None); }
            catch { }
        }
        Connected?.Invoke();

        var buffer = new byte[256 * 1024];
        // Per-connection buffer: a shared field corrupts interleaved frames when an
        // old connection is still draining while a new one arrives.
        var msgBuffer = new List<byte>();

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            try { result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct); }
            catch { break; }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); }
                catch { }
                break;
            }

            msgBuffer.AddRange(new ArraySegment<byte>(buffer, 0, result.Count));

            if (result.EndOfMessage)
            {
                var json = Encoding.UTF8.GetString([.. msgBuffer]);
                msgBuffer.Clear();
                try
                {
                    var doc = JsonDocument.Parse(json);
                    MessageReceived?.Invoke(doc);
                }
                catch { }
            }
        }

        Interlocked.CompareExchange(ref _socket, null, ws);
        Disconnected?.Invoke();
    }

    public async Task SendAsync(object message)
    {
        var ws = _socket;
        if (ws?.State != WebSocketState.Open) return;

        var json = JsonSerializer.Serialize(message, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        });
        var bytes = Encoding.UTF8.GetBytes(json);

        await _sendLock.WaitAsync();
        try
        {
            await ws.SendAsync(new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text, true, CancellationToken.None);
        }
        finally { _sendLock.Release(); }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _socket?.Dispose();
        _listener?.Stop();
        _cts.Dispose();
        _sendLock.Dispose();
    }
}
