using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenMultiBraveLauncherV3;

internal static class ExtensionRunnerAutomation
{
    public sealed record ScrapeStepResult(
        bool ScrapeOk,
        bool ProxyError,
        bool Captcha,
        bool Aborted,
        string? Message,
        int? TabId,
        string? PageUrl);

    public sealed record BeforeNextLinkCheckResult(
        bool Ok,
        bool Captcha,
        bool Aborted,
        bool Waited,
        string? Message,
        int? TabId,
        string? PageUrl);

    public sealed record SheetLinkItem(
        int RowNumber,
        Dictionary<string, object?> RowData,
        string Link);

    public sealed record SheetLinkFetchResult(
        List<SheetLinkItem> Items,
        int SkippedMissingProductName,
        int SkippedMissingLink);

    /// <summary>
    /// ID extension d� x�c th?c (c� launcher hook) theo t?ng CDP port.
    /// M?i thao t�c (setDisplayState, executeScrapeStep, �) d�ng C�NG m?t ID
    /// d? tr�nh ghi state v�o extension kh�c v?i popup dang hi?n th?.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _resolvedExtensionByPort = new();

    /// <summary>Xóa ID cache khi Brave khởi động lại / dừng instance.</summary>
    public static void ClearResolvedExtension(int cdpPort) =>
        _resolvedExtensionByPort.TryRemove(cdpPort, out _);

    public static async Task<string> EnsureRunnerExtensionReadyAsync(
        int cdpPort,
        DirectoryInfo profileRoot,
        Action<string>? log,
        CancellationToken cancellationToken = default,
        int timeoutSeconds = 90)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        var lastLog = DateTime.MinValue;
        var lastWake = DateTime.MinValue;
        var lastPopupReopen = DateTime.MinValue;
        var expectedId = TryGetRunnerExtensionIdFromProfile(profileRoot)
            ?? RunnerExtensionPaths.TryGetLoadedExtensionId()
            ?? throw new InvalidOperationException(
                "Không tìm thấy thư mục extension Shopee Data Runner — build lại launcher.");

        // Theo d�i CDP unreachable d? b�o l?i s?m n?u Brave kh�ng ch?y
        var cdpUnreachableSince = (DateTime?)null;
        const int CdpUnreachableTimeoutSeconds = 25;

        // Phiên mới → xóa cache cũ để re-resolve đúng extension đang chạy
        ClearResolvedExtension(cdpPort);

        await CloseRunnerExtensionPopupTabsAsync(cdpPort, profileRoot, cancellationToken);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Kiểm tra nhanh xem Brave có đang lắng nghe trên CDP không
            var cdpReachable = await IsCdpPortReachableAsync(cdpPort, cancellationToken);
            if (!cdpReachable)
            {
                cdpUnreachableSince ??= DateTime.UtcNow;
                if ((DateTime.UtcNow - cdpUnreachableSince.Value).TotalSeconds >= CdpUnreachableTimeoutSeconds)
                    throw new InvalidOperationException(
                        $"Brave không lắng nghe trên CDP port {cdpPort}. " +
                        "Brave có thể đã tắt - hãy nhấn Stop rồi Start lại instance.");

                if ((DateTime.UtcNow - lastLog).TotalSeconds >= 5)
                {
                    log?.Invoke($"CDP port {cdpPort} chưa sẵn sàng — chờ Brave khởi động…");
                    lastLog = DateTime.UtcNow;
                }
                await Task.Delay(1000, cancellationToken);
                continue;
            }

            cdpUnreachableSince = null;

            var ids = await DiscoverRunnerExtensionIdsAsync(cdpPort, profileRoot, cancellationToken);

            if ((DateTime.UtcNow - lastLog).TotalSeconds >= 5)
            {
                var idList = ids.Count > 0 ? string.Join(", ", ids) : "(không tìm thấy)";
                log?.Invoke($"Đang chờ extension Shopee Data Runner trên CDP… [IDs: {idList}]");
                lastLog = DateTime.UtcNow;
            }

            foreach (var id in ids)
            {
                var (probeOk, probeMsg) = await ProbeExtensionWithReasonAsync(cdpPort, id, cancellationToken);
                if (probeOk)
                {
                    // Ghi nh? ID d� x�c th?c � m?i thao t�c sau d�ng C�NG ID n�y
                    _resolvedExtensionByPort[cdpPort] = id;
                    await CloseRunnerExtensionPopupTabsAsync(cdpPort, profileRoot, cancellationToken);
                    return id;
                }
                log?.Invoke($"  Probe {id[..8]}…: {probeMsg}");
                if (probeMsg.Contains("hasScrapeStep=false", StringComparison.OrdinalIgnoreCase) &&
                    DateTime.UtcNow - lastWake >= TimeSpan.FromSeconds(4))
                {
                    log?.Invoke("  → Reload extension vì service worker chưa nạp runner hook…");
                    await TryReloadExtensionAsync(cdpPort, id, cancellationToken);
                    lastWake = DateTime.UtcNow;
                    await Task.Delay(1800, cancellationToken);
                }
                else if (IsPopupBridgeError(probeMsg) &&
                         DateTime.UtcNow - lastPopupReopen >= TimeSpan.FromSeconds(15))
                {
                    log?.Invoke("  → Mở lại popup extension vì service worker không nhận message…");
                    await TryWakeServiceWorkerAsync(cdpPort, id, cancellationToken, forceNewPopup: true);
                    lastPopupReopen = DateTime.UtcNow;
                    lastWake = DateTime.UtcNow;
                    await Task.Delay(1800, cancellationToken);
                }
            }

            if (DateTime.UtcNow - lastWake >= TimeSpan.FromSeconds(4))
            {
                var wakeId = ids.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(wakeId))
                {
                    log?.Invoke("  -> Đánh thức extension...");
                    await TryWakeServiceWorkerAsync(cdpPort, wakeId, cancellationToken);
                    lastWake = DateTime.UtcNow;
                    await Task.Delay(1500, cancellationToken);
                }
            }

            await Task.Delay(1000, cancellationToken);
        }

        throw new InvalidOperationException(
            $"Không kết nối được extension \"{RunnerExtensionPaths.ExtensionDisplayName}\" (dự kiến {expectedId}). " +
            "Đóng profile → Mở profile lại từ launcher.");
    }

    /// <summary>
    /// ��nh th?c MV3 service worker.
    /// Brave không hỗ trợ ServiceWorker CDP domain.
    /// C�ch d�ng tin c?y nh?t: m? popup.html trong tab m?i ? browser start SW.
    /// </summary>
    private static async Task TryWakeServiceWorkerAsync(
        int cdpPort,
        string extensionId,
        CancellationToken ct,
        bool forceNewPopup = false)
    {
        if (await GetSwTargetIdFromListAsync(cdpPort, extensionId, ct) is not null)
            return;

        var popupUrl = $"chrome-extension://{extensionId}/popup.html";

        // Cách 1: Chrome/Brave remote endpoint /json/new tạo tab extension ổn định hơn
        // Target.createTarget(background=true) có thể không materialize popup target trong Brave.
        try
        {
            using var response = await AppServices.DirectHttp.PutAsync(
                $"http://127.0.0.1:{cdpPort}/json/new?{Uri.EscapeDataString(popupUrl)}",
                content: null,
                ct);
            if (response.IsSuccessStatusCode)
                return;
        }
        catch
        {
            // fallback below
        }

        // C�ch 1 (CH�NH): m? popup URL trong tab m?i qua Target.createTarget.
        // N?u popup d� m? th� gi? nguy�n; Runtime.evaluate tr�n popup s? g?i
        // chrome.runtime.sendMessage v� t? d�nh th?c SW khi c?n. ��ng/m? l?i
        // liên tục làm launcher bị kẹt ở vòng "wake" và không vào bước chạy.
        var existingPopupTarget = await FindExtensionPopupTargetIdAsync(cdpPort, extensionId, ct);
        if (forceNewPopup && existingPopupTarget is not null)
        {
            await TryCloseCdpTargetAsync(cdpPort, existingPopupTarget, ct);
            await Task.Delay(350, ct);
            existingPopupTarget = null;
        }
        if (existingPopupTarget is not null)
            return;

        ClientWebSocket? browser = null;
        try
        {
            browser = await ConnectBrowserWebSocketAsync(cdpPort, ct);
            await SendCdpAsync(browser, 50, "Target.createTarget", new { url = popupUrl }, ct);
            // Kh�ng d�ng tab � d? popup l�m c?u n?i g?i SW cho c�c l?nh launcher.
            return;
        }
        catch { }
        finally
        {
            if (browser is not null)
            {
                try { if (browser.State == WebSocketState.Open) await browser.CloseAsync(WebSocketCloseStatus.NormalClosure, "", ct); } catch { }
                browser.Dispose();
            }
        }

        // C�ch 2 (fallback): Target.activateTarget qua /json/list id (khi SW dang c� nhung chua active)
        ClientWebSocket? browser2 = null;
        try
        {
            using var response = await AppServices.DirectHttp.GetAsync(
                $"http://127.0.0.1:{cdpPort}/json/list", ct);
            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var type = item.TryGetProperty("type", out var ty) ? ty.GetString() ?? "" : "";
                    var url = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                    var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    if (!string.Equals(type, "service_worker", StringComparison.OrdinalIgnoreCase) ||
                        !url.Contains(extensionId, StringComparison.OrdinalIgnoreCase) ||
                        string.IsNullOrWhiteSpace(id))
                        continue;

                    browser2 = await ConnectBrowserWebSocketAsync(cdpPort, ct);
                    await SendCdpAsync(browser2, 33, "Target.activateTarget", new { targetId = id }, ct);
                    await Task.Delay(500, ct);
                    return;
                }
            }
        }
        catch { }
        finally
        {
            if (browser2 is not null)
            {
                try { if (browser2.State == WebSocketState.Open) await browser2.CloseAsync(WebSocketCloseStatus.NormalClosure, "", ct); } catch { }
                browser2.Dispose();
            }
        }
    }

    private static async Task TryReloadExtensionAsync(int cdpPort, string extensionId, CancellationToken ct)
    {
        const string expression = "(() => { try { chrome.runtime.reload(); } catch (_) {} return { ok: true }; })()";

        try
        {
            var swWsUrl = await GetSwDebuggerUrlFromListAsync(cdpPort, extensionId, ct);
            if (swWsUrl is not null)
            {
                using var socket = new ClientWebSocket();
                await socket.ConnectAsync(new Uri(swWsUrl), ct);
                await SendCdpAsync(socket, 1, "Runtime.enable", null, ct);
                await SendCdpAsync(socket, 2, "Runtime.evaluate", new
                {
                    expression,
                    awaitPromise = true,
                    returnByValue = true,
                }, ct);
                return;
            }
        }
        catch
        {
            // fallback to popup below
        }

        try
        {
            await TryWakeServiceWorkerAsync(cdpPort, extensionId, ct);
            var popupWsUrl = await FindExtensionPopupDebuggerUrlAsync(cdpPort, extensionId, ct);
            if (popupWsUrl is null)
                return;

            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri(popupWsUrl), ct);
            await SendCdpAsync(socket, 1, "Runtime.enable", null, ct);
            await SendCdpAsync(socket, 2, "Runtime.evaluate", new
            {
                expression,
                awaitPromise = true,
                returnByValue = true,
            }, ct);
        }
        catch
        {
            // best effort
        }
    }

    private static async Task<List<string>> DiscoverRunnerExtensionIdsAsync(
        int cdpPort,
        DirectoryInfo profileRoot,
        CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (var id in DiscoverExtensionIdsFromProfile(profileRoot))
        {
            if (seen.Add(id))
                result.Add(id);
        }

        // Ch? d�ng ID t�nh t? path nhu fallback cu?i c�ng. M?t s? profile d� c� extension
        // kh�c tr�ng t�n file/popup; Preferences l� ngu?n d�ng tin hon d? tr�nh m? nh?m.
        var loaded = RunnerExtensionPaths.TryGetLoadedExtensionId();
        if (result.Count == 0 && !string.IsNullOrWhiteSpace(loaded) && seen.Add(loaded))
            result.Add(loaded);

        foreach (var id in await DiscoverExtensionIdsFromBrowserAsync(cdpPort, ct).ConfigureAwait(false))
        {
            if (seen.Add(id))
                result.Add(id);
        }

        return result;
    }

    public static string? TryGetRunnerExtensionIdFromProfile(DirectoryInfo profileRoot) =>
        DiscoverExtensionIdsFromProfile(profileRoot).FirstOrDefault();

    private static List<string> DiscoverExtensionIdsFromProfile(DirectoryInfo profileRoot)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var defaultDir = Path.Combine(profileRoot.FullName, "Default");
        DiscoverExtensionIdsFromPreferences(Path.Combine(defaultDir, "Preferences"), ids);
        DiscoverExtensionIdsFromPreferences(Path.Combine(defaultDir, "Secure Preferences"), ids);
        return ids.ToList();
    }

    private static void DiscoverExtensionIdsFromPreferences(string preferencesPath, ISet<string> ids)
    {
        if (!File.Exists(preferencesPath))
            return;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(preferencesPath));
            if (!doc.RootElement.TryGetProperty("extensions", out var extensions) ||
                !extensions.TryGetProperty("settings", out var settings) ||
                settings.ValueKind != JsonValueKind.Object)
                return;

            var loadDir = RunnerExtensionPaths.ResolveLoadDirectory();
            foreach (var setting in settings.EnumerateObject())
            {
                var id = setting.Name;
                if (id.Length != 32 || !id.All(c => c is >= 'a' and <= 'p'))
                    continue;

                var root = setting.Value;
                var manifestName = "";
                var defaultPopup = "";
                if (root.TryGetProperty("manifest", out var manifest) &&
                    manifest.ValueKind == JsonValueKind.Object)
                {
                    manifestName = manifest.TryGetProperty("name", out var nameEl)
                        ? nameEl.GetString() ?? ""
                        : "";
                    if (manifest.TryGetProperty("action", out var action) &&
                        action.ValueKind == JsonValueKind.Object &&
                        action.TryGetProperty("default_popup", out var popupEl))
                        defaultPopup = popupEl.GetString() ?? "";
                }

                var path = root.TryGetProperty("path", out var pathEl)
                    ? pathEl.GetString() ?? ""
                    : "";

                var nameMatches = string.Equals(
                    manifestName, RunnerExtensionPaths.ExtensionDisplayName,
                    StringComparison.OrdinalIgnoreCase);
                var popupMatches = string.Equals(defaultPopup, "popup.html", StringComparison.OrdinalIgnoreCase);
                var pathMatches = !string.IsNullOrWhiteSpace(loadDir) &&
                    PathsEqual(path, loadDir);

                if (pathMatches || (nameMatches && popupMatches))
                    ids.Add(id);
            }
        }
        catch
        {
            // Preferences có thể đang bị Brave ghi; bỏ qua, vòng sau sẽ thử lại.
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<List<string>> DiscoverExtensionIdsFromBrowserAsync(int cdpPort, CancellationToken ct)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var response = await AppServices.DirectHttp.GetAsync(
                $"http://127.0.0.1:{cdpPort}/json/list", ct);
            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var url = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                    TryAddExtensionIdFromUrl(url, ids);
                }
            }
        }
        catch
        {
            // ignore
        }

        ClientWebSocket? browser = null;
        try
        {
            browser = await ConnectBrowserWebSocketAsync(cdpPort, ct);
            var targets = await SendCdpAsync(browser, 40, "Target.getTargets", new { }, ct);
            if (targets.TryGetProperty("targetInfos", out var targetInfos))
            {
                foreach (var target in targetInfos.EnumerateArray())
                {
                    var url = target.TryGetProperty("url", out var urlEl) ? urlEl.GetString() ?? "" : "";
                    TryAddExtensionIdFromUrl(url, ids);
                }
            }
        }
        catch
        {
            // ignore
        }
        finally
        {
            if (browser is not null)
            {
                try
                {
                    if (browser.State == WebSocketState.Open)
                        await browser.CloseAsync(WebSocketCloseStatus.NormalClosure, "", ct);
                }
                catch
                {
                    // ignore
                }

                browser.Dispose();
            }
        }

        return ids.ToList();
    }

    private static void TryAddExtensionIdFromUrl(string url, ISet<string> ids)
    {
        if (!url.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase))
            return;

        var rest = url["chrome-extension://".Length..];
        var slash = rest.IndexOf('/');
        var id = slash >= 0 ? rest[..slash] : rest;
        if (id.Length == 32 && id.All(c => c is >= 'a' and <= 'p'))
            ids.Add(id);
    }

    private static async Task CloseAllExtensionPopupTabsAsync(int cdpPort, CancellationToken ct)
    {
        try
        {
            using var response = await AppServices.DirectHttp.GetAsync(
                $"http://127.0.0.1:{cdpPort}/json/list", ct);
            if (!response.IsSuccessStatusCode)
                return;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var url = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                if (!url.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (item.TryGetProperty("id", out var idEl))
                    await TryCloseCdpTargetAsync(cdpPort, idEl.GetString(), ct);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static async Task CloseRunnerExtensionPopupTabsAsync(
        int cdpPort,
        DirectoryInfo profileRoot,
        CancellationToken ct)
    {
        var runnerIds = DiscoverExtensionIdsFromProfile(profileRoot);
        if (runnerIds.Count == 0)
            return;

        try
        {
            using var response = await AppServices.DirectHttp.GetAsync(
                $"http://127.0.0.1:{cdpPort}/json/list", ct);
            if (!response.IsSuccessStatusCode)
                return;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var url = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                if (!runnerIds.Any(id => url.Equals(
                        $"chrome-extension://{id}/popup.html",
                        StringComparison.OrdinalIgnoreCase)))
                    continue;

                if (item.TryGetProperty("id", out var idEl))
                    await TryCloseCdpTargetAsync(cdpPort, idEl.GetString(), ct);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static async Task TryCloseCdpTargetAsync(int cdpPort, string? targetId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(targetId))
            return;

        try
        {
            using var _ = await AppServices.DirectHttp.GetAsync(
                $"http://127.0.0.1:{cdpPort}/json/close/{Uri.EscapeDataString(targetId)}",
                ct);
        }
        catch
        {
            // ignore
        }
    }

    public static async Task<ScrapeStepResult> ExecuteScrapeStepAsync(
        int cdpPort,
        DirectoryInfo profileRoot,
        string link,
        int rowNumber,
        string statusText,
        string instanceName,
        string sku,
        int? tabId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var extensionId = await ResolveExtensionIdAsync(cdpPort, profileRoot, cancellationToken)
                ?? throw new InvalidOperationException("Không tìm thấy extension Shopee Data Runner.");

            var payload = JsonSerializer.Serialize(new
            {
                link,
                rowNumber,
                statusText,
                instanceName,
                sku,
                tabId,
            });

            var val = await EvaluateExtensionMethodAsync(
                cdpPort, extensionId, "executeScrapeStep", payload, cancellationToken, maxAttempts: 6);
            if (val is null)
                return new ScrapeStepResult(false, false, false, false, "Extension không phản hồi.", tabId, link);

            return new ScrapeStepResult(
                val.Value.TryGetProperty("scrapeOk", out var s) && s.GetBoolean(),
                val.Value.TryGetProperty("proxyError", out var p) && p.GetBoolean(),
                val.Value.TryGetProperty("captcha", out var c) && c.GetBoolean(),
                val.Value.TryGetProperty("aborted", out var a) && a.GetBoolean(),
                val.Value.TryGetProperty("message", out var m) ? m.GetString() : null,
                val.Value.TryGetProperty("tabId", out var t) && t.ValueKind == JsonValueKind.Number
                    ? t.GetInt32()
                    : tabId,
                val.Value.TryGetProperty("pageUrl", out var u) ? u.GetString() : link);
        }
        catch (Exception ex)
        {
            var msg = ex.Message;
            if (msg.Contains("No tab with id", StringComparison.OrdinalIgnoreCase))
                return new ScrapeStepResult(false, false, false, false, "Tab đã đóng - sẽ mở tab mới ở bước sau.", null, link);
            return new ScrapeStepResult(false, false, false, false, msg, null, link);
        }
    }

    public static async Task SetDisplayStateAsync(
        int cdpPort,
        DirectoryInfo profileRoot,
        object state,
        CancellationToken cancellationToken = default)
    {
        var extensionId = await ResolveExtensionIdAsync(cdpPort, profileRoot, cancellationToken);
        if (extensionId is null)
            return;

        var stateJson = JsonSerializer.Serialize(state);

        // Ghi state cho extension chính (mở popup wake nếu cần)
        try
        {
            await EvaluateExtensionMethodAsync(
                cdpPort, extensionId, "setDisplayState", stateJson, cancellationToken, maxAttempts: 2);
        }
        catch (Exception ex) when (IsTransientSwError(ex.Message))
        {
            return;
        }

        // Cập nhật thêm BẤT KỲ extension trùng nào đang có popup MỞ SẴN (không mở tab mới).
        // Ph�ng tru?ng h?p ngu?i d�ng dang xem popup c?a b?n extension tr�ng (ID kh�c).
        try
        {
            foreach (var otherId in await DiscoverRunnerExtensionIdsAsync(cdpPort, profileRoot, cancellationToken))
            {
                if (string.Equals(otherId, extensionId, StringComparison.OrdinalIgnoreCase))
                    continue;
                var popupWs = await FindExtensionPopupDebuggerUrlAsync(cdpPort, otherId, cancellationToken);
                if (popupWs is null)
                    continue;

                using var socket = new ClientWebSocket();
                await socket.ConnectAsync(new Uri(popupWs), cancellationToken);
                await SendCdpAsync(socket, 1, "Runtime.enable", null, cancellationToken);
                await SendCdpAsync(socket, 2, "Runtime.evaluate", new
                {
                    expression = BuildPopupInvokeExpression("setDisplayState", PayloadExpression(stateJson)),
                    awaitPromise = true,
                    returnByValue = true,
                }, cancellationToken);
            }
        }
        catch { /* bản trùng không phản hồi — bỏ qua */ }
    }

    public static async Task<BeforeNextLinkCheckResult> CheckBeforeNextLinkAsync(
        int cdpPort,
        DirectoryInfo profileRoot,
        int tabId,
        int rowNumber,
        string instanceName,
        string sku,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var extensionId = await ResolveExtensionIdAsync(cdpPort, profileRoot, cancellationToken)
                ?? throw new InvalidOperationException("Không tìm thấy extension Shopee Data Runner.");

            var payload = JsonSerializer.Serialize(new
            {
                tabId,
                rowNumber,
                instanceName,
                sku,
            });

            var val = await EvaluateExtensionMethodAsync(
                cdpPort, extensionId, "checkBeforeNextLink", payload, cancellationToken, maxAttempts: 4);
            if (val is null)
                return new BeforeNextLinkCheckResult(true, false, false, false, null, tabId, null);

            return new BeforeNextLinkCheckResult(
                val.Value.TryGetProperty("ok", out var ok) && ok.GetBoolean(),
                val.Value.TryGetProperty("captcha", out var c) && c.GetBoolean(),
                val.Value.TryGetProperty("aborted", out var a) && a.GetBoolean(),
                val.Value.TryGetProperty("waited", out var w) && w.GetBoolean(),
                val.Value.TryGetProperty("message", out var m) ? m.GetString() : null,
                val.Value.TryGetProperty("tabId", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : tabId,
                val.Value.TryGetProperty("pageUrl", out var u) ? u.GetString() : null);
        }
        catch (Exception ex)
        {
            return new BeforeNextLinkCheckResult(false, false, false, false, ex.Message, tabId, null);
        }
    }

    public static async Task ShowOverlayAsync(
        int cdpPort,
        DirectoryInfo profileRoot,
        int tabId,
        string text,
        CancellationToken cancellationToken = default)
    {
        var extensionId = await ResolveExtensionIdAsync(cdpPort, profileRoot, cancellationToken);
        if (extensionId is null)
            return;

        var payload = JsonSerializer.Serialize(new { tabId, text });
        try
        {
            await EvaluateExtensionMethodAsync(
                cdpPort, extensionId, "showOverlay", payload, cancellationToken, maxAttempts: 2);
        }
        catch (Exception ex) when (IsTransientSwError(ex.Message))
        {
            return;
        }
    }

    public static async Task HideOverlayAsync(
        int cdpPort,
        DirectoryInfo profileRoot,
        int tabId,
        CancellationToken cancellationToken = default)
    {
        var extensionId = await ResolveExtensionIdAsync(cdpPort, profileRoot, cancellationToken);
        if (extensionId is null)
            return;

        var payload = JsonSerializer.Serialize(new { tabId });
        try
        {
            await EvaluateExtensionMethodAsync(
                cdpPort, extensionId, "hideOverlay", payload, cancellationToken, maxAttempts: 2);
        }
        catch (Exception ex) when (IsTransientSwError(ex.Message))
        {
            return;
        }
    }

    public static async Task AbortScrapeStepAsync(
        int cdpPort,
        DirectoryInfo profileRoot,
        CancellationToken cancellationToken = default)
    {
        var extensionId = await ResolveExtensionIdAsync(cdpPort, profileRoot, cancellationToken);
        if (extensionId is null)
            return;

        await EvaluateExtensionMethodAsync(
            cdpPort, extensionId, "abortStep", null, cancellationToken, maxAttempts: 2);
    }

    public static async Task StopRunAsync(
        int cdpPort,
        DirectoryInfo profileRoot,
        Action<string> log,
        CancellationToken cancellationToken = default)
    {
        var extensionId = await ResolveExtensionIdAsync(cdpPort, profileRoot, cancellationToken);
        if (extensionId is null)
        {
            log("Không tìm thấy extension trên CDP — bỏ qua bước dừng runner.");
            return;
        }

        log("Đang dừng runner…");
        await AbortScrapeStepAsync(cdpPort, profileRoot, cancellationToken).ConfigureAwait(false);

        var evalResult = await EvaluateExtensionRawAsync(
            cdpPort, extensionId, "stopRun", null, cancellationToken, maxAttempts: 4);
        if (evalResult.TryGetProperty("exceptionDetails", out var exDetails) &&
            exDetails.ValueKind == JsonValueKind.Object)
            throw new InvalidOperationException(FormatCdpException(exDetails));

        if (evalResult.TryGetProperty("result", out var res) &&
            res.TryGetProperty("value", out var val) &&
            val.ValueKind == JsonValueKind.Object)
        {
            var last = val.TryGetProperty("lastCompletedRow", out var l) && l.ValueKind == JsonValueKind.Number
                ? l.GetInt32()
                : (int?)null;
            var cur = val.TryGetProperty("currentRow", out var c) && c.ValueKind == JsonValueKind.Number
                ? c.GetInt32()
                : (int?)null;
            var sheet = val.TryGetProperty("sheetName", out var s) ? s.GetString() : "";
            log(
                last is > 0
                    ? $"Extension đã dừng - xong dòng {last}" + (cur is > 0 ? $", đang dừng tại {cur}" : "") +
                      (string.IsNullOrWhiteSpace(sheet) ? "" : $", sheet \"{sheet}\"")
                    : "Extension đã dừng.");
            return;
        }

        log("Extension đã nhận lệnh dừng.");
        await TryBroadcastRunnerStateAsync(cdpPort, profileRoot, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<ExtensionRunnerState?> TryReadStateViaCdpAsync(
        int cdpPort,
        DirectoryInfo profileRoot,
        CancellationToken cancellationToken = default)
    {
        var extensionId = await ResolveExtensionIdAsync(cdpPort, profileRoot, cancellationToken);
        if (extensionId is null)
            return null;

        var val = await EvaluateExtensionMethodAsync(
            cdpPort, extensionId, "getRunnerState", null, cancellationToken, maxAttempts: 6);
        return val is null ? null : MapStateFromCdp(val.Value);
    }

    public static async Task<bool> TryApplyFormConfigAsync(
        int cdpPort,
        DirectoryInfo profileRoot,
        string sheetName,
        int? startRow,
        int? endRow,
        CancellationToken cancellationToken = default)
    {
        var extensionId = await ResolveExtensionIdAsync(cdpPort, profileRoot, cancellationToken);
        if (extensionId is null)
            return false;

        var payload = JsonSerializer.Serialize(new
        {
            sheetName = sheetName?.Trim() ?? "",
            startRow = startRow is > 0 ? startRow.Value : 0,
            endRow = endRow is > 0 ? endRow.Value : 0,
        });

        var val = await EvaluateExtensionMethodAsync(
            cdpPort,
            extensionId,
            "applyFormConfig",
            payload,
            cancellationToken,
            maxAttempts: 15);
        return val?.TryGetProperty("ok", out var ok) == true && ok.GetBoolean();
    }

    public static async Task TryBroadcastRunnerStateAsync(
        int cdpPort,
        DirectoryInfo profileRoot,
        CancellationToken cancellationToken = default)
    {
        var extensionId = await ResolveExtensionIdAsync(cdpPort, profileRoot, cancellationToken);
        if (extensionId is null)
            return;

        try
        {
            await EvaluateExtensionMethodAsync(
                cdpPort, extensionId, "notifyRunnerUi", null, cancellationToken, maxAttempts: 2);
        }
        catch
        {
            // popup có thể đang đóng
        }
    }

    private static string BuildPopupInvokeExpression(string method, string payloadExpression) =>
        "(async () => {" +
        "const response = await chrome.runtime.sendMessage({" +
        $"type:'LAUNCHER_INVOKE',method:{JsonSerializer.Serialize(method)},payload:{payloadExpression}" +
        "});" +
        "if (!response?.ok) throw new Error(response?.error || 'Extension không phản hồi');" +
        "return response.result;" +
        "})()";

    private static string BuildServiceWorkerMethodExpression(string method, string payloadExpression) =>
        method switch
        {
            "probe" => "({ hasScrapeStep: typeof globalThis.__launcherExecuteScrapeStep === 'function' })",
            "executeScrapeStep" => $"(async () => globalThis.__launcherExecuteScrapeStep({payloadExpression}))()",
            "setDisplayState" => $"(async () => globalThis.__launcherSetDisplayState({payloadExpression}))()",
            "getRunnerState" => "(async () => globalThis.__launcherGetRunnerState())()",
            "applyFormConfig" => $"(async () => globalThis.__launcherApplyFormConfig({payloadExpression}))()",
            "showOverlay" => $"(async () => globalThis.__launcherShowOverlay({payloadExpression}))()",
            "hideOverlay" => $"(async () => globalThis.__launcherHideOverlay({payloadExpression}))()",
            "abortStep" => "(async () => globalThis.__launcherAbortStep())()",
            "stopRun" => "(async () => globalThis.__launcherStopRun())()",
            "notifyRunnerUi" =>
                "(async () => { try { const s=(await chrome.storage.local.get('runnerState')).runnerState||{running:false};" +
                "await chrome.runtime.sendMessage({type:'RUNNER_STATE',state:s}); } catch(_){} return { ok: true }; })()",
            "checkBeforeNextLink" => $"(async () => globalThis.__launcherCheckBeforeNextLink({payloadExpression}))()",
            _ => BuildPopupInvokeExpression(method, payloadExpression),
        };

    private static string PayloadExpression(string? payloadJson) =>
        payloadJson is null ? "null" : $"JSON.parse({JsonSerializer.Serialize(payloadJson)})";

    private static async Task<JsonElement?> EvaluateExtensionMethodAsync(
        int cdpPort,
        string extensionId,
        string method,
        string? payloadJson,
        CancellationToken ct,
        int maxAttempts = 15)
    {
        var payloadExpr = PayloadExpression(payloadJson);
        var evalResult = await EvaluateExtensionRawAsync(
            cdpPort,
            extensionId,
            method,
            payloadJson,
            ct,
            maxAttempts);

        if (evalResult.TryGetProperty("exceptionDetails", out var exDetails) &&
            exDetails.ValueKind == JsonValueKind.Object)
            throw new InvalidOperationException(FormatCdpException(exDetails));

        if (evalResult.TryGetProperty("result", out var res) &&
            res.TryGetProperty("value", out var val) &&
            val.ValueKind == JsonValueKind.Object)
            return val.Clone();

        return null;
    }

    private static async Task<JsonElement> EvaluateExtensionRawAsync(
        int cdpPort,
        string extensionId,
        string method,
        string? payloadJson,
        CancellationToken ct,
        int maxAttempts = 15)
    {
        var payloadExpr = PayloadExpression(payloadJson);
        var swExpression = BuildServiceWorkerMethodExpression(method, payloadExpr);
        var popupExpression = BuildPopupInvokeExpression(method, payloadExpr);
        var isProbe = string.Equals(method, "probe", StringComparison.OrdinalIgnoreCase);

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var swResult = await TryEvaluateOnServiceWorkerAsync(cdpPort, extensionId, swExpression, ct);
            if (swResult is not null &&
                !HasTransientSwException(swResult.Value) &&
                (!isProbe || IsReadyProbeResult(swResult.Value)))
                return swResult.Value;

            // ƯU TIÊN đường popup → chrome.runtime.sendMessage → SW.
            // Trong Brave, SW extension thường KHÔNG xuất hiện như target độc lập trong /json/list,
            // và Target.getTargets có thể trả về SW context rỗng (function chưa định nghĩa → false sai).
            // �u?ng popup d�ng sendMessage t? d�nh th?c SW v� g?i d�ng message handler ? d�ng tin c?y.
            var popupWsUrl = await FindExtensionPopupDebuggerUrlAsync(cdpPort, extensionId, ct);
            if (popupWsUrl is not null)
            {
                using var socket = new ClientWebSocket();
                await socket.ConnectAsync(new Uri(popupWsUrl), ct);
                await SendCdpAsync(socket, 1, "Runtime.enable", null, ct);
                var popupResult = await SendCdpAsync(socket, 2, "Runtime.evaluate", new
                {
                    expression = popupExpression,
                    awaitPromise = true,
                    returnByValue = true,
                }, ct);

                if (!IsPopupBridgeError(popupResult) && !HasTransientSwException(popupResult))
                    return popupResult;

                var swFallback = await TryEvaluateOnServiceWorkerAsync(cdpPort, extensionId, swExpression, ct);
                if (swFallback is not null &&
                    !HasTransientSwException(swFallback.Value) &&
                    (!isProbe || IsReadyProbeResult(swFallback.Value)))
                    return swFallback.Value;

                await TryWakeServiceWorkerAsync(cdpPort, extensionId, ct);
                if (attempt < maxAttempts - 1)
                {
                    await Task.Delay(700, ct);
                    continue;
                }

                return isProbe && swFallback is not null && !IsReadyProbeResult(swFallback.Value)
                    ? popupResult
                    : swFallback ?? popupResult;
            }

            // Fallback: SW-direct (khi popup chưa mở nhưng SW target có trong /json/list)
            // Kh�ng c� popup l?n SW target ? m? popup d? d�nh th?c SW r?i th? l?i
            await TryWakeServiceWorkerAsync(cdpPort, extensionId, ct);
            if (attempt < maxAttempts - 1)
            {
                await Task.Delay(700, ct);
                continue;
            }

            return MakeTransientExtensionErrorResult();
        }

        return MakeTransientExtensionErrorResult();
    }

    private static bool IsReadyProbeResult(JsonElement evalResult)
    {
        if (!evalResult.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("value", out var value) ||
            value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("hasScrapeStep", out var hasScrapeStep) ||
            hasScrapeStep.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            return false;

        return hasScrapeStep.GetBoolean();
    }

    private static JsonElement MakeTransientExtensionErrorResult() =>
        JsonDocument.Parse("{\"exceptionDetails\":{\"text\":\"No SW: extension chưa sẵn sàng trên CDP\"}}")
            .RootElement.Clone();

    /// <summary>
    /// Giữ SW sống bằng flat session (Target.attachToTarget qua browser WS).
    /// Kh�c v?i direct WS, flat session KH�NG l�m SW target bi?n kh?i /json/list �
    /// probe vẫn thấy và kết nối được SW target trong khi pinner đang giữ.
    /// </summary>
    public static async Task PinSwWithFlatSessionAsync(
        int cdpPort, string extensionId, Action<string> log, CancellationToken ct)
    {
        // T�m nhanh SW trong /json/list � th? t?i da 40 l?n x 300ms = 12 gi�y
        for (var i = 0; i < 40 && !ct.IsCancellationRequested; i++)
        {
            try
            {
                await Task.Delay(300, ct).ConfigureAwait(false);
                var swId = await GetSwTargetIdFromListAsync(cdpPort, extensionId, ct).ConfigureAwait(false);
                if (swId is null) continue;

                log($"SW pinner: attach flat session tới target {swId[..Math.Min(swId.Length, 16)]}…");
                using var browser = await ConnectBrowserWebSocketAsync(cdpPort, ct).ConfigureAwait(false);

                var attach = await SendCdpAsync(browser, 1, "Target.attachToTarget", new
                {
                    targetId = swId,
                    flatten = true,
                }, ct).ConfigureAwait(false);

                if (!attach.TryGetProperty("sessionId", out var sessEl)) continue;
                var sess = sessEl.GetString();
                if (string.IsNullOrWhiteSpace(sess)) continue;

                log("SW pinner: flat session OK, đang giữ SW sống…");

                // Giữ browser WS open → duy trì flat session → SW không bị Brave terminate.
                // Kh�ng d�ng ReceiveAsync timeout ? d�y: cancel receive c� th? l�m ClientWebSocket
                // chuy?n sang Aborted sau d�ng 30 gi�y.
                var keepAliveId = 100;
                while (!ct.IsCancellationRequested && browser.State == WebSocketState.Open)
                {
                    try
                    {
                        await Task.Delay(20_000, ct).ConfigureAwait(false);
                        if (browser.State != WebSocketState.Open)
                            break;

                        await SendCdpAsync(browser, keepAliveId++, "Target.getTargets", new { }, ct)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { break; }
                    catch { break; }
                }

                log($"SW pinner: flat session đóng (state={browser.State})");
                break;
            }
            catch (OperationCanceledException) { break; }
            catch { /* Brave chưa sẵn sàng, thử lại */ }
        }
    }

    /// <summary>
    /// Lấy webSocketDebuggerUrl của SW extension từ /json/list (Brave không trả targetId trong Target.getTargets).
    /// </summary>
    private static async Task<string?> GetSwDebuggerUrlFromListAsync(
        int cdpPort, string extensionId, CancellationToken ct)
    {
        try
        {
            using var response = await AppServices.DirectHttp.GetAsync(
                $"http://127.0.0.1:{cdpPort}/json/list", ct);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var type = item.TryGetProperty("type", out var ty) ? ty.GetString() ?? "" : "";
                var url = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                var ws = item.TryGetProperty("webSocketDebuggerUrl", out var w) ? w.GetString() : null;
                if (string.Equals(type, "service_worker", StringComparison.OrdinalIgnoreCase) &&
                    url.Contains(extensionId, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(ws))
                    return ws;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Lấy id (targetId CDP) của SW extension từ /json/list.
    /// Brave không trả service_worker qua Target.getTargets, nhưng /json/list có đầy đủ thông tin.
    /// </summary>
    private static async Task<string?> GetSwTargetIdFromListAsync(
        int cdpPort, string extensionId, CancellationToken ct)
    {
        try
        {
            using var response = await AppServices.DirectHttp.GetAsync(
                $"http://127.0.0.1:{cdpPort}/json/list", ct);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var type = item.TryGetProperty("type", out var ty) ? ty.GetString() ?? "" : "";
                var url = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                if (string.Equals(type, "service_worker", StringComparison.OrdinalIgnoreCase) &&
                    url.Contains(extensionId, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(id))
                    return id;
            }
        }
        catch { }
        return null;
    }

    private static async Task<JsonElement?> TryEvaluateOnServiceWorkerAsync(
        int cdpPort,
        string extensionId,
        string expression,
        CancellationToken ct)
    {
        // Approach 1: dùng /json/list → webSocketDebuggerUrl trực tiếp
        // (hoạt động khi không có SW pinner đang giữ kết nối)
        try
        {
            var swWsUrl = await GetSwDebuggerUrlFromListAsync(cdpPort, extensionId, ct);
            if (!string.IsNullOrWhiteSpace(swWsUrl))
            {
                using var swSocket = new ClientWebSocket();
                await swSocket.ConnectAsync(new Uri(swWsUrl), ct);
                await SendCdpAsync(swSocket, 1, "Runtime.enable", null, ct);
                return await SendCdpAsync(swSocket, 2, "Runtime.evaluate", new
                {
                    expression,
                    awaitPromise = true,
                    returnByValue = true,
                }, ct);
            }
        }
        catch { }

        // Approach 2: dùng /json/list id → Target.attachToTarget qua browser WS (flat session)
        // Hoạt động khi SW pinner đang giữ kết nối trực tiếp — flat session là độc lập, không conflict.
        try
        {
            var swTargetId = await GetSwTargetIdFromListAsync(cdpPort, extensionId, ct);
            if (!string.IsNullOrWhiteSpace(swTargetId))
            {
                using var browserWs = await ConnectBrowserWebSocketAsync(cdpPort, ct);
                var attach = await SendCdpAsync(browserWs, 21, "Target.attachToTarget", new
                {
                    targetId = swTargetId,
                    flatten = true,
                }, ct);
                if (attach.TryGetProperty("sessionId", out var sessEl))
                {
                    var sess = sessEl.GetString();
                    if (!string.IsNullOrWhiteSpace(sess))
                    {
                        await SendCdpAsync(browserWs, 22, "Runtime.enable", null, ct, sess);
                        return await SendCdpAsync(browserWs, 23, "Runtime.evaluate", new
                        {
                            expression,
                            awaitPromise = true,
                            returnByValue = true,
                        }, ct, sess);
                    }
                }
            }
        }
        catch { }

        // Approach 3: Target.getTargets + attachToTarget (Chrome standard, không hoạt động trong Brave)
        ClientWebSocket? browser = null;
        try
        {
            browser = await ConnectBrowserWebSocketAsync(cdpPort, ct);
            var targets = await SendCdpAsync(browser, 20, "Target.getTargets", new { }, ct);
            if (!targets.TryGetProperty("targetInfos", out var targetInfos))
                return null;

            string? targetId = null;
            var fallbackTargets = new List<string>();
            foreach (var target in targetInfos.EnumerateArray())
            {
                var type = target.TryGetProperty("type", out var typeEl) ? typeEl.GetString() ?? "" : "";
                var url = target.TryGetProperty("url", out var urlEl) ? urlEl.GetString() ?? "" : "";
                if (!string.Equals(type, "service_worker", StringComparison.OrdinalIgnoreCase) ||
                    !url.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!target.TryGetProperty("targetId", out var targetIdEl))
                    continue;

                var id = targetIdEl.GetString();
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                if (url.Contains(extensionId, StringComparison.OrdinalIgnoreCase))
                {
                    targetId = id;
                    break;
                }

                fallbackTargets.Add(id);
            }

            targetId ??= fallbackTargets.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(targetId))
                return null;

            var attach = await SendCdpAsync(browser, 21, "Target.attachToTarget", new
            {
                targetId,
                flatten = true,
            }, ct);
            if (!attach.TryGetProperty("sessionId", out var sessionEl))
                return null;

            var sessionId = sessionEl.GetString();
            if (string.IsNullOrWhiteSpace(sessionId))
                return null;

            await SendCdpAsync(browser, 22, "Runtime.enable", null, ct, sessionId);
            return await SendCdpAsync(browser, 23, "Runtime.evaluate", new
            {
                expression,
                awaitPromise = true,
                returnByValue = true,
            }, ct, sessionId);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (browser is not null)
            {
                try
                {
                    if (browser.State == WebSocketState.Open)
                        await browser.CloseAsync(WebSocketCloseStatus.NormalClosure, "", ct);
                }
                catch
                {
                    // ignore
                }

                browser.Dispose();
            }
        }
    }

    private static async Task<ClientWebSocket> ConnectBrowserWebSocketAsync(int cdpPort, CancellationToken ct)
    {
        using var response = await AppServices.DirectHttp.GetAsync(
            $"http://127.0.0.1:{cdpPort}/json/version", ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var wsUrl = doc.RootElement.GetProperty("webSocketDebuggerUrl").GetString()
            ?? throw new InvalidOperationException("CDP browser endpoint không khả dụng.");

        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(wsUrl), ct);
        return socket;
    }

    private static bool IsPopupBridgeError(JsonElement evalResult)
    {
        if (!evalResult.TryGetProperty("exceptionDetails", out var exDetails) ||
            exDetails.ValueKind != JsonValueKind.Object)
            return false;

        var message = FormatCdpException(exDetails);
        return IsPopupBridgeError(message);
    }

    private static bool HasTransientSwException(JsonElement evalResult)
    {
        if (!evalResult.TryGetProperty("exceptionDetails", out var exDetails) ||
            exDetails.ValueKind != JsonValueKind.Object)
            return false;

        return IsTransientSwError(FormatCdpException(exDetails));
    }

    private static bool IsPopupBridgeError(string message) =>
        message.Contains("Receiving end does not exist", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("Could not establish connection", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("sendMessage", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("chrome.runtime", StringComparison.OrdinalIgnoreCase);

    private static bool IsTransientSwError(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        return IsPopupBridgeError(message) ||
               message.Contains("No SW", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("service worker", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Cannot find context", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("remote party closed the WebSocket", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Target closed", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Inspected target navigated or closed", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatCdpException(JsonElement exDetails)
    {
        if (exDetails.TryGetProperty("exception", out var ex) &&
            ex.TryGetProperty("description", out var desc))
        {
            var text = desc.GetString();
            if (!string.IsNullOrWhiteSpace(text))
                return text.Split('\n')[0];
        }

        if (exDetails.TryGetProperty("text", out var t))
            return t.GetString() ?? exDetails.ToString();

        return exDetails.ToString();
    }

    private static ExtensionRunnerState MapStateFromCdp(JsonElement root)
    {
        if (!root.TryGetProperty("runnerState", out var rs) || rs.ValueKind != JsonValueKind.Object)
            rs = root;

        var sheetName = GetStringProp(rs, "sheetName", "lastSheetName");
        var startRow = GetIntProp(rs, "startRow");
        var endRow = GetIntProp(rs, "endRow");

        if (root.TryGetProperty("lastRunConfig", out var cfg) && cfg.ValueKind == JsonValueKind.Object)
        {
            if (string.IsNullOrWhiteSpace(sheetName))
                sheetName = GetStringProp(cfg, "sheetName");
            if (startRow is null or < 1)
                startRow = GetIntProp(cfg, "startRow");
            if (endRow is null or < 1)
                endRow = GetIntProp(cfg, "endRow");
        }

        return new ExtensionRunnerState
        {
            SheetName = sheetName,
            StartRow = startRow,
            EndRow = endRow,
            LastCompletedRow = GetIntProp(rs, "lastCompletedRow"),
            CurrentRow = GetIntProp(rs, "currentRow"),
            LastSku = GetStringProp(rs, "lastSku"),
            Phase = GetStringProp(rs, "phase"),
            Running = GetBoolProp(rs, "running"),
            LastMessage = GetStringProp(rs, "lastMessage"),
        };
    }

    private static string? GetStringProp(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var el))
                continue;
            if (el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(s))
                    return s;
            }
            else if (el.ValueKind == JsonValueKind.Number)
            {
                return el.GetRawText();
            }
        }

        return null;
    }

    private static int? GetIntProp(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el))
            return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n) && n > 0)
            return n;
        if (el.ValueKind == JsonValueKind.String &&
            int.TryParse(el.GetString(), out var parsed) &&
            parsed > 0)
            return parsed;
        return null;
    }

    private static bool? GetBoolProp(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el))
            return null;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static async Task<string?> ResolveExtensionIdAsync(
        int cdpPort,
        DirectoryInfo profileRoot,
        CancellationToken ct,
        bool allowProfileFallback = true)
    {
        if (!allowProfileFallback)
            return null;

        // D�ng l?i ID d� x�c th?c t? EnsureRunnerExtensionReadyAsync (nh?t qu�n + tr�nh re-probe)
        if (_resolvedExtensionByPort.TryGetValue(cdpPort, out var cached))
            return cached;

        foreach (var id in await DiscoverRunnerExtensionIdsAsync(cdpPort, profileRoot, ct))
        {
            if (await ExtensionHasLauncherHookAsync(cdpPort, id, ct))
            {
                _resolvedExtensionByPort[cdpPort] = id;
                return id;
            }
        }

        return null;
    }

    private static async Task<string?> ResolveExtensionIdAsync(
        int cdpPort,
        DirectoryInfo profileRoot,
        CancellationToken ct) =>
        await ResolveExtensionIdAsync(cdpPort, profileRoot, ct, allowProfileFallback: true);

    private static async Task<bool> ExtensionHasLauncherHookAsync(int cdpPort, string extensionId, CancellationToken ct)
    {
        var (ok, _) = await ProbeExtensionWithReasonAsync(cdpPort, extensionId, ct);
        return ok;
    }

    /// <summary>Dump tất cả entries trong /json/list để debug (type + url ngắn).</summary>
    private static async Task<string> GetAllSwTargetsSummaryAsync(int cdpPort, CancellationToken ct)
    {
        try
        {
            using var response = await AppServices.DirectHttp.GetAsync(
                $"http://127.0.0.1:{cdpPort}/json/list", ct);
            if (!response.IsSuccessStatusCode) return "(HTTP fail)";
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var entries = new List<string>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var type = item.TryGetProperty("type", out var ty) ? ty.GetString() ?? "?" : "?";
                var url = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                var wsOk = item.TryGetProperty("webSocketDebuggerUrl", out _) ? "ws+" : "ws-";
                var shortUrl = url.Length > 55 ? url[..55] : url;
                entries.Add($"{type}({wsOk}):{shortUrl}");
            }
            return entries.Count == 0 ? "(list rỗng)" : string.Join(" | ", entries);
        }
        catch (Exception ex) { return $"(ex: {ex.Message})"; }
    }

    private static async Task<(bool ok, string reason)> ProbeExtensionWithReasonAsync(
        int cdpPort, string extensionId, CancellationToken ct)
    {
        try
        {
            // N?u chua c� SW l?n popup ? m? popup d? d�nh th?c SW tru?c khi evaluate.
            // (SW extension trong Brave thường không xuất hiện như target độc lập, và bị
            //  terminate khi idle → cần popup để wake + làm cầu nối sendMessage tới SW.)
            var swWsUrl = await GetSwDebuggerUrlFromListAsync(cdpPort, extensionId, ct);
            var popupUrl = await FindExtensionPopupDebuggerUrlAsync(cdpPort, extensionId, ct);
            if (swWsUrl is null && popupUrl is null)
            {
                await TryWakeServiceWorkerAsync(cdpPort, extensionId, ct);
                await Task.Delay(800, ct);
                popupUrl = await FindExtensionPopupDebuggerUrlAsync(cdpPort, extensionId, ct);
                if (popupUrl is null)
                {
                    var swSummary = await GetAllSwTargetsSummaryAsync(cdpPort, ct);
                    return (false, $"không có SW target và không có popup [json/list SWs: {swSummary}]");
                }
            }

            var val = await EvaluateExtensionMethodAsync(cdpPort, extensionId, "probe", null, ct, maxAttempts: 8);
            if (val is null)
                return (false, "evaluate trả về null");

            var ok = val.Value.TryGetProperty("hasScrapeStep", out var hook) && hook.GetBoolean();
            return (ok, ok ? "OK" : $"hasScrapeStep=false (val={val.Value})");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static async Task<string?> FindServiceWorkerTargetInfoAsync(
        int cdpPort, string extensionId, CancellationToken ct)
    {
        try
        {
            var browser = await ConnectBrowserWebSocketAsync(cdpPort, ct);
            try
            {
                var targets = await SendCdpAsync(browser, 40, "Target.getTargets", new { }, ct);
                if (!targets.TryGetProperty("targetInfos", out var infos))
                    return null;

                foreach (var t in infos.EnumerateArray())
                {
                    var type = t.TryGetProperty("type", out var ty) ? ty.GetString() ?? "" : "";
                    var url = t.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                    if (string.Equals(type, "service_worker", StringComparison.OrdinalIgnoreCase) &&
                        url.Contains(extensionId, StringComparison.OrdinalIgnoreCase))
                    {
                        return t.TryGetProperty("targetId", out var tid) ? tid.GetString() : null;
                    }
                }
            }
            finally
            {
                try { if (browser.State == WebSocketState.Open) await browser.CloseAsync(WebSocketCloseStatus.NormalClosure, "", ct); } catch { }
                browser.Dispose();
            }
        }
        catch { }
        return null;
    }

    private static object? JsonValueToObject(JsonElement el) =>
        el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.TryGetInt64(out var i) ? i : el.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => el.ToString(),
        };

    public static async Task<int> ResolveEndRowAsync(string sheet, int startRow, CancellationToken ct)
    {
        var url = $"{ApiServerHelper.DefaultApiBase}/data/{Uri.EscapeDataString(sheet)}?start_row=1&end_row=1";
        using var doc = await GetJsonAsync(url, ct);
        if (!doc.RootElement.TryGetProperty("total_rows", out var totalEl))
            throw new InvalidOperationException("API không trả về total_rows.");
        var total = totalEl.GetInt32();
        if (total < startRow)
            throw new InvalidOperationException($"Từ dòng {startRow} vượt quá số dòng sheet ({total}).");
        return total;
    }

    public static async Task<SheetLinkFetchResult> FetchSheetLinksAsync(
        string sheet,
        int startRow,
        int endRow,
        CancellationToken ct)
    {
        var url =
            $"{ApiServerHelper.DefaultApiBase}/data/{Uri.EscapeDataString(sheet)}?start_row={startRow}&end_row={endRow}";
        using var doc = await GetJsonAsync(url, ct);
        if (!doc.RootElement.TryGetProperty("data", out var dataEl) ||
            dataEl.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("API không trả về mảng data.");

        var items = new List<SheetLinkItem>();
        var skippedMissingProductName = 0;
        var skippedMissingLink = 0;
        var offset = 0;
        foreach (var row in dataEl.EnumerateArray())
        {
            var rowNumber = startRow + offset;
            offset++;

            var dict = new Dictionary<string, object?>();
            foreach (var prop in row.EnumerateObject())
                dict[prop.Name] = JsonValueToObject(prop.Value);

            if (!HasColumnFProductName(row))
            {
                skippedMissingProductName++;
                continue;
            }

            var link = ExtractFirstColumnLink(row);
            if (!string.IsNullOrWhiteSpace(link))
            {
                items.Add(new SheetLinkItem(rowNumber, dict, link));
            }
            else
            {
                skippedMissingLink++;
            }
        }

        return new SheetLinkFetchResult(items, skippedMissingProductName, skippedMissingLink);
    }

    private static bool HasColumnFProductName(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object)
            return false;

        var index = 0;
        foreach (var prop in row.EnumerateObject())
        {
            index++;
            if (index != 6)
                continue;

            return !string.IsNullOrWhiteSpace(prop.Value.ToString());
        }

        return false;
    }

    private static string ExtractFirstColumnLink(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object)
            return "";

        foreach (var prop in row.EnumerateObject())
        {
            return NormalizeLink(prop.Value.ToString());
        }

        return "";
    }

    private static string NormalizeLink(string? value)
    {
        var trimmed = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return "";
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return trimmed;
        if (trimmed.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            return "https://" + trimmed;
        return trimmed;
    }

    private static async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await AppServices.DirectHttp.GetAsync(url, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"API lỗi {(int)response.StatusCode}: {text}");
            return JsonDocument.Parse(text);
        }
        catch (Exception ex) when (ApiServerHelper.IsConnectionRefused(ex))
        {
            throw new ApiNotRunningException(ApiServerHelper.ConnectionRefusedHelp, ex);
        }
    }

    private static async Task<string?> FindExtensionPopupDebuggerUrlAsync(
        int cdpPort,
        string extensionId,
        CancellationToken ct)
    {
        try
        {
            using var response = await AppServices.DirectHttp.GetAsync(
                $"http://127.0.0.1:{cdpPort}/json/list",
                ct);
            if (!response.IsSuccessStatusCode)
                return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var popupSuffix = $"chrome-extension://{extensionId}/popup.html";

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var url = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                if (!url.Equals(popupSuffix, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (item.TryGetProperty("webSocketDebuggerUrl", out var ws))
                    return ws.GetString();
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static async Task<string?> FindExtensionPopupTargetIdAsync(
        int cdpPort,
        string extensionId,
        CancellationToken ct)
    {
        try
        {
            using var response = await AppServices.DirectHttp.GetAsync(
                $"http://127.0.0.1:{cdpPort}/json/list", ct);
            if (!response.IsSuccessStatusCode)
                return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var popupSuffix = $"chrome-extension://{extensionId}/popup.html";

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var url = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                if (!url.Equals(popupSuffix, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (item.TryGetProperty("id", out var idEl))
                    return idEl.GetString();
            }
        }
        catch { }

        return null;
    }

    private static async Task<JsonElement> SendCdpAsync(
        ClientWebSocket socket,
        int id,
        string method,
        object? parameters,
        CancellationToken ct,
        string? sessionId = null)
    {
        string json;
        if (parameters is null)
        {
            json = sessionId is null
                ? JsonSerializer.Serialize(new { id, method })
                : JsonSerializer.Serialize(new { id, method, sessionId });
        }
        else
        {
            json = sessionId is null
                ? JsonSerializer.Serialize(new { id, method, @params = parameters })
                : JsonSerializer.Serialize(new { id, method, sessionId, @params = parameters });
        }

        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);

        var buffer = new byte[1024 * 512];
        while (true)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult recv;
            do
            {
                recv = await socket.ReceiveAsync(buffer, ct);
                if (recv.MessageType == WebSocketMessageType.Close)
                    throw new InvalidOperationException("CDP đóng khi gọi extension.");
                ms.Write(buffer, 0, recv.Count);
            } while (!recv.EndOfMessage);

            using var response = JsonDocument.Parse(ms.ToArray());
            var root = response.RootElement;
            if (!root.TryGetProperty("id", out var idProp) || idProp.GetInt32() != id)
                continue;

            if (root.TryGetProperty("error", out var err))
                throw new InvalidOperationException($"CDP: {err}");

            return root.TryGetProperty("result", out var result)
                ? result.Clone()
                : default;
        }
    }

    private static async Task<bool> IsCdpPortReachableAsync(int port, CancellationToken ct)
    {
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            var connectTask = tcp.ConnectAsync("127.0.0.1", port, ct).AsTask();
            return await Task.WhenAny(connectTask, Task.Delay(1500, ct)) == connectTask
                   && connectTask.IsCompletedSuccessfully;
        }
        catch
        {
            return false;
        }
    }
}
