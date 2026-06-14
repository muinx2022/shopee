using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace ShopeeStatApp.Services;

/// <summary>
/// Drives the parallel "search by file" run: a pool of <see cref="SearchSession"/> lanes pulls
/// Excel files from a shared queue. Each file is processed by one lane = one account, which
/// crawls the file's product links sequentially (shop-from-link mode). When a file's account
/// hits captcha/network errors it is rested and the lane borrows a different free account to
/// continue the SAME file. No account runs two lanes at once. A shared <see cref="ScannedShopStore"/>
/// reserves shops atomically so two parallel accounts never crawl the same shop. All callbacks
/// fire on background threads — the UI layer marshals them onto the UI thread.
/// </summary>
public sealed class FileRunCoordinator
{
    private readonly AppSettingsService _appSettings;
    private readonly SearchTaskStore _taskStore;
    private readonly IReadOnlyList<InstanceConfig> _accounts;
    private readonly ConcurrentQueue<string> _fileQueue;
    private readonly int _laneCount;
    private readonly long _minPrice;
    private readonly int _minSold;
    private readonly ScannedShopStore _shopStore;

    // Live sessions, for browser teardown on Stop / form close.
    private readonly object _sesLock = new();
    private readonly List<SearchSession> _sessions = [];

    // Account pool guarded by _accLock (same scheme as AutoRunCoordinator).
    private readonly object _accLock = new();
    private readonly HashSet<string> _busy = [];
    private readonly Dictionary<string, long> _restUntilTick = [];
    private const long RestMillis = 60_000;

    // Per-lane cancellation + skip list for the "✕" on a file tab.
    private readonly object _laneCtsLock = new();
    private readonly Dictionary<int, CancellationTokenSource> _laneCts = [];
    private readonly HashSet<string> _skippedFiles = new(StringComparer.OrdinalIgnoreCase);

    // Lane events (laneId is 1-based).
    public event Action<int, string>? LaneStatus;
    public event Action<int, ProductResult>? LaneProduct;
    public event Action<int, string, string, IReadOnlyList<LinkFileStore.LinkRow>>? LaneAssignedFile; // laneId, filePath, accountName, rows
    public event Action<int, int, string>? LaneLinkStatus;  // laneId, rowNumber, status
    public event Action<int>? LaneFinished;
    public event Action? AccountsChanged;

    /// <summary>Persist a finished shop's products to Excel (fileKeyword, shopName, products). Off the UI thread.</summary>
    public Func<string, string, IReadOnlyList<ProductResult>, Task>? SaveShopExcel;

    public FileRunCoordinator(
        AppSettingsService appSettings,
        SearchTaskStore taskStore,
        IReadOnlyList<InstanceConfig> accounts,
        IEnumerable<string> filePaths,
        int laneCount,
        long minPrice,
        int minSold,
        ScannedShopStore shopStore)
    {
        _appSettings = appSettings;
        _taskStore = taskStore;
        _accounts = accounts;
        _fileQueue = new ConcurrentQueue<string>(filePaths);
        _laneCount = Math.Max(1, laneCount);
        _minPrice = minPrice;
        _minSold = minSold;
        _shopStore = shopStore;
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

    /// <summary>"✕" on a file tab while it is RUNNING on this lane: cancel just this file's crawl and
    /// kill its browser. Pending links stay pending ("chưa kết thúc"); the worker moves to the next file.</summary>
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

    /// <summary>"✕" on a file tab that is still QUEUED (not started): skip it when a worker dequeues it.</summary>
    public void SkipFile(string filePath)
    {
        lock (_laneCtsLock) _skippedFiles.Add(filePath);
    }

    private bool IsSkipped(string filePath)
    {
        lock (_laneCtsLock) return _skippedFiles.Contains(filePath);
    }

    private async Task WorkerLoopAsync(int laneId, CancellationToken ct)
    {
        var session = new SearchSession(laneId, _appSettings, _taskStore);
        session.Log += msg => LaneStatus?.Invoke(laneId, msg);
        session.ProductFound += product => LaneProduct?.Invoke(laneId, product);
        session.AccountStateChanged += () => AccountsChanged?.Invoke();
        lock (_sesLock) _sessions.Add(session);

        try
        {
            while (!ct.IsCancellationRequested && _fileQueue.TryDequeue(out var filePath))
            {
                if (IsSkipped(filePath)) continue; // "✕" trên file đang chờ → bỏ qua
                // Linked token so StopLane(laneId) cancels just THIS file, not the whole run.
                using (var laneCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    lock (_laneCtsLock) _laneCts[laneId] = laneCts;
                    try { await ProcessFileAsync(session, laneId, filePath, laneCts.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        LaneStatus?.Invoke(laneId, $"Đã dừng file \"{Path.GetFileName(filePath)}\" (chưa kết thúc).");
                    }
                    finally
                    {
                        lock (_laneCtsLock) _laneCts.Remove(laneId);
                    }
                }
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

    private async Task ProcessFileAsync(SearchSession session, int laneId, string filePath, CancellationToken ct)
    {
        var fileName = Path.GetFileName(filePath);
        var store = new LinkFileStore(filePath);
        List<LinkFileStore.LinkRow> rows;
        try
        {
            rows = store.Load();
        }
        catch (Exception ex)
        {
            LaneStatus?.Invoke(laneId, $"Không đọc được \"{fileName}\": {ex.Message}");
            return;
        }

        var pending = rows.Where(r => !r.IsDone).ToList();

        // Borrow the file's first account; held until the file finishes (swapped only on captcha/network).
        var account = await BorrowAccountAsync([], ct);
        if (account is null) return; // cancelled

        LaneAssignedFile?.Invoke(laneId, filePath, account.DisplayName, rows);
        LaneStatus?.Invoke(laneId, $"File \"{fileName}\" — tài khoản \"{account.DisplayName}\" ({pending.Count} link).");

        try
        {
            for (var i = 0; i < pending.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var linkRow = pending[i];
                var shopId = ParseShopId(linkRow.Link);

                // Atomic reserve: skip shops already scanned (this run or before) or being crawled by another lane.
                if (!_shopStore.TryBegin(shopId))
                {
                    SetLinkStatus(store, laneId, linkRow.RowNumber, "Trùng shop (bỏ qua)");
                    LaneStatus?.Invoke(laneId, $"Link dòng {linkRow.RowNumber}: shop {shopId} đã quét/đang quét — bỏ qua.");
                    continue;
                }

                var triedForLink = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var resolved = false;
                while (!resolved)
                {
                    ct.ThrowIfCancellationRequested();
                    if (account is null)
                    {
                        account = await BorrowAccountAsync(triedForLink, ct);
                        if (account is null)
                        {
                            _shopStore.Abandon(shopId);
                            SetLinkStatus(store, laneId, linkRow.RowNumber, "Lỗi: hết account khả dụng");
                            resolved = true;
                            break;
                        }
                        LaneStatus?.Invoke(laneId, $"File \"{fileName}\" — đổi sang tài khoản \"{account.DisplayName}\".");
                    }

                    SetLinkStatus(store, laneId, linkRow.RowNumber, LinkFileStore.Processing);
                    LaneStatus?.Invoke(laneId, $"Link {i + 1}/{pending.Count} (dòng {linkRow.RowNumber}) — \"{account.DisplayName}\": {linkRow.Link}");

                    var cfg = new SearchConfig
                    {
                        Mode = "shopFromLink",
                        ProductLink = linkRow.Link,
                        Keyword = linkRow.Link,
                        MinPriceVnd = _minPrice,
                        MinMonthlySold = _minSold,
                        RegionFilterText = "",
                        CheckVariantStock = false,
                        FilterPriceClientSide = true,
                        ResumeCategoryIndex = 1,
                    };

                    SearchRunOutcome outcome;
                    List<ProductResult> results;
                    string shopName;
                    try
                    {
                        outcome = await session.RunAsync(account, cfg, ct);
                        results = session.Results.ToList();
                        shopName = session.ShopName;
                    }
                    catch (OperationCanceledException)
                    {
                        _shopStore.Abandon(shopId);
                        throw;
                    }
                    finally
                    {
                        try { await session.CloseBrowserAsync(); } catch { }
                    }

                    if (outcome == SearchRunOutcome.Cancelled)
                    {
                        _shopStore.Abandon(shopId);
                        return;
                    }

                    if (outcome == SearchRunOutcome.Completed)
                    {
                        _shopStore.Complete(shopId, shopName);
                        if (results.Count > 0)
                        {
                            try { _taskStore.SaveShopProducts(shopId, shopName, linkRow.Link, results); }
                            catch (Exception ex) { LaneStatus?.Invoke(laneId, "Lưu CSDL lỗi: " + ex.Message); }
                            var shopPart = string.IsNullOrWhiteSpace(shopName) ? $"shop-{shopId}" : shopName;
                            await SaveShopOnceAsync(laneId, Path.GetFileNameWithoutExtension(filePath), shopPart, results);
                        }
                        SetLinkStatus(store, laneId, linkRow.RowNumber, LinkFileStore.Processed);
                        LaneStatus?.Invoke(laneId, $"Xong shop \"{shopName}\" ({results.Count} sản phẩm).");
                        resolved = true; // keep account for the next link
                    }
                    else if (outcome is SearchRunOutcome.CaptchaOrVerify or SearchRunOutcome.NetworkError)
                    {
                        // Keep the reservation; rest this account and retry the SAME link on another.
                        triedForLink.Add(account.Id);
                        ReleaseAccount(account, rest: true);
                        account = null;
                        LaneStatus?.Invoke(laneId, $"Lỗi ({outcome}) ở dòng {linkRow.RowNumber} — đổi account, thử lại link.");
                    }
                    else // Error
                    {
                        _shopStore.Abandon(shopId);
                        SetLinkStatus(store, laneId, linkRow.RowNumber, "Lỗi: " + outcome);
                        resolved = true; // keep account, move to next link
                    }
                }

                if (!ct.IsCancellationRequested)
                    await Task.Delay(1200, ct);
            }

            LaneStatus?.Invoke(laneId, $"Hoàn thành file \"{fileName}\".");
        }
        finally
        {
            if (account is not null) ReleaseAccount(account, rest: false);
        }
    }

    private async Task SaveShopOnceAsync(int laneId, string fileKeyword, string shopName, IReadOnlyList<ProductResult> results)
    {
        if (SaveShopExcel is null) return;
        try { await SaveShopExcel(fileKeyword, shopName, results); }
        catch (Exception ex) { LaneStatus?.Invoke(laneId, "Lỗi lưu Excel: " + ex.Message); }
    }

    // Writes the row status into the Excel file and notifies the UI.
    private void SetLinkStatus(LinkFileStore store, int laneId, int rowNumber, string status)
    {
        try { store.MarkStatus(rowNumber, status); }
        catch (Exception ex) { LaneStatus?.Invoke(laneId, "Ghi file lỗi: " + ex.Message); }
        LaneLinkStatus?.Invoke(laneId, rowNumber, status);
    }

    /// <summary>
    /// Borrows a free account not in <paramref name="tried"/>. Prefers accounts neither busy nor
    /// resting; waits while candidates exist but are busy; returns null only when all are exhausted
    /// or the run is cancelled.
    /// </summary>
    private async Task<InstanceConfig?> BorrowAccountAsync(HashSet<string> tried, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            lock (_accLock)
            {
                var candidates = _accounts.Where(a => !tried.Contains(a.Id)).ToList();
                if (candidates.Count == 0)
                    return null; // exhausted

                var now = Environment.TickCount64;
                var pick =
                    candidates.FirstOrDefault(a => !_busy.Contains(a.Id) && !IsResting(a.Id, now))
                    ?? candidates.FirstOrDefault(a => !_busy.Contains(a.Id));

                if (pick is not null)
                {
                    _busy.Add(pick.Id);
                    return pick;
                }
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

    private static readonly Regex IdRx = new(
        @"/product/(\d+)/(\d+)|-i\.(\d+)\.(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static long ParseShopId(string link)
    {
        var m = IdRx.Match(link ?? "");
        if (!m.Success) return 0;
        var shop = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[3].Value;
        return long.TryParse(shop, out var s) ? s : 0;
    }
}
