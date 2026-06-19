namespace ShopeeStatApp.Services;

public enum SearchRunOutcome
{
    Completed,
    CaptchaOrVerify,
    NetworkError,
    Error,
    Cancelled,
    /// <summary>
    /// Lane mất kết nối / treo (không tiến triển) — hoặc user bấm "Kết nối lại". Coordinator sẽ
    /// tự khởi động lại lane và tiếp tục TỪ CHECKPOINT (ưu tiên cùng account), không tính là lỗi
    /// account và không bỏ từ khóa/link.
    /// </summary>
    Reconnect,
}

/// <summary>
/// One self-contained crawl "lane": owns its own Edge window, WebSocket server (on a
/// dynamically allocated port), orchestrator and CDP input controller. Runs a single
/// (account, keyword) search to completion and raises events instead of touching the UI,
/// so several sessions can run in parallel. Reusable across keywords — call
/// <see cref="CloseBrowserAsync"/> between runs and <see cref="DisposeAsync"/> at the end.
/// </summary>
public sealed class SearchSession : IAsyncDisposable
{
    private readonly AppSettingsService _appSettings;
    private readonly SearchTaskStore _taskStore;
    private readonly EdgeManager _edge;

    private WebSocketServer? _ws;
    private SearchOrchestrator? _orchestrator;
    private CdpInputController? _cdpInput;

    public int LaneId { get; }
    public long TaskId { get; private set; }
    public IReadOnlyList<ProductResult> Results => _orchestrator?.Results ?? [];

    /// <summary>Thông điệp lỗi của lần chạy gần nhất (outcome == Error) — để hiển thị lý do ở ô link.</summary>
    public string? LastError { get; private set; }

    // Watchdog "treo": mọi sự kiện (kết nối / progress / sản phẩm / checkpoint) chạm vào mốc này.
    // Im lặng quá IdleTimeoutMs → coi như lane chết → tự kết nối lại (resolve Reconnect).
    private const long IdleTimeoutMs = 120_000;
    private long _lastActivityTick;
    // Completion của lần RunAsync hiện tại — để RequestReconnect()/watchdog kết thúc sớm từ bên ngoài.
    private volatile TaskCompletionSource<SearchRunOutcome>? _runCompletion;

    private void Touch() => Interlocked.Exchange(ref _lastActivityTick, Environment.TickCount64);

    /// <summary>User bấm "Kết nối lại" (hoặc tự động): kết thúc lần chạy hiện tại bằng Reconnect để
    /// coordinator relaunch + tiếp tục từ checkpoint với cùng account. Không đụng tới account pool.</summary>
    public void RequestReconnect() => _runCompletion?.TrySetResult(SearchRunOutcome.Reconnect);

    /// <summary>The shop name captured during a shop-from-link run (read before CloseBrowserAsync).</summary>
    public string ShopName => _orchestrator?.ShopName ?? "";

    /// <summary>Latest crawl checkpoint (1-based) from the last run — used to resume on account swap.</summary>
    public int LastCategoryIndex { get; private set; } = 1;
    public int LastPage { get; private set; } = 1;

    /// <summary>Progress/status text for this lane.</summary>
    public event Action<string>? Log;
    /// <summary>A product was found or updated.</summary>
    public event Action<ProductResult>? ProductFound;
    /// <summary>The lane's extension WebSocket connected (true) or dropped (false).</summary>
    public event Action<bool>? ConnectionChanged;
    /// <summary>Raised after a successful login clears the account's login flag (refresh UI).</summary>
    public event Action? AccountStateChanged;

    public SearchSession(int laneId, AppSettingsService appSettings, SearchTaskStore taskStore)
    {
        LaneId = laneId;
        _appSettings = appSettings;
        _taskStore = taskStore;
        _edge = new EdgeManager(appSettings);
    }

    public async Task<SearchRunOutcome> RunAsync(InstanceConfig account, SearchConfig config, CancellationToken ct, long resumeTaskId = 0)
    {
        // Defensive: drop anything left from a previous run on this session.
        await DisposeRunScopedAsync();

        // Seed the checkpoint from the config so a failure before any pageData resumes at this
        // attempt's intended start (not a stale value from a previous keyword).
        LastCategoryIndex = Math.Max(1, config.ResumeCategoryIndex);
        LastPage = Math.Max(1, config.ResumePage);
        LastError = null;
        Touch(); // mốc hoạt động đầu tiên cho watchdog của lần chạy này

        var port = EdgeManager.FindFreePort();

        string? profileDir;
        string? proxy;
        try
        {
            profileDir = _appSettings.GetProfileDir(account);
            proxy = await _edge.ResolveProxyAsync(account);
        }
        catch (Exception ex)
        {
            Log?.Invoke(ex.Message);
            EdgeManager.ReleasePort(port); // never bound — don't leak the reservation
            return SearchRunOutcome.Error;
        }

        // Bind the WS server on `port` and wire the orchestrator BEFORE launching Edge, so the
        // extension reaches a live server the instant it connects: no window for another lane's
        // FindFreePort to grab this port, and no connect-before-listen race. The crawl stays gated —
        // PrepareSearch() runs only AFTER login below, so the extension's early "ready" (the launch
        // URL carries #_ss_ws={port}) is a no-op until we're logged in.
        // Reuse an existing task when resuming a keyword (keeps its accumulated products + checkpoint
        // across attempts and across app restarts); otherwise create a fresh task.
        if (resumeTaskId > 0)
        {
            TaskId = resumeTaskId;
            _taskStore.UpdateStatus(TaskId, "Running");
        }
        else
        {
            TaskId = _taskStore.CreateTask(config, account);
        }

        _ws = new WebSocketServer(port);
        _ws.Connected += () => { Touch(); ConnectionChanged?.Invoke(true); };
        _ws.Disconnected += () => { Touch(); ConnectionChanged?.Invoke(false); };

        var completion = new TaskCompletionSource<SearchRunOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _runCompletion = completion;
        using var registration = ct.Register(() => completion.TrySetResult(SearchRunOutcome.Cancelled));

        _orchestrator = new SearchOrchestrator(_ws, _appSettings);
        _orchestrator.ProgressChanged += msg => { Touch(); Log?.Invoke(msg); };
        _orchestrator.ProductFound += product => { Touch(); ProductFound?.Invoke(product); };
        _orchestrator.ProductPersisted += product => _taskStore.SaveProduct(TaskId, product);
        _orchestrator.CheckpointChanged += (categoryIndex, categoryName, page) =>
        {
            Touch();
            LastCategoryIndex = Math.Max(1, categoryIndex);
            if (page > 0) LastPage = page;
            _taskStore.UpdateCheckpoint(TaskId, categoryIndex, categoryName, page);
        };
        _orchestrator.CaptchaDetected += () =>
        {
            _taskStore.UpdateStatus(TaskId, "Failed", "Verify/captcha");
            completion.TrySetResult(SearchRunOutcome.CaptchaOrVerify);
        };
        _orchestrator.NetworkErrorDetected += msg =>
        {
            _taskStore.UpdateStatus(TaskId, "Failed", msg);
            completion.TrySetResult(SearchRunOutcome.NetworkError);
        };
        _orchestrator.SearchCompleted += () =>
        {
            _taskStore.UpdateStatus(TaskId, "Completed");
            completion.TrySetResult(SearchRunOutcome.Completed);
        };
        _orchestrator.ErrorOccurred += msg =>
        {
            LastError = msg;
            _taskStore.UpdateStatus(TaskId, "Failed", msg);
            completion.TrySetResult(SearchRunOutcome.Error);
        };

        _ws.Start();
        // The listener now owns the port (HTTP.sys binds synchronously), so the OS won't
        // hand it to another lane — release the reservation that guarded the pre-bind gap.
        EdgeManager.ReleasePort(port);

        _edge.Launch(account, profileDir, proxy, port);
        Log?.Invoke("Edge đang khởi động...");
        await _edge.CleanupRestoredTabsAsync(port, ct);

        if (account.OpenWithShopeeAccount)
        {
            Log?.Invoke("Đang đăng nhập Shopee...");
            var loginSvc = new ShopeeLoginService(_appSettings);
            var ok = await loginSvc.EnsureLoggedInAsync(account, _edge.CdpPort, m => Log?.Invoke(m), ct);
            if (!ok)
            {
                Log?.Invoke("Đăng nhập thất bại.");
                return SearchRunOutcome.Error;
            }
            AccountStateChanged?.Invoke();

            // The login flow navigates to shopee.vn/buyer/login → shopee.vn, dropping the
            // "#_ss_ws={port}" hash the extension reads to find THIS lane's WS port. Each parallel
            // lane uses a random port, so we must re-load shopee.vn with the hash or the extension
            // stays on the 9111 default and never connects → lane hangs.
            await ReinjectWsPortAsync(port, ct);
        }

        // Logged in (or no login needed) → arm the crawl. If the extension already connected (it
        // usually has, from the launch URL hash), PrepareSearch sends "start" right away; otherwise
        // the orchestrator sends it when the next "ready" arrives.
        _orchestrator.PrepareSearch(config);

        // Trusted-input controller; failure is non-fatal (extension falls back to synthetic events).
        _cdpInput = new CdpInputController(_ws, _edge.CdpPort);
        _cdpInput.Log += msg => Log?.Invoke(msg);
        _ = _cdpInput.StartAsync(ct);

        // Watchdog phát hiện trang verify/captcha TRỰC TIẾP qua CDP (độc lập với extension/searchTabId):
        // nếu extension không nhận ra verify (vd searchTabId lệch, SW kẹt) thì lane vẫn đổi account
        // thay vì treo trên trang captcha.
        _ = WatchForVerifyAsync(_edge.CdpPort, completion, ct);

        // Watchdog "treo/mất kết nối": tự kết nối lại nếu lane im lặng quá lâu (xem WatchForIdleAsync).
        _ = WatchForIdleAsync(completion, ct);

        Log?.Invoke("Chờ extension kết nối...");
        try { return await completion.Task; }
        finally { _runCompletion = null; }
    }

    // Tự khôi phục lane treo: nếu không có sự kiện nào (kết nối/progress/sản phẩm/checkpoint) trong
    // IdleTimeoutMs thì coi như lane chết (extension không kết nối lại được, SW kẹt, trang đứng...) và
    // resolve Reconnect để coordinator relaunch + tiếp tục từ checkpoint — thay vì treo vô hạn.
    private async Task WatchForIdleAsync(TaskCompletionSource<SearchRunOutcome> completion, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && !completion.Task.IsCompleted)
            {
                await Task.Delay(15000, ct);
                if (completion.Task.IsCompleted) return;
                var idle = Environment.TickCount64 - Interlocked.Read(ref _lastActivityTick);
                if (idle >= IdleTimeoutMs)
                {
                    Log?.Invoke($"Mất kết nối/treo {idle / 1000}s — tự kết nối lại.");
                    completion.TrySetResult(SearchRunOutcome.Reconnect);
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    // Polls Edge's CDP target list; if any page sits on a /verify/ (captcha) URL, resolve the run as
    // CaptchaOrVerify so the coordinator swaps account — even when the extension's own detection misses it.
    private async Task WatchForVerifyAsync(int cdpPort, TaskCompletionSource<SearchRunOutcome> completion, CancellationToken ct)
    {
        if (cdpPort <= 0) return;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var hits = 0;
        try
        {
            while (!ct.IsCancellationRequested && !completion.Task.IsCompleted)
            {
                await Task.Delay(8000, ct);
                if (completion.Task.IsCompleted) return;
                try
                {
                    var json = await http.GetStringAsync($"http://localhost:{cdpPort}/json/list", ct);
                    using var doc = JsonDocument.Parse(json);
                    var onVerify = doc.RootElement.EnumerateArray().Any(t =>
                        t.TryGetProperty("type", out var ty) && ty.GetString() == "page" &&
                        t.TryGetProperty("url", out var u) &&
                        (u.GetString() ?? "").Contains("/verify/", StringComparison.OrdinalIgnoreCase));
                    // Cần thấy ở 2 lần dò liên tiếp (~16s) để tránh redirect verify thoáng qua.
                    hits = onVerify ? hits + 1 : 0;
                    if (hits >= 2)
                    {
                        Log?.Invoke("Phát hiện trang verify/captcha (CDP) — đổi account.");
                        completion.TrySetResult(SearchRunOutcome.CaptchaOrVerify);
                        return;
                    }
                }
                catch { /* CDP tạm thời không trả lời → thử lại lần sau */ }
            }
        }
        catch (OperationCanceledException) { }
    }

    // Re-loads shopee.vn with the "#_ss_ws={port}" hash so the extension reconnects to this
    // lane's WS port. The "?_sslane" query forces a full (cross-document) navigation — a hash-only
    // change would be a same-document nav that doesn't fire the extension's onUpdated(complete).
    private async Task ReinjectWsPortAsync(int port, CancellationToken ct)
    {
        try
        {
            await using var cdp = await CdpSession.ConnectToPageMatchingAsync(
                _edge.CdpPort,
                url => url.Contains("shopee.vn", StringComparison.OrdinalIgnoreCase)
                       && !url.Contains("shopee.vn/api/", StringComparison.OrdinalIgnoreCase),
                ct);
            await cdp.SendAsync("Page.navigate",
                new { url = $"https://shopee.vn/?_sslane={port}#_ss_ws={port}" }, ct);
            Log?.Invoke($"Nạp lại port WS {port} sau đăng nhập.");
        }
        catch (Exception ex)
        {
            Log?.Invoke("Không nạp lại được port WS sau đăng nhập: " + ex.Message);
        }
    }

    /// <summary>Synchronous, best-effort kill of this lane's Edge window (for shutdown paths).</summary>
    public void KillBrowser() => _edge.Kill();

    /// <summary>Kills this lane's Edge window and tears down the run-scoped WS/CDP/orchestrator.</summary>
    public async Task CloseBrowserAsync()
    {
        await DisposeRunScopedAsync();
        _edge.Kill();
    }

    private async Task DisposeRunScopedAsync()
    {
        var cdp = _cdpInput;
        _cdpInput = null;
        if (cdp is not null)
        {
            try { await cdp.DisposeAsync(); } catch { }
        }

        _orchestrator = null;
        _ws?.Dispose();
        _ws = null;
    }

    public async ValueTask DisposeAsync() => await CloseBrowserAsync();
}

