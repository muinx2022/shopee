using Microsoft.Playwright;

namespace UpdateProduct;

internal static class BigSellerCrawlHelper
{
    public const string CrawlUrl = "https://www.bigseller.com/web/crawl/index.htm";

    public static bool IsCrawlPage(IPage page, string? targetUrl = null) =>
        IsTargetCrawlUrl(page.Url ?? "", ResolveCrawlUrl(targetUrl));

    public static async Task<bool> IsCrawlPageReadyAsync(IPage page, int timeoutMs = 8000, string? targetUrl = null)
    {
        if (!IsCrawlPage(page, targetUrl))
            return false;

        const string readySelector =
            "#activeTab, " +
            ".tabs_module, " +
            "button:has-text('Import to Stores'), " +
            "a.action_btn[title='Import to Stores'], " +
            "tbody.ant-table-tbody tr, " +
            ".ant-empty, " +
            ".ant-table-placeholder";

        try
        {
            await page.WaitForSelectorAsync(readySelector, new() { State = WaitForSelectorState.Attached, Timeout = timeoutMs });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static async Task StopPageLoadingAsync(IPage page)
    {
        try { await page.EvaluateAsync("() => window.stop()"); } catch { }
    }

    public static async Task<bool> NavigateToCrawlUrlAsync(IPage page, string? targetUrl = null, Action<string>? log = null)
    {
        var crawlUrl = ResolveCrawlUrl(targetUrl);
        await StopPageLoadingAsync(page);
        try
        {
            await page.GotoAsync(crawlUrl, new() { WaitUntil = WaitUntilState.Commit, Timeout = 12000 });
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"Commit navigation lỗi, thử JS: {ex.Message}");
        }

        try
        {
            await page.EvaluateAsync("url => { window.location.href = url; }", crawlUrl);
            await page.WaitForURLAsync("**/web/crawl/index.htm**", new() { WaitUntil = WaitUntilState.Commit, Timeout = 12000 });
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"JS navigation lỗi: {ex.Message}");
        }

        try
        {
            await page.GotoAsync(crawlUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 12000 });
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"DOM navigation lỗi: {ex.Message}");
            return false;
        }
    }

    public static async Task<bool> GoToCrawlPageAsync(IPage page, bool forceReload = false, string? targetUrl = null, Action<string>? log = null)
    {
        if (IsCrawlPage(page, targetUrl) && !forceReload)
        {
            if (await IsCrawlPageReadyAsync(page, 3000, targetUrl))
                return true;
            log?.Invoke("Đang ở Crawl List nhưng nội dung chưa sẵn sàng, reload...");
        }

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            log?.Invoke("Chuyển về trang Crawl List...");
            try
            {
                if (!await NavigateToCrawlUrlAsync(page, targetUrl, log))
                    throw new TimeoutException("Không mở được Crawl List.");

                if (await IsCrawlPageReadyAsync(page, 10000, targetUrl))
                {
                    await Task.Delay(2000);
                    return true;
                }

                throw new TimeoutException("Crawl List mở nhưng bảng chưa sẵn sàng.");
            }
            catch (Exception ex)
            {
                if (IsCrawlPage(page, targetUrl) && await IsCrawlPageReadyAsync(page, 3000, targetUrl))
                {
                    log?.Invoke($"Crawl List đã mở, bỏ qua lỗi phụ: {ex.Message}");
                    await Task.Delay(2000);
                    return true;
                }

                log?.Invoke($"Chuyển Crawl List lỗi {attempt}/3: {ex.Message}");
                if (attempt < 3)
                {
                    await StopPageLoadingAsync(page);
                    await Task.Delay(3000);
                }
                else
                {
                    log?.Invoke($"Không mở được Crawl List. URL: {page.Url}");
                    return false;
                }
            }
        }

        return false;
    }

    public static async Task<bool> SelectClaimedTabAsync(IPage page, Action<string>? log = null) =>
        await SelectClaimedTabByTextAsync(page, log);

    public static async Task<bool> SelectClaimedTabByTextAsync(IPage page, Action<string>? log = null)
    {
        try
        {
            var result = await page.EvaluateAsync<string>(
                @"() => {
                    const normalize = v => (v || '')
                        .normalize('NFD')
                        .replace(/[̀-ͯ]/g, '')
                        .replace(/[đĐdd]/g, 'd')
                        .replace(/\s+/g, ' ')
                        .trim()
                        .toLowerCase();

                    const selectors = [
                        '#activeTab li', '#activeTab a', '#activeTab > div', '#activeTab > span',
                        '.tabs_module li', '.tabs_module a', '.tabs_module > div', '.tabs_module > span',
                        '.ant-tabs-tab', '[class*=""tab-nav""] li', '[class*=""tab-nav""] a',
                        '[class*=""tab-list""] li', '[class*=""tab-list""] a', '[class*=""tab-item""]',
                    ];
                    const seen = new Set();
                    const items = selectors
                        .flatMap(s => Array.from(document.querySelectorAll(s)))
                        .filter(el => { if (seen.has(el)) return false; seen.add(el); return true; });

                    if (items.length === 0) return 'no-items';

                    let el = items.find(item => normalize(item.textContent).includes('da nhan'));
                    if (!el && items.length >= 3) el = items[2];
                    if (!el) return 'not-found:' + items.length;

                    const isActive = el.classList.contains('active') ||
                                     el.classList.contains('ant-tabs-tab-active') ||
                                     el.getAttribute('aria-selected') === 'true';
                    if (isActive) return 'already-active';

                    (el.querySelector('a, button') || el).click();
                    return 'clicked';
                }");

            switch (result)
            {
                case "clicked":
                log?.Invoke("Đã chọn tab Đã nhận.");
                    await Task.Delay(1500);
                    await WaitForCrawlListContentAsync(page, 8000);
                    return true;
                case "already-active":
                log?.Invoke("Tab Đã nhận đã được chọn sẵn.");
                    return true;
                case "no-items":
                log?.Invoke("Không tìm thấy container tab trên trang.");
                    return false;
                default:
                log?.Invoke($"Không tìm thấy tab Đã nhận ({result}).");
                    return false;
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"Chọn tab Đã nhận lỗi: {ex.Message}");
            return false;
        }
    }

    public static async Task WaitForCrawlListContentAsync(IPage page, int timeoutMs = 8000)
    {
        const string selector =
            "td[colid='col_10'] a.action_btn[title='Import to Stores'], " +
            ".vxe-body--column.col_10 a.action_btn[title='Import to Stores'], " +
            "a.action_btn[title='Import to Stores'], " +
            "a.action_btn:has-text('Import to Stores'), " +
            "button:has-text('Import to Stores'), " +
            "tbody.ant-table-tbody tr, " +
            ".vxe-table--body tr, " +
            ".ant-empty, " +
            ".ant-table-placeholder";

        try { await page.WaitForSelectorAsync(selector, new() { State = WaitForSelectorState.Attached, Timeout = timeoutMs }); }
        catch { }
    }

    public static async Task<bool> ClickNextCrawlPageAsync(IPage page, Action<string>? log = null)
    {
        try
        {
            var clicked = await page.EvaluateAsync<bool>(
                @"() => {
                    const next = document.querySelector('.pagination .next_item:not(.disabled), li.next_item:not(.disabled)');
                    if (!next) return false;
                    const action = next.querySelector('a.paging_action, a, button') || next;
                    action.click();
                    return true;
                }");

            if (!clicked)
            {
            log?.Invoke("Không còn trang tiếp theo.");
                return false;
            }

            log?.Invoke("Chuyển sang trang tiếp theo...");
            await Task.Delay(1500);
            await WaitForCrawlListContentAsync(page, 10000);
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"Chuyển trang tiếp theo lỗi: {ex.Message}");
            return false;
        }
    }

    public static async Task DismissPostImportDialogsAsync(IPage page, Action<string>? log = null)
    {
        try
        {
            var draftLink = page.Locator("a.has_underline:has-text('Hộp nháp')").First;
            if (await draftLink.IsVisibleAsync(new() { Timeout = 3000 }))
            {
                log?.Invoke("Bỏ qua link Hộp nháp, dùng hộp thoại...");
                await page.Keyboard.PressAsync("Escape");
                await Task.Delay(500);
            }
        }
        catch { }

        for (var i = 0; i < 3; i++)
        {
            var closeBtn = page.Locator(".ant-modal-wrap:visible .ant-modal-close").Last;
            if (await closeBtn.CountAsync() == 0)
                break;

            try
            {
                if (await closeBtn.IsVisibleAsync(new() { Timeout = 1000 }))
                {
                    await closeBtn.ClickAsync(new() { Timeout = 3000 });
                    await Task.Delay(400);
                }
                else break;
            }
            catch
            {
                break;
            }
        }

        try { await page.Keyboard.PressAsync("Escape"); } catch { }
        await Task.Delay(500);
    }

    public static async Task<bool> DeleteBrokenRowAsync(IPage page, ILocator rowElem, Action<string>? log = null)
    {
        try
        {
            var deleteBtn = rowElem.Locator("a[title='Xóa']");
            if (await deleteBtn.CountAsync() == 0)
            {
                log?.Invoke("Không tìm thấy nút Xóa");
                return false;
            }

            await deleteBtn.ClickAsync();
            var confirmBtn = page.Locator(".ant-modal-confirm-btns button.ant-btn-primary");
            await confirmBtn.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 3000 });
            await confirmBtn.ClickAsync();
            await Task.Delay(2000);
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"Lỗi khi xóa dòng: {ex.Message}");
            return false;
        }
    }

    public static string ResolveCrawlUrl(string? targetUrl) =>
        string.IsNullOrWhiteSpace(targetUrl) ? CrawlUrl : targetUrl.Trim();

    private static bool IsTargetCrawlUrl(string currentUrl, string targetUrl)
    {
        if (string.IsNullOrWhiteSpace(currentUrl))
            return false;

        if (!Uri.TryCreate(currentUrl, UriKind.Absolute, out var current))
            return currentUrl.Contains("bigseller.com/web/crawl/index.htm", StringComparison.OrdinalIgnoreCase);

        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var target))
            return currentUrl.Contains(targetUrl, StringComparison.OrdinalIgnoreCase);

        return current.Host.Contains("bigseller.com", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(current.AbsolutePath, target.AbsolutePath, StringComparison.OrdinalIgnoreCase);
    }
}
