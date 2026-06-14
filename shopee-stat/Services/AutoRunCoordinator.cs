using System.Collections.Concurrent;

namespace ShopeeStatApp.Services;

/// <summary>
/// Drives the parallel "auto" run: a pool of <see cref="SearchSession"/> lanes pulls keywords
/// from a shared queue. Each keyword is crawled by exactly one account at a time; on
/// captcha/proxy/error the lane borrows a different free account and retries the SAME keyword.
/// No account is used by two lanes at once. All callbacks fire on background threads — the UI
/// layer marshals them onto the UI thread.
/// </summary>
public sealed class AutoRunCoordinator
{
    private readonly AppSettingsService _appSettings;
    private readonly SearchTaskStore _taskStore;
    private readonly IReadOnlyList<InstanceConfig> _accounts;
    private readonly ConcurrentQueue<string> _keywordQueue;
    private readonly int _laneCount;

    // Search params (read once from the UI when the run starts).
    private readonly string _region;
    private readonly long _minPrice;
    private readonly int _minSold;

    // Live sessions, for prompt browser teardown on Stop / form close.
    private readonly object _sesLock = new();
    private readonly List<SearchSession> _sessions = [];

    // Account pool guarded by _accLock.
    private readonly object _accLock = new();
    private readonly HashSet<string> _busy = [];
    private readonly Dictionary<string, long> _restUntilTick = [];
    private const long RestMillis = 60_000;

    // Per-lane cancellation: the "✕" on a lane tab cancels just that lane's CURRENT keyword
    // (the worker then moves on to the next keyword); the keyword is left NOT-completed.
    private readonly object _laneCtsLock = new();
    private readonly Dictionary<int, CancellationTokenSource> _laneCts = [];

    // Lane events (laneId is 1-based).
    public event Action<int, string>? LaneStatus;
    public event Action<int, ProductResult>? LaneProduct;
    public event Action<int, string, string, bool>? LaneAssigned;   // laneId, keyword, accountName, isFirstAttempt
    public event Action<int, bool>? LaneConnection;                 // laneId, connected
    public event Action<int>? LaneFinished;
    public event Action<string>? LaneKeywordCompleted;              // keyword (đã hoàn thành → đánh dấu used)
    public event Action<int, string>? LaneKeywordReleased;          // laneId, keyword (rời lane: xong/skip/bỏ)
    public event Action? TasksChanged;
    public event Action? AccountsChanged;

    /// <summary>Persist a finished keyword's products. Invoked off the UI thread.</summary>
    public Func<string, IReadOnlyList<ProductResult>, Task>? SaveExcel;

    public AutoRunCoordinator(
        AppSettingsService appSettings,
        SearchTaskStore taskStore,
        IReadOnlyList<InstanceConfig> accounts,
        IEnumerable<string> keywords,
        int laneCount,
        string region,
        long minPrice,
        int minSold)
    {
        _appSettings = appSettings;
        _taskStore = taskStore;
        _accounts = accounts;
        _keywordQueue = new ConcurrentQueue<string>(keywords);
        _laneCount = Math.Max(1, laneCount);
        _region = region;
        _minPrice = minPrice;
        _minSold = minSold;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var workers = new List<Task>();
        for (var lane = 1; lane <= _laneCount; lane++)
        {
            var laneId = lane;
            workers.Add(Task.Run(() => WorkerLoopAsync(laneId, ct), ct));
        }
        await Task.WhenAll(workers);
    }

    /// <summary>Best-effort synchronous kill of every lane's Brave window (Stop / form close).</summary>
    public void KillAllBrowsers()
    {
        lock (_sesLock)
            foreach (var s in _sessions)
            {
                try { s.KillBrowser(); } catch { }
            }
    }

    /// <summary>"✕" on a lane tab: cancel ONLY this lane's current keyword and kill its browser;
    /// the keyword is left not-completed (resumable). The worker then continues to the next keyword.</summary>
    public void StopLane(int laneId)
    {
        lock (_laneCtsLock)
            if (_laneCts.TryGetValue(laneId, out var cts))
            {
                try { cts.Cancel(); } catch { }
            }
        lock (_sesLock)
        {
            var s = _sessions.FirstOrDefault(x => x.LaneId == laneId);
            try { s?.KillBrowser(); } catch { }
        }
    }

    private async Task WorkerLoopAsync(int laneId, CancellationToken ct)
    {
        var session = new SearchSession(laneId, _appSettings, _taskStore);
        session.Log += msg => LaneStatus?.Invoke(laneId, msg);
        session.ProductFound += product => LaneProduct?.Invoke(laneId, product);
        session.ConnectionChanged += connected => LaneConnection?.Invoke(laneId, connected);
        session.AccountStateChanged += () => AccountsChanged?.Invoke();
        lock (_sesLock) _sessions.Add(session);

        try
        {
            while (!ct.IsCancellationRequested && _keywordQueue.TryDequeue(out var keyword))
            {
                // Linked token so StopLane(laneId) cancels just THIS keyword, not the whole run.
                using (var laneCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    lock (_laneCtsLock) _laneCts[laneId] = laneCts;
                    try { await ProcessKeywordAsync(session, laneId, keyword, laneCts.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Lane-only cancel ("✕"): skip this keyword and keep the worker going.
                        LaneStatus?.Invoke(laneId, $"Đã dừng từ khóa \"{keyword}\" (chưa kết thúc).");
                    }
                    finally
                    {
                        lock (_laneCtsLock) _laneCts.Remove(laneId);
                        LaneKeywordReleased?.Invoke(laneId, keyword);
                    }
                }
                TasksChanged?.Invoke();
                if (!ct.IsCancellationRequested)
                    await Task.Delay(800, ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            try { await session.DisposeAsync(); } catch { }
            LaneFinished?.Invoke(laneId);
        }
    }

    private async Task ProcessKeywordAsync(SearchSession session, int laneId, string keyword, CancellationToken ct)
    {
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // First attempt for THIS keyword on this lane → the UI clears the lane grid then.
        // Retries (account swap) keep firstAttempt=false so the grid accumulates instead of
        // resetting to the new attempt's count (which looked like results "shrinking").
        var firstAttempt = true;

        // Reuse the keyword's SQLite task as the single source of truth (checkpoint + accumulated
        // products) so resume works across account swaps AND across app restarts. taskId == 0 → fresh.
        var taskId = _taskStore.GetResumableTaskId(keyword);
        var resumeCategoryIndex = 1;
        var resumePage = 1;
        var reloadedGrid = false;
        if (taskId > 0 && _taskStore.GetTask(taskId) is { } rec)
        {
            resumeCategoryIndex = Math.Max(1, rec.ResumeCategoryIndex);
            resumePage = Math.Max(1, rec.CurrentPage);
        }

        while (!ct.IsCancellationRequested)
        {
            var account = await BorrowAccountAsync(tried, ct);
            if (account is null)
            {
                // Exhausted every account for this keyword → save what we gathered, then give up.
                await SaveOnceAsync(laneId, keyword, taskId > 0 ? _taskStore.GetProducts(taskId) : []);
                LaneStatus?.Invoke(laneId, $"Hết account khả dụng cho \"{keyword}\" — bỏ qua từ khóa này.");
                return;
            }
            tried.Add(account.Id);

            var rest = false;
            try
            {
                LaneAssigned?.Invoke(laneId, keyword, account.DisplayName, firstAttempt);
                // Resuming a prior run: re-show its already-collected products in the (just-cleared)
                // lane grid so the count doesn't look like it restarted from zero.
                if (firstAttempt && taskId > 0 && !reloadedGrid)
                {
                    foreach (var p in _taskStore.GetProducts(taskId)) LaneProduct?.Invoke(laneId, p);
                    reloadedGrid = true;
                }
                firstAttempt = false;
                LaneStatus?.Invoke(laneId, resumeCategoryIndex > 1 || resumePage > 1
                    ? $"Tài khoản \"{account.DisplayName}\", từ khóa \"{keyword}\" — tiếp tục danh mục {resumeCategoryIndex}, trang {resumePage}."
                    : $"Tài khoản \"{account.DisplayName}\", từ khóa \"{keyword}\".");

                var config = BuildConfig(keyword, resumeCategoryIndex, resumePage);
                var outcome = await session.RunAsync(account, config, ct, taskId);
                taskId = session.TaskId; // capture the id created on the first fresh attempt
                // Remember how far this attempt got so the next account resumes there, not at the start.
                resumeCategoryIndex = Math.Max(1, session.LastCategoryIndex);
                resumePage = Math.Max(1, session.LastPage);
                await session.CloseBrowserAsync();

                if (outcome == SearchRunOutcome.Cancelled)
                {
                    // Stopped via "✕" (or whole-run stop): leave the task resumable ("chưa kết thúc").
                    if (taskId > 0) _taskStore.UpdateStatus(taskId, "Stopped");
                    return;
                }

                if (outcome == SearchRunOutcome.Completed)
                {
                    var results = _taskStore.GetProducts(taskId); // union across all attempts + prior runs
                    await SaveOnceAsync(laneId, keyword, results); // one file per keyword
                    LaneKeywordCompleted?.Invoke(keyword);
                    LaneStatus?.Invoke(laneId, $"Xong \"{keyword}\" ({results.Count} sản phẩm).");
                    return;
                }

                // Failed (captcha/network/error): retry with another account, resuming at the checkpoint.
                rest = outcome is SearchRunOutcome.CaptchaOrVerify or SearchRunOutcome.NetworkError;
                LaneStatus?.Invoke(laneId, $"Lỗi ({outcome}) ở \"{keyword}\" — đổi account, tiếp tục từ danh mục {resumeCategoryIndex}, trang {resumePage}.");
            }
            catch (OperationCanceledException)
            {
                try { await session.CloseBrowserAsync(); } catch { }
                throw;
            }
            catch (Exception ex)
            {
                LaneStatus?.Invoke(laneId, "Lỗi lane: " + ex.Message);
                try { await session.CloseBrowserAsync(); } catch { }
                rest = true;
            }
            finally
            {
                ReleaseAccount(account, rest);
            }

            if (!ct.IsCancellationRequested)
                await Task.Delay(1000, ct);
        }
    }

    private async Task SaveOnceAsync(int laneId, string keyword, IReadOnlyList<ProductResult> results)
    {
        if (results.Count == 0 || SaveExcel is null) return;
        try { await SaveExcel(keyword, results); }
        catch (Exception ex) { LaneStatus?.Invoke(laneId, "Lỗi lưu Excel: " + ex.Message); }
    }

    private SearchConfig BuildConfig(string keyword, int resumeCategoryIndex, int resumePage) => new()
    {
        Keyword = keyword,
        RegionFilterText = _region,
        MinPriceVnd = _minPrice,
        MinMonthlySold = _minSold,
        CheckVariantStock = false,
        ResumePage = resumePage,
        ResumeCategoryIndex = resumeCategoryIndex,
    };

    /// <summary>
    /// Borrows a free account not yet tried for the current keyword. Prefers accounts that are
    /// neither busy nor resting; waits while candidates exist but are busy on other lanes;
    /// returns null only when every untried account is exhausted.
    /// </summary>
    private async Task<InstanceConfig?> BorrowAccountAsync(HashSet<string> tried, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            lock (_accLock)
            {
                var candidates = _accounts.Where(a => !tried.Contains(a.Id)).ToList();
                if (candidates.Count == 0)
                    return null; // exhausted for this keyword

                var now = Environment.TickCount64;
                var pick =
                    candidates.FirstOrDefault(a => !_busy.Contains(a.Id) && !IsResting(a.Id, now))
                    ?? candidates.FirstOrDefault(a => !_busy.Contains(a.Id));

                if (pick is not null)
                {
                    _busy.Add(pick.Id);
                    return pick;
                }
                // else: all untried candidates are busy on other lanes → wait below.
            }
            await Task.Delay(500, ct);
        }
        return null;
    }

    private bool IsResting(string accountId, long now) =>
        _restUntilTick.TryGetValue(accountId, out var until) && now < until;

    private void ReleaseAccount(InstanceConfig account, bool rest)
    {
        lock (_accLock)
        {
            _busy.Remove(account.Id);
            if (rest)
                _restUntilTick[account.Id] = Environment.TickCount64 + RestMillis;
        }
    }
}
