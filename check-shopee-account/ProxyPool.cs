namespace CheckShopeeAccount;

/// <summary>
/// Quản lý danh sách proxy (kiotproxy key và/hoặc proxy trực tiếp) và cấp cho mỗi tài khoản
/// một IP MỚI. Quy tắc:
///  - Khi cần proxy: thử key hiện tại bằng /new. Nếu key đó chưa tới giờ đổi (nextRequestAt)
///    thì chuyển sang key khác để lấy IP mới.
///  - Nếu tất cả key đều đang chờ → đợi tới key sớm nhất rồi lấy IP mới.
///  - Mỗi key nhớ IP hiện tại + mốc được đổi tiếp; có IP mới thì thay IP cũ (cập nhật dần).
///  - Proxy trực tiếp (host:port) thì dùng nguyên, không có giờ chờ.
/// Trạng thái này có thể lưu/khôi phục qua settings để chạy lại vẫn tôn trọng thời gian chờ.
/// </summary>
public sealed class ProxyPool
{
    public sealed class Entry
    {
        public string Raw = "";            // key hoặc proxy trực tiếp
        public bool IsKey;
        public string? CurrentProxy;       // "http://ip:port"
        public string? CurrentIp;
        public long NextChangeAtMs;        // epoch ms; <= now nghĩa là đổi được ngay
    }

    private readonly List<Entry> _entries;
    private int _cursor;

    // Cấp proxy phải tuần tự khi check nhiều tk song song: nếu 2 luồng cùng gọi sẽ đua _cursor
    // và state của Entry (CurrentIp/NextChangeAtMs) → 2 luồng dính trùng IP hoặc hỏng con trỏ.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public event Action<string>? Log;

    public IReadOnlyList<Entry> Entries => _entries;
    public int Count => _entries.Count;

    public ProxyPool(IEnumerable<string> rawLines, IReadOnlyDictionary<string, (string? ip, long next)> seed)
    {
        _entries = [];
        foreach (var raw in rawLines)
        {
            var isKey = !IsDirectProxy(raw);
            var e = new Entry { Raw = raw, IsKey = isKey };
            if (isKey)
            {
                if (seed.TryGetValue(raw, out var s)) { e.CurrentIp = s.ip; e.NextChangeAtMs = s.next; }
            }
            else
            {
                e.CurrentProxy = NormalizeDirectProxy(raw);
                e.CurrentIp = raw;
            }
            _entries.Add(e);
        }
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Cấp 1 proxy có IP mới cho tài khoản kế tiếp; null nếu không có proxy nào hoạt động.
    /// Tuần tự hoá qua <see cref="_gate"/> để an toàn khi nhiều luồng check song song cùng xin proxy.</summary>
    public async Task<string?> AcquireFreshAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await AcquireFreshCoreAsync(ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<string?> AcquireFreshCoreAsync(CancellationToken ct)
    {
        if (_entries.Count == 0) return null;

        // Vòng 1: tìm 1 entry có thể cấp IP mới ngay (proxy trực tiếp, hoặc key đã tới giờ đổi).
        for (var n = 0; n < _entries.Count; n++)
        {
            ct.ThrowIfCancellationRequested();
            var idx = (_cursor + n) % _entries.Count;
            var e = _entries[idx];

            if (!e.IsKey)
            {
                _cursor = idx + 1;
                Log?.Invoke($"  proxy trực tiếp: {e.CurrentProxy}");
                return e.CurrentProxy;
            }

            var now = NowMs();
            if (now < e.NextChangeAtMs)
            {
                Log?.Invoke($"  key …{Tail(e.Raw)} còn chờ {(e.NextChangeAtMs - now) / 1000}s → thử proxy khác");
                continue;
            }

            var r = await KiotProxyClient.FetchNewAsync(e.Raw, ct);
            if (r.Proxy is not null)
            {
                var changed = !string.Equals(e.CurrentIp, r.Ip, StringComparison.Ordinal);
                e.CurrentProxy = r.Proxy;
                e.CurrentIp = r.Ip;
                e.NextChangeAtMs = r.NextChangeAtMs;
                _cursor = idx + 1;
                Log?.Invoke(changed
                    ? $"  key …{Tail(e.Raw)} → IP mới {r.Ip} (đổi tiếp sau ~{Math.Max(0, (r.NextChangeAtMs - now) / 1000)}s)"
                    : $"  key …{Tail(e.Raw)} → IP {r.Ip} (chưa đổi, đợi sau)");
                return r.Proxy;
            }
            Log?.Invoke($"  key …{Tail(e.Raw)} lỗi: {r.Error} → thử proxy khác");
        }

        // Vòng 2: tất cả key đang chờ → đợi key sớm nhất rồi lấy IP mới.
        var soonest = _entries
            .Where(x => x.IsKey)
            .OrderBy(x => x.NextChangeAtMs)
            .FirstOrDefault();

        if (soonest is null)
            return _entries[0].CurrentProxy; // toàn proxy trực tiếp (đã trả ở vòng 1)

        var wait = soonest.NextChangeAtMs - NowMs();
        if (wait > 0)
        {
            Log?.Invoke($"  tất cả key đang chờ — đợi {wait / 1000}s để có IP mới…");
            await Task.Delay((int)Math.Min(wait + 1000, 150_000), ct);
        }

        var rr = await KiotProxyClient.FetchNewAsync(soonest.Raw, ct);
        if (rr.Proxy is not null)
        {
            soonest.CurrentProxy = rr.Proxy;
            soonest.CurrentIp = rr.Ip;
            soonest.NextChangeAtMs = rr.NextChangeAtMs;
            Log?.Invoke($"  key …{Tail(soonest.Raw)} → IP mới {rr.Ip}");
            return rr.Proxy;
        }

        Log?.Invoke($"  key …{Tail(soonest.Raw)} vẫn lỗi: {rr.Error}" +
                    (soonest.CurrentProxy is not null ? " → dùng tạm IP cũ" : ""));
        return soonest.CurrentProxy;
    }

    public static bool IsDirectProxy(string s)
    {
        if (s.Contains("://")) return true;
        var idx = s.LastIndexOf(':');
        return idx > 0 && idx < s.Length - 1 && s[(idx + 1)..].All(char.IsDigit);
    }

    private static string NormalizeDirectProxy(string s) => s.Contains("://") ? s : "http://" + s;

    private static string Tail(string s) => s.Length <= 4 ? s : s[^4..];
}
