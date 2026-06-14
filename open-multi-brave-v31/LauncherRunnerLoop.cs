using System.Text;
using System.Text.Json;

namespace OpenMultiBraveLauncherV3;

internal static class LauncherRunnerLoop
{
    private const int MinRestMs = 120_000;
    private const int MaxRestMs = 300_000;
    private const int VideoMaxDurationS = 60;
    private const int MaxDownloadRetries = 1;
    private static readonly Random RestRandom = new();

    public static async Task RunAsync(
        int cdpPort,
        DirectoryInfo profileRoot,
        InstanceConfig config,
        Action<string> log,
        Action onProgress,
        Func<Task>? ensureBigSellerCookies,
        bool preferSuggestedResume,
        CancellationToken cancellationToken)
    {
        var sheet = config.DataSheet?.Trim()
            ?? throw new InvalidOperationException("Thiếu sheet.");

        var runRow = config.GetEffectiveRunRow() ?? config.StartRow;
        if (runRow is not > 0)
            throw new InvalidOperationException("Thiếu dòng tiếp theo / từ dòng.");

        if (!config.TryValidateRunRow(runRow.Value, out var rangeError))
            throw new InvalidOperationException(rangeError ?? "Dòng chạy ngoài phạm vi.");

        var startRow = runRow.Value;

        if (!await ApiServerHelper.EnsureReachableAsync(log, cancellationToken: cancellationToken))
            throw new ApiNotRunningException(ApiServerHelper.ConnectionRefusedHelp);

        var endRow = config.EndRow;
        if (endRow is null || endRow < startRow)
            endRow = await ExtensionRunnerAutomation.ResolveEndRowAsync(sheet, startRow, cancellationToken);

        config.EndRow = endRow;

        log($"Đang tải dữ liệu: {sheet} dòng {startRow}–{endRow}…");
        var fetch = await ExtensionRunnerAutomation.FetchSheetLinksAsync(
            sheet, startRow, endRow.Value, cancellationToken);
        var items = fetch.Items;

        if (items.Count == 0)
            throw new InvalidOperationException("Không có link hợp lệ trong khoảng dòng đã chọn.");

        if (fetch.SkippedMissingProductName > 0)
            log($"Bỏ qua {fetch.SkippedMissingProductName} dòng vì cột F (tên SP) trống.");
        if (fetch.SkippedMissingLink > 0)
            log($"Bỏ qua {fetch.SkippedMissingLink} dòng vì cột A không có link hợp lệ.");

        await ExtensionRunnerAutomation.EnsureRunnerExtensionReadyAsync(
            cdpPort, profileRoot, log, cancellationToken).ConfigureAwait(false);

        await ExtensionRunnerAutomation.TryApplyFormConfigAsync(
            cdpPort, profileRoot, sheet, config.StartRow, config.EndRow ?? endRow, cancellationToken).ConfigureAwait(false);

        config.RunLog ??= [];
        config.RunnerRunning = true;
        config.RunnerPhase = "starting";
        config.LastRunnerMessage = "Đang chuẩn bị…";

        int? tabId = null;
        var totalLinks = items.Count;

        await PushDisplayStateAsync(
            cdpPort, profileRoot, config, sheet, startRow, endRow.Value,
            0, totalLinks, null, cancellationToken).ConfigureAwait(false);

        try
        {
            for (var index = 0; index < items.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var item = items[index];
                var rowNumber = item.RowNumber;
                var rowData = item.RowData;
                var link = item.Link;
                var sku = ExtractSkuFromRow(rowData, rowNumber);

                if (ensureBigSellerCookies is not null)
                    await ensureBigSellerCookies().ConfigureAwait(false);

                config.CurrentRow = rowNumber;
                config.RunnerPhase = "opening";
                config.LastRunnerMessage = $"Đang mở link {index + 1}/{totalLinks} — dòng {rowNumber} (SKU: {sku}).";

                await PushDisplayStateAsync(
                    cdpPort, profileRoot, config, sheet, startRow, endRow.Value,
                    index + 1, totalLinks, tabId, cancellationToken).ConfigureAwait(false);
                onProgress();

                var statusText = $"Đang mở link {index + 1}/{totalLinks} — dòng {rowNumber}.";
                var step = await ExtensionRunnerAutomation.ExecuteScrapeStepAsync(
                    cdpPort,
                    profileRoot,
                    link,
                    rowNumber,
                    statusText,
                    config.DisplayName,
                    sku,
                    tabId,
                    cancellationToken).ConfigureAwait(false);

                if (!step.ScrapeOk && step.TabId is null && tabId is not null)
                {
                    tabId = null;
                    step = await ExtensionRunnerAutomation.ExecuteScrapeStepAsync(
                        cdpPort,
                        profileRoot,
                        link,
                        rowNumber,
                        statusText,
                        config.DisplayName,
                        sku,
                        null,
                        cancellationToken).ConfigureAwait(false);
                }

                if (step.Aborted)
                    throw new OperationCanceledException();

                if (step.ProxyError)
                {
                    config.RunnerPhase = "paused";
                    config.LastRunnerMessage = step.Message ?? $"Lỗi proxy dòng {rowNumber}.";
                    await PushDisplayStateAsync(
                        cdpPort, profileRoot, config, sheet, startRow, endRow.Value,
                        index + 1, totalLinks, step.TabId, cancellationToken).ConfigureAwait(false);
                    onProgress();
                    log(config.LastRunnerMessage);
                    return;
                }

                if (step.Captcha && !step.ScrapeOk)
                {
                    config.RunnerPhase = "paused";
                    config.LastRunnerMessage =
                        step.Message ?? $"Dừng vì captcha - {config.DisplayName}, dòng {rowNumber} (SKU: {sku}).";
                    await PushDisplayStateAsync(
                        cdpPort, profileRoot, config, sheet, startRow, endRow.Value,
                        index + 1, totalLinks, step.TabId, cancellationToken).ConfigureAwait(false);
                    onProgress();
                    log(config.LastRunnerMessage);
                    return;
                }

                tabId = step.TabId;

                var scrapeOk = step.ScrapeOk;
                if (scrapeOk)
                {
                    if (step.Captcha)
                        log($"Đã giải captcha và click scrape - dòng {rowNumber}.");
                    log($"�� click scrape � d�ng {rowNumber}.");
                    await TryShowOverlayAsync(
                        cdpPort, profileRoot, tabId,
                        $"�� click scrape � d�ng {rowNumber}.\n�ang qu�t video SKU {sku}�",
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    log($"Dòng {rowNumber}: {step.Message ?? "không scrape được."}");
                }

                config.RunnerPhase = "video";
                config.LastRunnerMessage = $"Đang quét video SKU {sku}…";
                log($"Đang quét video SKU {sku} — dòng {rowNumber}…");
                await PushDisplayStateAsync(
                    cdpPort, profileRoot, config, sheet, startRow, endRow.Value,
                    index + 1, totalLinks, tabId, cancellationToken).ConfigureAwait(false);
                onProgress();

                await TryShowOverlayAsync(
                    cdpPort, profileRoot, tabId,
                    $"Đang quét video SKU {sku}…",
                    cancellationToken).ConfigureAwait(false);

                var videoOk = false;
                var videoPath = "";
                var pageUrl = step.PageUrl ?? link;
                var rowOutcomeOverlay = ""; // thông điệp kết quả cuối để giữ lại khi bước chờ ghi đè overlay

                try
                {
                    var candidates = await PageCdpHelper.CollectVideoCandidatesAsync(
                        cdpPort, pageUrl, cancellationToken).ConfigureAwait(false);
                    var downloadable = candidates
                        .Where(c => c.Duration is null or < VideoMaxDurationS)
                        .ToList();

                    if (downloadable.Count > 0)
                    {
                        Exception? downloadError = null;
                        for (var attempt = 0; attempt <= MaxDownloadRetries; attempt++)
                        {
                            try
                            {
                                var downloadMsg = attempt == 0
                                    ? $"Đang tải video tốt nhất cho SKU {sku}…"
                                    : $"Thử lại tải video (lần {attempt}) — SKU {sku}…";
                                config.LastRunnerMessage = downloadMsg;
                                log($"{downloadMsg} ({downloadable.Count} video tìm thấy)");
                                await PushDisplayStateAsync(
                                    cdpPort, profileRoot, config, sheet, startRow, endRow.Value,
                                    index + 1, totalLinks, tabId, cancellationToken).ConfigureAwait(false);
                                onProgress();
                                await TryShowOverlayAsync(
                                    cdpPort, profileRoot, tabId, downloadMsg, cancellationToken)
                                    .ConfigureAwait(false);

                                var result = await DownloadBestVideoAsync(sku, downloadable, cancellationToken)
                                    .ConfigureAwait(false);
                                videoOk = true;
                                videoPath = result.SavedPath;
                                var savedMsg = string.IsNullOrWhiteSpace(videoPath)
                                    ? $"Đã tải {sku}.mp4 - dòng {rowNumber}"
                                    : $"Đã tải {sku}.mp4\n{videoPath}";
                                config.LastRunnerMessage = savedMsg;
                                log($"Đã tải {sku}.mp4 - dòng {rowNumber}.");
                                rowOutcomeOverlay = savedMsg;
                                await TryShowOverlayAsync(
                                    cdpPort, profileRoot, tabId, savedMsg, cancellationToken)
                                    .ConfigureAwait(false);
                                downloadError = null;
                                break;
                            }
                            catch (Exception ex)
                            {
                                downloadError = ex;
                            }
                        }

                        if (downloadError is not null)
                        {
                            var errMsg = $"Dòng {rowNumber}: lỗi tải video SKU {sku}";
                            config.LastRunnerMessage = errMsg;
                            log($"Không tải được video SKU {sku}: {downloadError.Message}");
                        rowOutcomeOverlay = errMsg;
                            await TryShowOverlayAsync(
                                cdpPort, profileRoot, tabId, errMsg, cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        var noVideoMsg = $"Dòng {rowNumber}: không có video phù hợp (SKU {sku})";
                        config.LastRunnerMessage = noVideoMsg;
                        log($"Không có video phù hợp cho SKU {sku} — dòng {rowNumber}.");
                    rowOutcomeOverlay = noVideoMsg;
                        await TryShowOverlayAsync(
                            cdpPort, profileRoot, tabId, noVideoMsg, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    var errMsg = $"Lỗi quét video dòng {rowNumber}: {ex.Message}";
                    config.LastRunnerMessage = errMsg;
                    log(errMsg);
                rowOutcomeOverlay = errMsg;
                    await TryShowOverlayAsync(
                        cdpPort, profileRoot, tabId, errMsg, cancellationToken)
                        .ConfigureAwait(false);
                }

                config.RunLog.Add(new RunnerLogEntry
                {
                    RowNumber = rowNumber,
                    Sku = sku,
                    ScrapeOk = scrapeOk,
                    VideoOk = videoOk,
                    VideoPath = videoPath,
                });
                if (config.RunLog.Count > 200)
                    config.RunLog.RemoveRange(0, config.RunLog.Count - 200);

                config.LastCompletedRow = rowNumber;
                config.NextRunRow = rowNumber + 1;
                config.LastSku = sku;
                config.LastRunnerMessage = videoOk
                    ? $"Xong dòng {rowNumber} - đã tải video."
                    : $"Xong dòng {rowNumber}.";

                await PushDisplayStateAsync(
                    cdpPort, profileRoot, config, sheet, startRow, endRow.Value,
                    index + 1, totalLinks, tabId, cancellationToken).ConfigureAwait(false);
                onProgress();

                if (index < items.Count - 1)
                {
                    var restMs = RestRandom.Next(MinRestMs, MaxRestMs + 1);
                    var nextRow = rowNumber + 1;
                    var nextAt = DateTimeOffset.Now.AddMilliseconds(restMs);
                    var nextMsg = $"Link tiếp theo (dòng {nextRow}) lúc {nextAt.LocalDateTime:HH:mm:ss}";
                    var waitMsg = string.IsNullOrWhiteSpace(rowOutcomeOverlay)
                        ? nextMsg
                        : $"{nextMsg}\n{rowOutcomeOverlay}";

                    config.RunnerPhase = "waiting";
                    config.LastRunnerMessage = waitMsg;
                    await PushDisplayStateAsync(
                        cdpPort, profileRoot, config, sheet, startRow, endRow.Value,
                        index + 1, totalLinks, tabId, cancellationToken).ConfigureAwait(false);

                    if (tabId is not null)
                    {
                        try
                        {
                            await ExtensionRunnerAutomation.ShowOverlayAsync(
                                cdpPort, profileRoot, tabId.Value, waitMsg, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch
                        {
                            tabId = null;
                        }
                    }

                    log(waitMsg);
                    var preCheckDelay = Math.Max(0, restMs - 30_000);
                    if (preCheckDelay > 0)
                        await Task.Delay(preCheckDelay, cancellationToken).ConfigureAwait(false);

                    if (tabId is not null)
                    {
                        config.LastRunnerMessage = $"Kiểm tra captcha trước link tiếp theo — dòng {nextRow}.";
                        await PushDisplayStateAsync(
                            cdpPort, profileRoot, config, sheet, startRow, endRow.Value,
                            index + 1, totalLinks, tabId, cancellationToken).ConfigureAwait(false);
                        onProgress();

                        var check = await ExtensionRunnerAutomation.CheckBeforeNextLinkAsync(
                            cdpPort,
                            profileRoot,
                            tabId.Value,
                            rowNumber,
                            config.DisplayName,
                            sku,
                            cancellationToken).ConfigureAwait(false);

                        if (check.Aborted)
                            throw new OperationCanceledException();

                        if (!check.Ok)
                        {
                            config.RunnerPhase = check.Captcha ? "paused" : "error";
                            config.LastRunnerMessage =
                                check.Message ?? (check.Captcha
                                    ? $"Dừng vì captcha trước link tiếp theo - {config.DisplayName}, dòng {rowNumber} (SKU: {sku})."
                                    : $"URL hiện tại không hợp lệ trước link tiếp theo - dòng {rowNumber}.");
                            await PushDisplayStateAsync(
                                cdpPort, profileRoot, config, sheet, startRow, endRow.Value,
                                index + 1, totalLinks, check.TabId ?? tabId, cancellationToken).ConfigureAwait(false);
                            onProgress();
                            log(config.LastRunnerMessage);
                            return;
                        }

                        if (check.Waited)
                        {
                            log($"Đã giải captcha trước link tiếp theo - dòng {rowNumber}.");
                            await TryShowOverlayAsync(
                                cdpPort, profileRoot, tabId,
                                $"Đã giải captcha - chuẩn bị mở link tiếp theo (dòng {nextRow})",
                                cancellationToken).ConfigureAwait(false);
                        }
                    }

                    var remainingDelay = (int)Math.Max(
                        0,
                        (nextAt - DateTimeOffset.Now).TotalMilliseconds);
                    if (remainingDelay > 0)
                        await Task.Delay(remainingDelay, cancellationToken).ConfigureAwait(false);
                }
            }

            config.RunnerRunning = false;
            config.RunnerPhase = "finished";
            config.LastRunnerMessage = $"Hoàn tất {totalLinks} link của sheet \"{sheet}\".";
            log(config.LastRunnerMessage);

            if (tabId is not null)
                await ExtensionRunnerAutomation.HideOverlayAsync(cdpPort, profileRoot, tabId.Value, cancellationToken)
                    .ConfigureAwait(false);

            await PushDisplayStateAsync(
                cdpPort, profileRoot, config, sheet, startRow, endRow.Value,
                totalLinks, totalLinks, tabId, cancellationToken).ConfigureAwait(false);
            onProgress();
        }
        catch (OperationCanceledException)
        {
            config.RunnerRunning = false;
            config.RunnerPhase = "stopped";
            config.LastRunnerMessage = "Đã dừng từ Multi Brave Manager.";
            log(config.LastRunnerMessage);

            if (tabId is not null)
            {
                try
                {
                    await ExtensionRunnerAutomation.HideOverlayAsync(
                        cdpPort, profileRoot, tabId.Value, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // ignore
                }
            }

            await PushDisplayStateAsync(
                cdpPort, profileRoot, config, sheet, startRow, endRow.Value,
                config.RunLog?.Count ?? 0, totalLinks, tabId, CancellationToken.None).ConfigureAwait(false);
            onProgress();
            throw;
        }
        finally
        {
            config.RunnerRunning = false;
            config.ProgressSyncedAt = DateTimeOffset.Now;
        }
    }

    private static async Task TryShowOverlayAsync(
        int cdpPort,
        DirectoryInfo profileRoot,
        int? tabId,
        string text,
        CancellationToken cancellationToken)
    {
        if (tabId is null || string.IsNullOrWhiteSpace(text))
            return;

        try
        {
            await ExtensionRunnerAutomation.ShowOverlayAsync(
                cdpPort, profileRoot, tabId.Value, text, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // tab có thể đã đóng
        }
    }

    private static async Task PushDisplayStateAsync(
        int cdpPort,
        DirectoryInfo profileRoot,
        InstanceConfig config,
        string sheet,
        int startRow,
        int endRow,
        int currentLinkIndex,
        int totalLinks,
        int? tabId,
        CancellationToken cancellationToken)
    {
        var state = new
        {
            running = config.RunnerRunning == true,
            phase = config.RunnerPhase ?? "",
            sheetName = sheet,
            startRow,
            endRow,
            currentRow = config.CurrentRow,
            currentLinkIndex,
            totalLinks,
            lastCompletedRow = config.LastCompletedRow,
            lastSheetName = sheet,
            lastSku = config.LastSku ?? "",
            lastMessage = config.LastRunnerMessage ?? "",
            tabId,
            runLog = (config.RunLog ?? []).Select(e => new
            {
                rowNumber = e.RowNumber,
                sku = e.Sku,
                scrapeOk = e.ScrapeOk,
                videoOk = e.VideoOk,
                videoPath = e.VideoPath,
            }).ToList(),
        };

        await ExtensionRunnerAutomation.SetDisplayStateAsync(
            cdpPort, profileRoot, state, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<VideoDownloadResult> DownloadBestVideoAsync(
        string sku,
        List<VideoCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            sku,
            candidates = candidates.Select(c => new
            {
                url = c.Url,
                duration = c.Duration,
                label = c.Label,
            }),
        };

        var json = JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await AppServices.DirectHttp.PostAsync(
            $"{ApiServerHelper.DefaultApiBase}/video/download",
            content,
            cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(text);

        using var doc = JsonDocument.Parse(text);
        var saved = doc.RootElement.TryGetProperty("saved_path", out var p) ? p.GetString() : "";
        return new VideoDownloadResult(saved ?? "");
    }

    private static string ExtractSkuFromRow(Dictionary<string, object?> row, int rowNumber)
    {
        foreach (var key in row.Keys)
        {
            if (string.Equals(key, "sku", StringComparison.OrdinalIgnoreCase))
            {
                var sku = row[key]?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(sku))
                    return sku;
            }
        }

        foreach (var key in row.Keys)
        {
            if (key.Contains("sku", StringComparison.OrdinalIgnoreCase))
            {
                var sku = row[key]?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(sku))
                    return sku;
            }
        }

        return $"row_{rowNumber}";
    }

internal sealed record VideoDownloadResult(string SavedPath);
}

public sealed class RunnerLogEntry
{
    public int RowNumber { get; set; }
    public string Sku { get; set; } = "";
    public bool ScrapeOk { get; set; }
    public bool VideoOk { get; set; }
    public string VideoPath { get; set; } = "";
}
