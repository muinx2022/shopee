namespace CheckShopeeAccount;

/// <summary>Kết quả lấy proxy từ kiotproxy.</summary>
public readonly record struct KiotResult(string? Proxy, string? Ip, long NextChangeAtMs, string? Error);

/// <summary>
/// Lấy proxy từ kiotproxy. <see cref="FetchNewAsync"/> gọi /proxies/new để xoay IP — chỉ thực
/// sự đổi IP khi đã qua mốc <c>nextRequestAt</c> của key (ProxyPool tự gate theo mốc này).
/// </summary>
public static class KiotProxyClient
{
    public static async Task<KiotResult> FetchNewAsync(
        string key, CancellationToken ct = default, string proxyType = "http")
    {
        if (string.IsNullOrWhiteSpace(key))
            return new KiotResult(null, null, 0, "key rỗng");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        var url = $"https://api.kiotproxy.com/api/v1/proxies/new?key={Uri.EscapeDataString(key)}&region=random";
        return await TryFetchAsync(http, url, proxyType, ct);
    }

    /// <summary>Lấy proxy đang gán với key (không xoay) — dùng khi key chưa tới giờ đổi.</summary>
    public static async Task<KiotResult> FetchCurrentAsync(
        string key, CancellationToken ct = default, string proxyType = "http")
    {
        if (string.IsNullOrWhiteSpace(key))
            return new KiotResult(null, null, 0, "key rỗng");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        var url = $"https://api.kiotproxy.com/api/v1/proxies/current?key={Uri.EscapeDataString(key)}";
        return await TryFetchAsync(http, url, proxyType, ct);
    }

    private static async Task<KiotResult> TryFetchAsync(
        HttpClient http, string url, string proxyType, CancellationToken ct)
    {
        string response;
        try
        {
            response = await http.GetStringAsync(url, ct);
        }
        catch (Exception ex)
        {
            return new KiotResult(null, null, 0, ex.Message);
        }

        try
        {
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;
            if (root.TryGetProperty("success", out var successProp) && !successProp.GetBoolean())
            {
                var message = root.TryGetProperty("message", out var msg) ? msg.GetString() : null;
                return new KiotResult(null, null, 0, message ?? "kiotproxy success=false.");
            }

            var data = root.TryGetProperty("data", out var dataProp) ? dataProp : root;
            var nextChangeAt = data.TryGetProperty("nextRequestAt", out var nr) && nr.TryGetInt64(out var n) ? n : 0;
            var ip = data.TryGetProperty("realIpAddress", out var ipEl) ? ipEl.GetString() : null;

            var fieldName = string.Equals(proxyType, "socks5", StringComparison.OrdinalIgnoreCase) ? "socks5" : "http";
            if (data.TryGetProperty(fieldName, out var proxyProp))
            {
                var proxy = proxyProp.GetString();
                if (!string.IsNullOrWhiteSpace(proxy))
                {
                    ip ??= proxy.Split(':')[0];
                    return new KiotResult($"{fieldName}://{proxy}", ip, nextChangeAt, null);
                }
            }

            if (!data.TryGetProperty("host", out var hostProp))
                return new KiotResult(null, null, nextChangeAt, "kiotproxy không trả về host/proxy.");
            var host = hostProp.GetString();
            if (string.IsNullOrWhiteSpace(host))
                return new KiotResult(null, null, nextChangeAt, "kiotproxy trả về host rỗng.");

            var portField = string.Equals(fieldName, "socks5", StringComparison.OrdinalIgnoreCase) ? "socks5Port" : "httpPort";
            if (!data.TryGetProperty(portField, out var portProp))
                return new KiotResult(null, null, nextChangeAt, $"kiotproxy không trả về {portField}.");

            ip ??= host;
            return new KiotResult($"{fieldName}://{host}:{portProp.GetInt32()}", ip, nextChangeAt, null);
        }
        catch (JsonException)
        {
            return new KiotResult(null, null, 0, response);
        }
    }
}
