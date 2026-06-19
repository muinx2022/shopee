namespace ShopeeStatApp.Services;

/// <summary>
/// Persists the set of shops already scanned (keyed by shopId) so file runs skip
/// duplicates instead of re-scanning. Stored as a TSV next to the shop exports.
///
/// Thread-safe: a single instance is shared across all parallel file lanes. Besides the
/// persistent "done" set (the TSV), it keeps an in-memory "in-progress" set so two lanes
/// that hit the SAME shop concurrently don't both crawl it — the first to call
/// <see cref="TryBegin"/> reserves the shop; the others see it immediately and skip.
/// </summary>
public sealed class ScannedShopStore
{
    private readonly object _lock = new();
    private readonly string _path;
    private readonly HashSet<long> _done = [];        // persisted: scanned this run or a previous one
    private readonly HashSet<long> _inProgress = [];  // reserved by a lane this session, not yet completed
    private readonly Dictionary<long, string> _shopNames = [];

    public ScannedShopStore(string directory)
    {
        _path = Path.Combine(directory, "scanned-shops.tsv");
        Load();
    }

    /// <summary>
    /// Atomically reserves <paramref name="shopId"/> for crawling. Returns false (skip) if it
    /// was already scanned (persisted) or is currently being crawled by another lane. Returns
    /// true (proceed) — and records the reservation — otherwise. shopId &lt;= 0 can't be deduped
    /// so it always returns true without reserving.
    /// </summary>
    public bool TryBegin(long shopId)
    {
        if (shopId <= 0) return true;
        lock (_lock)
        {
            if (_done.Contains(shopId) || _inProgress.Contains(shopId)) return false;
            _inProgress.Add(shopId);
            return true;
        }
    }

    /// <summary>Marks a reserved shop as fully scanned: drops the reservation and persists it to the TSV.</summary>
    public void Complete(long shopId, string shopName)
    {
        if (shopId <= 0) return;
        lock (_lock)
        {
            _inProgress.Remove(shopId);
            _shopNames[shopId] = shopName ?? "";
            if (!_done.Add(shopId)) return;
            AppendLine(shopId, shopName);
        }
    }

    /// <summary>Releases a reservation without persisting (link errored/cancelled) so it can be retried.</summary>
    public void Abandon(long shopId)
    {
        if (shopId <= 0) return;
        lock (_lock) _inProgress.Remove(shopId);
    }

    public bool Contains(long shopId)
    {
        if (shopId <= 0) return false;
        lock (_lock) return _done.Contains(shopId);
    }

    /// <summary>Removes a shop from the scanned set so it will be scanned again next run.</summary>
    public void Remove(long shopId)
    {
        if (shopId <= 0) return;
        lock (_lock)
        {
            _inProgress.Remove(shopId);
            _shopNames.Remove(shopId);
            if (!_done.Remove(shopId)) return;
            try
            {
                if (!File.Exists(_path)) return;
                var kept = File.ReadAllLines(_path).Where(line =>
                {
                    var tab = line.IndexOf('\t');
                    var idText = tab >= 0 ? line[..tab] : line;
                    return !(long.TryParse(idText.Trim(), out var id) && id == shopId);
                });
                File.WriteAllLines(_path, kept);
            }
            catch { /* best effort */ }
        }
    }

    public void Add(long shopId, string shopName)
    {
        if (shopId <= 0) return;
        lock (_lock)
        {
            _shopNames[shopId] = shopName ?? "";
            if (!_done.Add(shopId)) return;
            AppendLine(shopId, shopName);
        }
    }

    public string GetDisplayName(long shopId)
    {
        if (shopId <= 0) return "";
        lock (_lock)
        {
            if (_shopNames.TryGetValue(shopId, out var name) && !string.IsNullOrWhiteSpace(name))
                return name.Trim();
            return $"shop-{shopId}";
        }
    }

    /// <summary>Xóa toàn bộ shop đã quét (file TSV + bộ nhớ).</summary>
    public void ClearAll()
    {
        lock (_lock)
        {
            _done.Clear();
            _inProgress.Clear();
            _shopNames.Clear();
            try
            {
                if (File.Exists(_path))
                    File.Delete(_path);
            }
            catch { /* best effort */ }
        }
    }

    // Caller must hold _lock.
    private void AppendLine(long shopId, string? shopName)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var line = $"{shopId}\t{(shopName ?? "").Replace('\t', ' ').Replace('\n', ' ').Trim()}\t{DateTime.Now:O}{Environment.NewLine}";
            File.AppendAllText(_path, line);
        }
        catch { /* best effort */ }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            foreach (var line in File.ReadAllLines(_path))
            {
                var tab = line.IndexOf('\t');
                var idText = tab >= 0 ? line[..tab] : line;
                if (long.TryParse(idText.Trim(), out var id))
                {
                    _done.Add(id);
                    var parts = line.Split('\t');
                    if (parts.Length > 1)
                        _shopNames[id] = parts[1].Trim();
                }
            }
        }
        catch { /* best effort */ }
    }
}
