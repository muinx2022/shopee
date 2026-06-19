namespace ShopeeStatApp.Services;

public sealed class SearchOrchestrator
{
    private readonly WebSocketServer _ws;
    private readonly AppSettingsService _appSettings;
    private readonly List<ProductResult> _results = [];

    private SearchConfig? _searchConfig;
    private SearchConfig? _pendingConfig;
    private bool _searchActive;
    // True once the extension has sent "ready". Lets PrepareSearch (called after login) start
    // the crawl immediately when the extension connected earlier (from the launch URL hash)
    // and so won't send another "ready".
    private bool _extensionReady;
    public IReadOnlyList<ProductResult> Results => _results;

    /// <summary>Shop name captured from the shop page (shop-from-link mode); null until known.</summary>
    public string? ShopName { get; private set; }

    public event Action<string>? ProgressChanged;
    public event Action<ProductResult>? ProductFound;
    public event Action<ProductResult>? ProductPersisted;
    public event Action<int, string, int>? CheckpointChanged;
    public event Action? CaptchaDetected;
    public event Action<string>? NetworkErrorDetected;
    public event Action? SearchCompleted;
    public event Action<string>? ErrorOccurred;

    public SearchOrchestrator(WebSocketServer ws, AppSettingsService appSettings)
    {
        _ws = ws;
        _appSettings = appSettings;
        _ws.MessageReceived += OnMessage;
    }

    /// <summary>
    /// Arms the crawl: stores the search config so "start" is sent either immediately (if the
    /// extension is already connected) or when the next "ready" arrives. Safe to call after
    /// ws.Start() — e.g. only once login has completed, to gate the crawl until then.
    /// </summary>
    public void PrepareSearch(SearchConfig config)
    {
        _pendingConfig = config;
        _searchConfig = config;
        _searchActive = true;
        _results.Clear();
        ShopName = null;

        // Extension already connected (e.g. it connected from the launch URL hash before we
        // finished logging in)? Send "start" now — there won't be another "ready" to trigger it.
        if (_extensionReady) SendPendingSearchOnReady();
    }

    public async Task StartAsync(SearchConfig config)
    {
        _searchConfig = config;
        _searchActive = true;
        await SendStartCommandAsync(config);
    }

    private async Task SendStartCommandAsync(SearchConfig config)
    {
        await _ws.SendAsync(new
        {
            action = "start",
            mode = config.Mode,
            keyword = config.Keyword,
            link = config.ProductLink,
            resumeCategoryIndex = Math.Max(1, config.ResumeCategoryIndex),
            resumePage = Math.Max(1, config.ResumePage),
            filters = new
            {
                minPrice = config.MinPriceVnd,
                minSold = config.MinMonthlySold,
                checkStock = config.CheckVariantStock,
            },
            apis = _appSettings.ApiConfig,
        });
    }

    /// <summary>
    /// Resumes after a captcha/verify pause by re-issuing the search command from
    /// the latest checkpoint. The extension's startSearch() calls stopSearch() first,
    /// so a fresh "start" cleanly replaces the dead crawl loop instead of trying to
    /// revive it. _searchConfig.ResumeCategoryIndex is kept current in HandlePageData.
    /// </summary>
    public async Task ResumeFromCheckpointAsync()
    {
        if (_searchConfig is null) return;
        _searchActive = true;
        await SendStartCommandAsync(_searchConfig);
    }

    public async Task StopAsync()
    {
        _searchActive = false;
        await _ws.SendAsync(new { action = "stop" });
    }

    /// <summary>Pauses the live crawl: the extension waits at the next safe point. Edge/WS stay alive.</summary>
    public async Task PauseAsync()
    {
        await _ws.SendAsync(new { action = "pause" });
    }

    /// <summary>Resumes a live paused crawl (no error involved) — continues where it left off.</summary>
    public async Task ResumeAsync()
    {
        await _ws.SendAsync(new { action = "resume" });
    }

    private void OnMessage(JsonDocument doc)
    {
        var root = doc.RootElement;
        if (!root.TryGetProperty("action", out var actionProp)) return;
        var action = actionProp.GetString();

        switch (action)
        {
            case "ready":
                _extensionReady = true;
                ProgressChanged?.Invoke("Extension sẵn sàng.");
                SendPendingSearchOnReady();
                break;

            case "shopInfo":
                if (root.TryGetProperty("name", out var shopNameEl))
                {
                    var name = shopNameEl.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        ShopName = name.Trim();
                        ProgressChanged?.Invoke($"Shop: {ShopName}");
                    }
                }
                break;

            case "captcha":
                _searchActive = false;
                CaptchaDetected?.Invoke();
                break;

            case "networkError":
                _searchActive = false;
                var networkMessage = root.TryGetProperty("message", out var networkMsg)
                    ? networkMsg.GetString() ?? "Lỗi mạng/proxy"
                    : "Lỗi mạng/proxy";
                NetworkErrorDetected?.Invoke(networkMessage);
                break;

            case "progress":
                if (root.TryGetProperty("message", out var msg))
                    ProgressChanged?.Invoke(msg.GetString() ?? "");
                break;

            case "pageData":
                HandlePageData(root);
                break;

            case "done":
                _searchActive = false;
                ProgressChanged?.Invoke($"Hoàn thành. Tổng: {_results.Count} sản phẩm.");
                SearchCompleted?.Invoke();
                break;

            case "error":
                _searchActive = false;
                if (root.TryGetProperty("message", out var err))
                    ErrorOccurred?.Invoke(err.GetString() ?? "Lỗi không xác định");
                break;
        }
    }

    private void SendPendingSearchOnReady()
    {
        var config = _pendingConfig;
        var isReconnectResume = false;
        if (config is null)
        {
            if (!_searchActive || _searchConfig is null) return;
            config = _searchConfig;
            isReconnectResume = true;
        }
        else
        {
            _pendingConfig = null;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                ProgressChanged?.Invoke(isReconnectResume
                    ? "Extension reconnect, gửi lại lệnh tìm kiếm hiện tại..."
                    : "Đang gửi lệnh tìm kiếm sang extension...");
                await SendStartCommandAsync(config);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke("Lỗi gửi lệnh tìm kiếm: " + ex.Message);
            }
        });
    }

    private void HandlePageData(JsonElement root)
    {
        var source = root.TryGetProperty("data", out var dataEl) ? dataEl : root;
        var srcName = source.TryGetProperty("source", out var sn) ? sn.GetString() ?? "?" : "?";
        var categoryIndex = source.TryGetProperty("categoryIndex", out var ci) ? ci.GetInt32() : 1;
        var categoryName = source.TryGetProperty("category", out var cat) ? cat.GetString() ?? "" : "";
        var page = source.TryGetProperty("page", out var pageEl) ? pageEl.GetInt32() : 0;
        var idx = Math.Max(1, categoryIndex);
        CheckpointChanged?.Invoke(idx, categoryName, page);
        // Keep the live config in sync so a reconnect (which re-sends _searchConfig
        // via SendPendingSearchOnReady) resumes at the latest category, not category 1.
        if (_searchConfig is not null)
            _searchConfig.ResumeCategoryIndex = idx;

        // Structured items (have name/price/sold)
        if (source.TryGetProperty("items", out var itemsEl) && itemsEl.GetArrayLength() > 0)
        {
            var totalReceived = itemsEl.GetArrayLength();
            var added = 0;
            var updatedOrDuplicate = 0;
            var skippedByRegion = 0;
            // Lưu CSDL TOÀN BỘ sản phẩm — không lọc theo bán/tháng hay giá khi crawl. Việc lọc (min bán
            // từ–đến, min giá) chỉ áp dụng khi hiển thị + xuất Excel ở phía form.
            foreach (var it in itemsEl.EnumerateArray())
            {
                var price = GetLong(it, "price");
                var sold  = GetInt(it, "sold");
                var location = GetStr(it, "location");
                if (!MatchesRegionFilter(location, _searchConfig?.RegionFilterText))
                {
                    skippedByRegion++;
                    continue;
                }

                var r = new ProductResult
                {
                    ItemId    = GetLong(it, "itemid"),
                    ShopId    = GetLong(it, "shopid"),
                    Name      = GetStr(it, "name"),
                    PriceVnd  = price,
                    MonthlySold = sold,
                    Rating    = GetDouble(it, "rating"),
                    Category  = GetStr(it, "category"),
                    ShopLocation = location,
                    ImageUrl  = GetStr(it, "image"),
                };
                r.ImageUrl = string.IsNullOrWhiteSpace(r.ImageUrl)
                    ? ""
                    : r.ImageUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        ? r.ImageUrl
                        : $"https://cf.shopee.vn/file/{r.ImageUrl}";
                if (AddResultIfNew(r))
                    added++;
                else
                    updatedOrDuplicate++;
            }
            ProgressChanged?.Invoke($"[pageData/{srcName}] received {totalReceived}, added {added}, updated/duplicate {updatedOrDuplicate}, skipped region {skippedByRegion}, total {_results.Count}.");
            if (IsFinalPageData(source))
                SearchCompleted?.Invoke();
            return;
        }

        // Fallback: only links found via DOM
        if (source.TryGetProperty("links", out var linksEl) && linksEl.GetArrayLength() > 0)
        {
            var count = linksEl.GetArrayLength();
            ProgressChanged?.Invoke($"[pageData/{srcName}] {count} link sản phẩm - chưa có data chi tiết.");
            var sample = linksEl.EnumerateArray().Take(5)
                .Select(l => l.GetString() ?? "")
                .Where(s => s.Length > 0);
            foreach (var link in sample)
                ProgressChanged?.Invoke("  " + link);
            if (IsFinalPageData(source))
                SearchCompleted?.Invoke();
            return;
        }

        ProgressChanged?.Invoke($"[pageData/{srcName}] Không lấy được sản phẩm. Source={srcName}");
        ErrorOccurred?.Invoke("Trang không chứa dữ liệu sản phẩm có thể đọc được.");
    }

    private bool AddResultIfNew(ProductResult result)
    {
        if (result.ItemId <= 0 || result.ShopId <= 0) return false;
        var existing = _results.FirstOrDefault(r => r.ItemId == result.ItemId && r.ShopId == result.ShopId);
        if (existing is not null)
        {
            var changed = false;
            if (!string.IsNullOrWhiteSpace(result.Name) && existing.Name != result.Name)
            {
                existing.Name = result.Name;
                changed = true;
            }
            if (result.PriceVnd > 0 && existing.PriceVnd != result.PriceVnd)
            {
                existing.PriceVnd = result.PriceVnd;
                changed = true;
            }
            if (result.MonthlySold > 0 && existing.MonthlySold != result.MonthlySold)
            {
                existing.MonthlySold = result.MonthlySold;
                changed = true;
            }
            if (result.Rating > 0 && Math.Abs(existing.Rating - result.Rating) > 0.001)
            {
                existing.Rating = result.Rating;
                changed = true;
            }
            if (!string.IsNullOrWhiteSpace(result.ShopLocation) && existing.ShopLocation != result.ShopLocation)
            {
                existing.ShopLocation = result.ShopLocation;
                changed = true;
            }
            // Giữ danh mục thấy LẦN ĐẦU: 1 sản phẩm có thể nằm trong nhiều danh mục (facet), nếu ghi đè
            // theo lần gặp sau thì ô "Danh mục" của các dòng cũ bị đổi loạn → nhìn như xen kẽ. Chỉ điền
            // khi chưa có.
            if (string.IsNullOrWhiteSpace(existing.Category) && !string.IsNullOrWhiteSpace(result.Category))
            {
                existing.Category = result.Category;
                changed = true;
            }
            if (!string.IsNullOrWhiteSpace(result.ImageUrl) && existing.ImageUrl != result.ImageUrl)
            {
                existing.ImageUrl = result.ImageUrl;
                changed = true;
            }

            if (changed)
            {
                ProductFound?.Invoke(existing);
                ProductPersisted?.Invoke(existing);
            }
            return false;
        }

        _results.Add(result);
        ProductFound?.Invoke(result);
        ProductPersisted?.Invoke(result);
        return true;
    }

    private static bool IsFinalPageData(JsonElement source) =>
        !source.TryGetProperty("isFinal", out var finalEl) || finalEl.ValueKind != JsonValueKind.False;

    private static bool MatchesRegionFilter(string location, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        if (string.IsNullOrWhiteSpace(location)) return false;

        var normalizedLocation = NormalizeRegionText(location);
        var normalizedFilter = NormalizeRegionText(filter);
        if (normalizedLocation.Contains(normalizedFilter, StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var alias in RegionAliases(normalizedFilter))
        {
            if (normalizedLocation.Contains(alias, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static IEnumerable<string> RegionAliases(string normalizedFilter)
    {
        if (normalizedFilter is "hcm" or "tp hcm" or "tphcm" or "tp ho chi minh" or "sai gon" or "saigon")
            yield return "ho chi minh";
        if (normalizedFilter is "hn" or "ha noi")
            yield return "ha noi";
    }

    private static string NormalizeRegionText(string value)
    {
        var formD = value.Trim().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(formD.Length);
        foreach (var ch in formD)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }

        return sb.ToString()
            .Normalize(System.Text.NormalizationForm.FormC)
            .Replace("d", "d")
            .Replace(".", " ")
            .Replace("-", " ")
            .Replace("_", " ")
            .Replace("  ", " ");
    }

    // --- helpers ---
    private static long GetLong(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.TryGetInt64(out var n) ? n : 0;

    private static int GetInt(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.TryGetInt32(out var n) ? n : 0;

    private static double GetDouble(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.TryGetDouble(out var d) ? d : 0;

    private static string GetStr(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) ? v.GetString() ?? "" : "";
}

