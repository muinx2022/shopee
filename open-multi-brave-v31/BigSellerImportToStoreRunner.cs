using System.Diagnostics;
using System.Text.Json;
using Microsoft.Playwright;

namespace OpenMultiBraveLauncherV3;

internal sealed class BigSellerImportToStoreRunner : IAsyncDisposable
{
    private const string ImportButtonSelector =
        "td[colid='col_10'] a.action_btn[title='Import to Stores']:visible, " +
        ".vxe-body--column.col_10 a.action_btn[title='Import to Stores']:visible, " +
        "a.action_btn[title='Import to Stores']:visible, " +
        "a.action_btn:has-text('Import to Stores'):visible, " +
        "button:has-text('Import to Stores'):visible";

    private readonly BigSellerWorkflowSettings _settings;
    private readonly Action<string> _log;
    private readonly WorkflowPauseToken? _pauseToken;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private Process? _braveProcess;

    public BigSellerImportToStoreRunner(
        BigSellerWorkflowSettings settings,
        Action<string> log,
        WorkflowPauseToken? pauseToken = null)
    {
        _settings = settings;
        _log = log;
        _pauseToken = pauseToken;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_settings.BravePath))
            throw new FileNotFoundException($"Khong tim thay Brave: {_settings.BravePath}");

        StartBraveForBigSeller();
        _log($"Da goi Brave PID={_braveProcess?.Id.ToString() ?? "unknown"}, cho CDP port {_settings.DebugPort}...");
        if (!await WaitForCdpReadyAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException(
                $"CDP port {_settings.DebugPort} khong san sang. Hay dong Brave BigSeller profile cu roi chay lai.");

        // Profile BigSeller là persistent — nếu còn phiên sống thì phiên trong profile
        // luôn "tươi" hơn file tĩnh; ghi đè bằng file cũ sẽ làm văng phiên -> chỉ import khi mất phiên.
        // Có token chưa đủ (token có thể đã bị server thu hồi) — probe trang app để chắc chắn.
        var crawlUrl = BigSellerCrawlHelper.ResolveCrawlUrl(_settings.CrawlUrl);
        var hasLiveSession = false;
        try
        {
            hasLiveSession = BigSellerCookieImporter.HasAuthCookie(
                await BigSellerCookieImporter.GetBigSellerCookiesAsync(_settings.DebugPort).ConfigureAwait(false));
        }
        catch
        {
            // khong doc duoc cookie -> coi nhu chua dang nhap, import tu file nhu cu
        }

        if (hasLiveSession &&
            await BigSellerCookieImporter.ProbeLoggedInAsync(
                _settings.DebugPort, crawlUrl, _log, cancellationToken).ConfigureAwait(false) == false)
        {
            hasLiveSession = false;
            _log("Token BigSeller trong profile da bi server thu hoi — nap lai cookie tu file account.");
        }

        if (hasLiveSession)
        {
            _log("Profile da dang nhap BigSeller — giu phien hien tai, khong ghi de cookie tu file.");
            await BigSellerCookieImporter.TryExportProfileCookiesToFileAsync(
                _settings.DebugPort, _settings.BigSellerCookieFile, _log).ConfigureAwait(false);
        }
        else
        {
            _log("CDP da san sang, dang import cookie BigSeller...");
            await BigSellerCookieImporter.ImportFromFileAsync(
                _settings.DebugPort,
                _settings.BigSellerCookieFile ?? "",
                _log,
                reloadBigSellerTabs: false,
                navigateUrl: crawlUrl,
                cancellationToken).ConfigureAwait(false);
            _log("Da xu ly cookie BigSeller.");

            if (await BigSellerCookieImporter.ProbeLoggedInAsync(
                    _settings.DebugPort, crawlUrl, _log, cancellationToken).ConfigureAwait(false) == false)
                _log("Cookie tu file account cung da het han — mo tab Account, bam Open BigSeller, login lai roi bam Save & close.");
        }

        _playwright = await Playwright.CreateAsync();
        _log($"Ket noi CDP port {_settings.DebugPort}...");

        for (var attempt = 0; attempt < 5; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _browser = await _playwright.Chromium.ConnectOverCDPAsync(
                    $"http://127.0.0.1:{_settings.DebugPort}",
                    new() { Timeout = 30000 });
                break;
            }
            catch
            {
                await DelayAsync(3000, cancellationToken);
            }
        }

        if (_browser is null)
            throw new InvalidOperationException("Không kết nối được Brave qua CDP. Kiểm tra BigSeller profile đã đăng nhập chưa.");

        var context = _browser.Contexts.FirstOrDefault()
            ?? throw new InvalidOperationException("Brave chưa có browser context.");

        var page = await FindBigSellerPageAsync(context, cancellationToken)
            ?? throw new InvalidOperationException("Không tìm thấy tab BigSeller.");

        await page.BringToFrontAsync();
        _log($"Crawl URL: {crawlUrl}");
        if (!await BigSellerCrawlHelper.GoToCrawlPageAsync(page, forceReload: true, targetUrl: crawlUrl, log: _log))
            throw new InvalidOperationException("Không mở được Crawl List.");

        await SelectSourceTabIfNeededAsync(page, cancellationToken);

        _log(new string('=', 50));
        _log("BẮT ĐẦU IMPORT TO STORE");
        _log(new string('=', 50));

        var currentIndex = 0;
        var loopCount = 0;
        var importCount = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            await WaitIfNotPausedAsync(cancellationToken).ConfigureAwait(false);
            loopCount++;
            _log("");
            _log($"Vòng #{loopCount} | SP thứ {currentIndex + 1} trên Crawl List");

            if (!await BigSellerCrawlHelper.GoToCrawlPageAsync(page, targetUrl: crawlUrl, log: _log))
            {
                await DelayAsync(5000, cancellationToken);
                continue;
            }

            await SelectSourceTabIfNeededAsync(page, cancellationToken);

            var buttons = page.Locator(ImportButtonSelector);
            int count;
            try
            {
                count = _settings.ImportFromClaimedTab
                    ? await GetClaimedImportRowCountAsync(page)
                    : await buttons.CountAsync();
            }
            catch (Exception ex) when (IsTransientNavigationError(ex))
            {
                _log($"Trang BigSeller đang reload/ngôn ngữ thay đổi, thử lại: {ex.Message}");
                await DelayAsync(3000, cancellationToken);
                await BigSellerCrawlHelper.GoToCrawlPageAsync(page, targetUrl: crawlUrl, log: _log);
                await SelectSourceTabIfNeededAsync(page, cancellationToken);
                continue;
            }
            _log($"Crawl List có {count} sản phẩm.");

            if (count == 0)
            {
                if (_settings.ImportFromClaimedTab &&
                    await BigSellerCrawlHelper.ClickNextCrawlPageAsync(page, _log))
                {
                    currentIndex = 0;
                    await DelayAsync(2500, cancellationToken);
                    continue;
                }

                _log($"Hết sản phẩm. Đợi {_settings.ListingReloadSeconds}s...");
                await DelayAsync(TimeSpan.FromSeconds(Math.Max(3, _settings.ListingReloadSeconds)), cancellationToken);
                await BigSellerCrawlHelper.GoToCrawlPageAsync(page, forceReload: true, targetUrl: crawlUrl, log: _log);
                await SelectSourceTabIfNeededAsync(page, cancellationToken);
                await DelayAsync(5000, cancellationToken);
                currentIndex = 0;
                continue;
            }

            if (currentIndex >= count)
            {
                if (_settings.ImportFromClaimedTab &&
                    await BigSellerCrawlHelper.ClickNextCrawlPageAsync(page, _log))
                {
                    currentIndex = 0;
                    await DelayAsync(2500, cancellationToken);
                    continue;
                }

                currentIndex = 0;
            }

            if (_settings.ImportFromClaimedTab)
            {
                var claimedProductName = await ImportClaimedRowWithRetryAsync(page, currentIndex, crawlUrl, cancellationToken);
                importCount++;
                _log($"Đã Import to Stores #{importCount}.");

                await DelayAsync(2000, cancellationToken);
                await BigSellerCrawlHelper.DismissPostImportDialogsAsync(page, _log);

                if (await RemoveClaimedImportRowAsync(page, currentIndex))
                    _log("Đã xóa dòng vừa import khỏi bảng hiện tại.");
                else
                    _log("Không xóa được dòng vừa import khỏi bảng hiện tại.");

                _log("Import dòng tiếp theo trong tab Đã nhận...");
                await DelayAsync(1500, cancellationToken);
                continue;
            }

            var targetBtn = buttons.Nth(currentIndex);
            var rowElem = targetBtn.Locator("xpath=ancestor::tr");
            var productLink = rowElem.Locator("a.list_tit_link, .vxe-cell a[href]:not(.action_btn)").First;

            if (false && !_settings.ImportFromClaimedTab && await productLink.CountAsync() == 0)
            {
                _log("Dòng lỗi HTML -> xóa");
                if (await BigSellerCrawlHelper.DeleteBrokenRowAsync(page, rowElem, _log))
                {
                    await DelayAsync(1000, cancellationToken);
                    continue;
                }

                await rowElem.EvaluateAsync("el => el.remove()");
                continue;
            }

            var productName = await productLink.CountAsync() > 0
                ? (await productLink.TextContentAsync() ?? "").Trim()
                : (await rowElem.TextContentAsync() ?? "").Trim();
            var shortName = productName.Length > 50 ? productName[..50] + "..." : productName;
            _log($"[SP #{currentIndex + 1}] {shortName}");
            _log("Click Import to Stores...");

            try
            {
                await targetBtn.ClickAsync(new() { Timeout = 15000 });
            }
            catch
            {
                await page.Locator(ImportButtonSelector).Nth(currentIndex).ClickAsync();
            }

            var modal = page.Locator(".ant-modal-content:visible").Last;
            await modal.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 20000 });

            await SelectImportShopAndConfirmAsync(modal, _settings.ShopName);
            importCount++;
            _log($"Đã Import to Stores (#{importCount}) - không vào Hộp nháp.");

            await DelayAsync(2000, cancellationToken);
            await BigSellerCrawlHelper.DismissPostImportDialogsAsync(page, _log);

            if (_settings.ImportFromClaimedTab)
            {
                _log("Import dòng tiếp theo trong tab Đã nhận...");
                currentIndex++;
                await BigSellerCrawlHelper.GoToCrawlPageAsync(page, targetUrl: crawlUrl, log: _log);
                await SelectSourceTabIfNeededAsync(page, cancellationToken);
                await DelayAsync(1500, cancellationToken);
                continue;
            }

            _log("Quay về Crawl List, import SP tiếp theo...");
            await BigSellerCrawlHelper.GoToCrawlPageAsync(page, forceReload: true, targetUrl: crawlUrl, log: _log);
            await SelectSourceTabIfNeededAsync(page, cancellationToken);
            currentIndex = 0;
            await DelayAsync(3000, cancellationToken);
        }
    }

    private async Task SelectSourceTabIfNeededAsync(IPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_settings.ImportFromClaimedTab)
            return;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ok = await BigSellerCrawlHelper.SelectClaimedTabByTextAsync(page, _log);
            if (ok) return;

            _log($"Tab Đã nhận chưa chọn được (lần {attempt}/3), chờ trang load...");
            await DelayAsync(2000, cancellationToken);

            if (attempt == 2)
            {
                _log("Reload trang để thử lại chọn tab...");
                await BigSellerCrawlHelper.GoToCrawlPageAsync(page, forceReload: true,
                    targetUrl: BigSellerCrawlHelper.ResolveCrawlUrl(_settings.CrawlUrl), log: _log);
                await DelayAsync(2000, cancellationToken);
            }
        }

        _log("Cảnh báo: Không chọn được tab Đã nhận, tiếp tục vòng lặp kế tiếp...");
    }

    private async Task<string> ImportClaimedRowWithRetryAsync(
        IPage page,
        int currentIndex,
        string crawlUrl,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (attempt > 1)
                {
                    _log($"Thử lại import dòng #{currentIndex + 1} lần {attempt}/3...");
                    await BigSellerCrawlHelper.DismissPostImportDialogsAsync(page, _log);
                    await BigSellerCrawlHelper.GoToCrawlPageAsync(page, targetUrl: crawlUrl, log: _log);
                    await SelectSourceTabIfNeededAsync(page, cancellationToken);
                    await DelayAsync(1000, cancellationToken);
                }

                var claimedProductName = await ClickClaimedImportRowAsync(page, currentIndex);
                if (string.IsNullOrWhiteSpace(claimedProductName))
                    claimedProductName = $"Dòng {currentIndex + 1}";

                var claimedShortName = claimedProductName.Length > 50 ? claimedProductName[..50] + "..." : claimedProductName;
                _log($"[SP #{currentIndex + 1}] {claimedShortName}");

                var claimedModal = page.Locator(".ant-modal-content:visible").Last;
                await claimedModal.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 30000 });
                await SelectImportShopAndConfirmAsync(claimedModal, _settings.ShopName);
                return claimedProductName;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _log($"Import dòng #{currentIndex + 1} lỗi lần {attempt}/3: {ex.Message}");
                if (attempt < 3)
                {
                    if (IsTransientNavigationError(ex))
                    {
                        await DelayAsync(3000, cancellationToken);
                        await BigSellerCrawlHelper.GoToCrawlPageAsync(page, targetUrl: crawlUrl, log: _log);
                        await SelectSourceTabIfNeededAsync(page, cancellationToken);
                    }
                    else
                    {
                        await DelayAsync(1500, cancellationToken);
                    }
                }
            }
        }

        throw lastError ?? new InvalidOperationException($"Không import được dòng #{currentIndex + 1} sau 3 lần thử.");
    }

    private static bool IsTransientNavigationError(Exception ex)
    {
        var message = ex.Message ?? "";
        return message.Contains("Execution context was destroyed", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("most likely because of a navigation", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Cannot find context with specified id", StringComparison.OrdinalIgnoreCase);
    }

    private async Task SelectImportShopAndConfirmAsync(ILocator modal, string shopName)
    {
        _log($"Chon shop import: {shopName}");
        var selectedLabel = await modal.EvaluateAsync<string>(
            @"(root, targetShop) => {
                const normalize = value => (value || '')
                    .normalize('NFD')
                    .replace(/[\u0300-\u036f]/g, '')
                    .replace(/\u0111/g, 'd')
                    .replace(/\u0110/g, 'd')
                    .replace(/\s+/g, ' ')
                    .trim()
                    .toLowerCase();
                const compact = value => normalize(value).replace(/[^a-z0-9]/g, '');
                const target = normalize(targetShop);
                const targetCompact = compact(targetShop);
                const labelText = label => (label.textContent || '').replace(/\s+/g, ' ').trim();
                const isVisible = el => {
                    const rect = el.getBoundingClientRect();
                    return rect.width > 0 && rect.height > 0;
                };
                const labels = Array.from(root.querySelectorAll(
                    '.cont_btm.btmOut label.ant-checkbox-wrapper, .btmOut label.ant-checkbox-wrapper, label.ant-checkbox-wrapper'))
                    .filter(label => isVisible(label) && label.querySelector('input[type=checkbox]'));
                const stores = labels.filter(label => !normalize(labelText(label)).includes('select all'));
                const targetLabel = stores.find(label => {
                    const text = labelText(label);
                    const normalized = normalize(text);
                    const compacted = compact(text);
                    return normalized === target ||
                        normalized.includes(target) ||
                        compacted === targetCompact ||
                        compacted.includes(targetCompact);
                });
                if (!targetLabel) {
                    const available = stores.map(labelText).filter(Boolean).join(' | ');
                    throw new Error(`Khong tim thay shop import: ${targetShop}. Available: ${available}`);
                }
                const isChecked = label => {
                    const input = label.querySelector('input[type=checkbox]');
                    const box = label.querySelector('.ant-checkbox');
                    return !!(input?.checked || box?.classList.contains('ant-checkbox-checked') || label.classList.contains('ant-checkbox-wrapper-checked'));
                };
                const clickLabel = label => {
                    const target = label.querySelector('.ant-checkbox-inner') ||
                        label.querySelector('.ant-checkbox') ||
                        label.querySelector('input[type=checkbox]') ||
                        label;
                    target.click();
                };
                for (const label of stores) {
                    if (label === targetLabel) continue;
                    if (isChecked(label)) clickLabel(label);
                }
                if (!isChecked(targetLabel)) clickLabel(targetLabel);
                return labelText(targetLabel) || targetShop;
            }",
            shopName);

        await DelayAsync(500, CancellationToken.None);
        var isChecked = await IsImportShopCheckedAsync(modal, shopName);
        if (!isChecked)
            throw new InvalidOperationException($"Khong chon duoc shop import: {shopName}");

        _log($"Da chon shop import: {selectedLabel}");
        var importButton = modal.Locator("button.ant-btn-primary:has-text('Import to Stores'), button:has-text('Import to Stores')").First;
        await importButton.ClickAsync(new() { Force = true, Timeout = 10000 });
    }
    private static Task<bool> IsImportShopCheckedAsync(ILocator modal, string shopName) =>
        modal.EvaluateAsync<bool>(
            @"(root, targetShop) => {
                const normalize = value => (value || '')
                    .normalize('NFD')
                    .replace(/[\u0300-\u036f]/g, '')
                    .replace(/\u0111/g, 'd')
                    .replace(/\u0110/g, 'd')
                    .replace(/\s+/g, ' ')
                    .trim()
                    .toLowerCase();
                const compact = value => normalize(value).replace(/[^a-z0-9]/g, '');
                const target = normalize(targetShop);
                const targetCompact = compact(targetShop);
                const labelText = label => (label.textContent || '').replace(/\s+/g, ' ').trim();
                const labels = Array.from(root.querySelectorAll(
                    '.cont_btm.btmOut label.ant-checkbox-wrapper, .btmOut label.ant-checkbox-wrapper, label.ant-checkbox-wrapper'))
                    .filter(label => label.querySelector('input[type=checkbox]'));
                const label = labels.find(label => {
                    const text = labelText(label);
                    const normalized = normalize(text);
                    const compacted = compact(text);
                    return !normalized.includes('select all') &&
                        (normalized === target || normalized.includes(target) || compacted === targetCompact || compacted.includes(targetCompact));
                });
                if (!label) return false;
                const input = label.querySelector('input[type=checkbox]');
                const box = label.querySelector('.ant-checkbox');
                return !!(input?.checked || box?.classList.contains('ant-checkbox-checked') || label.classList.contains('ant-checkbox-wrapper-checked'));
            }",
            shopName);
    private static Task<int> GetClaimedImportRowCountAsync(IPage page) =>
        page.EvaluateAsync<int>(
            @"() => Array.from(document.querySelectorAll('tr.vxe-body--row'))
                .filter(row => {
                    const rect = row.getBoundingClientRect();
                    return rect.width > 0 &&
                        rect.height > 0 &&
                        row.querySelector(""td[colid='col_10'] a.action_btn[title='Import to Stores']"");
                }).length");

    private static Task<string> ClickClaimedImportRowAsync(IPage page, int rowIndex) =>
        page.EvaluateAsync<string>(
            @"index => {
                const rows = Array.from(document.querySelectorAll('tr.vxe-body--row'))
                    .filter(row => {
                        const rect = row.getBoundingClientRect();
                        return rect.width > 0 &&
                            rect.height > 0 &&
                            row.querySelector(""td[colid='col_10'] a.action_btn[title='Import to Stores']"");
                    });
                const row = rows[index];
                if (!row) throw new Error(`Khong tim thay dong import index ${index}`);

                const name = (
                    row.querySelector(""td[colid='col_3'] .list_tit_link"")?.textContent ||
                    row.querySelector(""td[colid='col_3']"")?.textContent ||
                    row.textContent ||
                    ''
                ).replace(/\s+/g, ' ').trim();

                const button = row.querySelector(""td[colid='col_10'] a.action_btn[title='Import to Stores']"");
                if (!button) throw new Error('Khong tim thay nut Import to Stores trong dong');
                button.click();
                return name;
            }",
            rowIndex);

    private static Task<bool> RemoveClaimedImportRowAsync(IPage page, int rowIndex) =>
        page.EvaluateAsync<bool>(
            @"index => {
                const rows = Array.from(document.querySelectorAll('tr.vxe-body--row'))
                    .filter(row => {
                        const rect = row.getBoundingClientRect();
                        return rect.width > 0 &&
                            rect.height > 0 &&
                            row.querySelector(""td[colid='col_10'] a.action_btn[title='Import to Stores']"");
                    });
                const row = rows[index];
                if (!row) return false;
                row.remove();
                return true;
            }",
            rowIndex);

    private void StartBraveForBigSeller()
    {
        Directory.CreateDirectory(_settings.ProfileDir);
        ClearSessionTabs(_settings.ProfileDir);

        var args = string.Join(" ", [
            $"--remote-debugging-port={_settings.DebugPort}",
            $"--user-data-dir=\"{_settings.ProfileDir}\"",
            "--no-first-run",
            "--no-default-browser-check",
            "--no-session-restore",
            "--restore-last-session=false",
            "--disable-session-crashed-bubble",
            "--start-maximized",
            "--window-size=1920,1080",
            "--disable-gpu",
            "--disable-dev-shm-usage",
            "--disable-software-rasterizer",
            $"\"{BigSellerCrawlHelper.ResolveCrawlUrl(_settings.CrawlUrl)}\"",
        ]);

        _log("Mở Brave BigSeller profile...");
        _braveProcess = Process.Start(new ProcessStartInfo
        {
            FileName = _settings.BravePath,
            Arguments = args,
            UseShellExecute = false,
        });
    }

    private async Task<bool> WaitForCdpReadyAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 30; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var response = await AppServices.DirectHttp.GetAsync(
                    $"http://127.0.0.1:{_settings.DebugPort}/json/version",
                    cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    return true;
            }
            catch
            {
                // Retry until Brave exposes the debugging endpoint.
            }

            if (attempt is 1 or 10 or 20)
                _log($"Dang cho CDP port {_settings.DebugPort}... ({attempt}/30)");
            await DelayAsync(500, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static void ClearSessionTabs(string profileDir)
    {
        var profilePath = new DirectoryInfo(profileDir);
        if (!profilePath.Exists)
            return;

        var patterns = new[] { "Current Session", "Current Tabs", "Last Session", "Last Tabs" };
        var dirs = new List<DirectoryInfo> { profilePath };
        dirs.AddRange(profilePath.GetDirectories("Profile *"));
        var defaultDir = Path.Combine(profilePath.FullName, "Default");
        if (Directory.Exists(defaultDir))
            dirs.Add(new DirectoryInfo(defaultDir));

        foreach (var dir in dirs)
        {
            foreach (var pattern in patterns)
            {
                foreach (var file in dir.GetFiles(pattern))
                {
                    try { file.Delete(); } catch { }
                }

                var sessions = Path.Combine(dir.FullName, "Sessions");
                if (!Directory.Exists(sessions))
                    continue;

                foreach (var file in Directory.GetFiles(sessions, pattern))
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }
    }

    private Task<IPage?> FindBigSellerPageAsync(IBrowserContext context, CancellationToken ct)
    {
        foreach (var p in context.Pages)
        {
            ct.ThrowIfCancellationRequested();
            if (BigSellerCrawlHelper.IsCrawlPage(p, _settings.CrawlUrl))
                return Task.FromResult<IPage?>(p);
        }

        foreach (var p in context.Pages)
        {
            if ((p.Url ?? "").Contains("bigseller.com", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult<IPage?>(p);
        }

        return Task.FromResult<IPage?>(context.Pages.FirstOrDefault());
    }

    private Task WaitIfNotPausedAsync(CancellationToken cancellationToken) =>
        _pauseToken?.WaitWhileRunningAsync(cancellationToken) ?? Task.CompletedTask;

    private Task DelayAsync(int milliseconds, CancellationToken cancellationToken) =>
        _pauseToken?.DelayAsync(milliseconds, cancellationToken) ?? Task.Delay(milliseconds, cancellationToken);

    private Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        _pauseToken?.DelayAsync(delay, cancellationToken) ?? Task.Delay(delay, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        // Brave còn sống -> tranh thủ lưu token BigSeller mới nhất về file trước khi kill,
        // để lần chạy sau (kể cả khi profile bị xóa) vẫn đăng nhập được.
        if (_braveProcess is not null)
        {
            try
            {
                if (!_braveProcess.HasExited)
                    await BigSellerCookieImporter.TryExportProfileCookiesToFileAsync(
                        _settings.DebugPort, _settings.BigSellerCookieFile, _log,
                        verifySessionAlive: true).ConfigureAwait(false);
            }
            catch { }
        }

        if (_browser is not null)
        {
            try { await _browser.DisposeAsync(); } catch { }
            _browser = null;
        }

        _playwright?.Dispose();
        _playwright = null;

        if (_braveProcess is not null)
        {
            try
            {
                if (!_braveProcess.HasExited)
                    _braveProcess.Kill(entireProcessTree: true);
            }
            catch { }

            try { _braveProcess.Dispose(); } catch { }
            _braveProcess = null;
        }
    }
}
