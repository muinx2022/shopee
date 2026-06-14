using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace OpenMultiBraveLauncherV3;

internal static class PageCdpHelper
{
    private const string CollectVideoCandidatesJs = """
        (async () => {
          const seen = new Set();
          const candidates = [];
          const normalizeUrl = (value) => {
            if (typeof value !== "string") return "";
            const trimmed = value.trim();
            return /^https?:\/\//i.test(trimmed) ? trimmed : "";
          };
          const addUrl = (url, duration, label) => {
            const u = normalizeUrl(url);
            if (!u || seen.has(u)) return;
            seen.add(u);
            candidates.push({ url: u, duration: Number.isFinite(duration) ? duration : null, label: label || "" });
          };
          const waitForDuration = async (video) => {
            const currentDuration = Number(video.duration);
            if (Number.isFinite(currentDuration) && currentDuration > 0) return currentDuration;
            return await new Promise((resolve) => {
              let done = false;
              const finish = () => {
                if (done) return;
                done = true;
                const duration = Number(video.duration);
                resolve(Number.isFinite(duration) && duration > 0 ? duration : null);
              };
              const timer = setTimeout(finish, 2000);
              const handler = () => { clearTimeout(timer); finish(); };
              try {
                video.addEventListener("loadedmetadata", handler, { once: true });
                video.addEventListener("durationchange", handler, { once: true });
                if (video.readyState >= 1) finish();
              } catch (_) {
                clearTimeout(timer);
                finish();
              }
            });
          };
          for (const video of Array.from(document.querySelectorAll("video"))) {
            const urls = new Set();
            for (const value of [
              video.currentSrc, video.src, video.getAttribute("src"),
              video.dataset?.src, video.dataset?.videoSrc,
            ]) {
              const url = normalizeUrl(value);
              if (url) urls.add(url);
            }
            for (const source of video.querySelectorAll("source")) {
              const url = normalizeUrl(source.src || source.getAttribute("src"));
              if (url) urls.add(url);
            }
            const duration = await waitForDuration(video);
            for (const url of urls) {
              addUrl(url, duration, video.getAttribute("aria-label") || video.getAttribute("title") || "");
            }
          }

          // Fallback 1: lấy từ Performance entries (mp4/m3u8)
          try {
            const entries = performance.getEntriesByType("resource") || [];
            for (const e of entries) {
              const name = e && typeof e.name === "string" ? e.name : "";
              if (!name) continue;
              if (/\.mp4(\?|#|$)/i.test(name) || /\.m3u8(\?|#|$)/i.test(name) || /video/i.test(name)) {
                addUrl(name, null, "perf");
              }
            }
          } catch (_) {}

          // Fallback 2: quét script JSON/text để tìm mp4/m3u8 (Shopee thường nhúng URL video)
          try {
            const scripts = Array.from(document.scripts || []);
            const rx = /(https?:\/\/[^\s"'\\]+?\.(?:mp4|m3u8)(?:\?[^\s"'\\]*)?)/ig;
            for (const s of scripts) {
              const text = (s && (s.textContent || s.innerText)) || "";
              if (!text || text.length < 20) continue;
              let m;
              while ((m = rx.exec(text)) !== null) addUrl(m[1], null, "script");
            }
          } catch (_) {}

          return candidates;
        })()
        """;

    public static async Task<List<VideoCandidate>> CollectVideoCandidatesAsync(
        int cdpPort,
        string pageUrlHint,
        CancellationToken cancellationToken = default)
    {
        var result = await EvaluateOnPageAsync(cdpPort, pageUrlHint, CollectVideoCandidatesJs, cancellationToken);
        if (result is null || result.Value.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<VideoCandidate>();
        foreach (var item in result.Value.EnumerateArray())
        {
            if (!item.TryGetProperty("url", out var urlEl))
                continue;
            var url = urlEl.GetString();
            if (string.IsNullOrWhiteSpace(url))
                continue;

            double? duration = null;
            if (item.TryGetProperty("duration", out var durEl) && durEl.ValueKind == JsonValueKind.Number)
                duration = durEl.GetDouble();

            var label = item.TryGetProperty("label", out var labelEl) ? labelEl.GetString() : "";
            list.Add(new VideoCandidate(url, duration, label ?? ""));
        }

        return list;
    }

    private static async Task<JsonElement?> EvaluateOnPageAsync(
        int cdpPort,
        string pageUrlHint,
        string expression,
        CancellationToken cancellationToken)
    {
        using var response = await AppServices.DirectHttp.GetAsync(
            $"http://127.0.0.1:{cdpPort}/json/list",
            cancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var hint = (pageUrlHint ?? "").Trim();

        var pages = new List<(string url, string wsUrl)>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var type = item.TryGetProperty("type", out var t) ? t.GetString() : "";
            if (!string.Equals(type, "page", StringComparison.OrdinalIgnoreCase))
                continue;

            var url = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(url))
                continue;
            if (url.StartsWith("chrome://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!item.TryGetProperty("webSocketDebuggerUrl", out var ws))
                continue;
            var wsUrl = ws.GetString();
            if (string.IsNullOrWhiteSpace(wsUrl))
                continue;
            pages.Add((url, wsUrl));
        }

        if (pages.Count == 0)
            return null;

        static bool UrlLooksLikeHint(string url, string hint)
        {
            if (string.IsNullOrWhiteSpace(hint)) return true;
            if (url.Contains(hint, StringComparison.OrdinalIgnoreCase)) return true;
            if (hint.Contains(url.Split('?')[0], StringComparison.OrdinalIgnoreCase)) return true;
            try
            {
                var u1 = new Uri(url);
                var u2 = new Uri(hint);
                if (!string.Equals(u1.Host, u2.Host, StringComparison.OrdinalIgnoreCase)) return false;
                // cùng host → ưu tiên
                return true;
            }
            catch
            {
                return false;
            }
        }

        // Uu ti�n nh?ng page match hint tru?c; n?u v?n r?ng s? th? h?t c�c page c�n l?i.
        var ordered = pages
            .OrderByDescending(p => UrlLooksLikeHint(p.url, hint))
            .ThenByDescending(p => p.url.Contains("shopee", StringComparison.OrdinalIgnoreCase))
            .ToList();

        JsonElement? best = null;
        var bestCount = -1;

        foreach (var (_, wsUrl) in ordered)
        {
            try
            {
                using var socket = new ClientWebSocket();
                await socket.ConnectAsync(new Uri(wsUrl), cancellationToken);
                var evalResult = await SendCdpAsync(socket, 1, "Runtime.evaluate", new
                {
                    expression,
                    awaitPromise = true,
                    returnByValue = true,
                }, cancellationToken);

                if (!evalResult.TryGetProperty("result", out var res) ||
                    !res.TryGetProperty("value", out var val))
                    continue;

                var cloned = val.Clone();
                var count = cloned.ValueKind == JsonValueKind.Array ? cloned.GetArrayLength() : 0;
                if (count > bestCount)
                {
                    best = cloned;
                    bestCount = count;
                    if (bestCount > 0)
                        break; // có candidate → khỏi thử tiếp
                }
            }
            catch
            {
                // ignore từng page
            }
        }

        return best;
    }

    private static async Task<JsonElement> SendCdpAsync(
        ClientWebSocket socket,
        int id,
        string method,
        object? parameters,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(
            parameters is null
                ? JsonSerializer.Serialize(new { id, method })
                : JsonSerializer.Serialize(new { id, method, @params = parameters }));
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);

        var buffer = new byte[1024 * 512];
        while (true)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult recv;
            do
            {
                recv = await socket.ReceiveAsync(buffer, cancellationToken);
                if (recv.MessageType == WebSocketMessageType.Close)
                    throw new InvalidOperationException("CDP page đóng.");
                ms.Write(buffer, 0, recv.Count);
            } while (!recv.EndOfMessage);

            using var response = JsonDocument.Parse(ms.ToArray());
            var root = response.RootElement;
            if (!root.TryGetProperty("id", out var idProp) || idProp.GetInt32() != id)
                continue;

            if (root.TryGetProperty("error", out var err))
                throw new InvalidOperationException($"CDP page: {err}");

            return root.TryGetProperty("result", out var result)
                ? result.Clone()
                : default;
        }
    }
}

internal sealed record VideoCandidate(string Url, double? Duration, string Label);
