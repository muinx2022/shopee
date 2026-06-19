using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShopeeStatApp.Services;

/// <summary>
/// Phân loại sản phẩm vào danh mục lá Shopee bằng OpenAI (chat completions). Gửi theo lô tên sản phẩm
/// + danh sách danh mục (đánh số), nhận về index danh mục cho từng sản phẩm.
/// </summary>
public sealed class CategoryAiUpdater
{
    private readonly string _apiKey;
    private readonly string _model;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(180) };

    public CategoryAiUpdater(string apiKey, string model = "gpt-4.1-mini")
    {
        _apiKey = apiKey;
        _model = model;
    }

    /// <summary>Phân loại 1 lô tên sản phẩm. Trả về mảng cùng độ dài <paramref name="names"/>:
    /// index danh mục trong <paramref name="categoryPaths"/>, hoặc -1 nếu không xác định.</summary>
    public async Task<int[]> ClassifyAsync(IReadOnlyList<string> names, IReadOnlyList<string> categoryPaths, CancellationToken ct)
    {
        var catSb = new StringBuilder();
        for (var i = 0; i < categoryPaths.Count; i++)
            catSb.Append(i).Append(": ").Append(categoryPaths[i]).Append('\n');

        var prodSb = new StringBuilder();
        for (var i = 0; i < names.Count; i++)
            prodSb.Append(i).Append(": ").Append((names[i] ?? "").Replace('\n', ' ').Replace('\r', ' ')).Append('\n');

        var sys =
            "Bạn là trợ lý phân loại sản phẩm trên sàn TMĐT Shopee. " +
            "Bạn nhận danh sách DANH MỤC (mỗi dòng dạng 'index: đường dẫn danh mục') và danh sách SẢN PHẨM (mỗi dòng 'index: tên'). " +
            "Với MỖI sản phẩm, hãy chọn ĐÚNG MỘT danh mục phù hợp nhất dựa trên TÊN sản phẩm, chỉ dùng index có trong danh sách danh mục. " +
            "Nếu không chắc, chọn danh mục gần đúng nhất. " +
            "Trả về JSON object: {\"r\":[{\"i\":<index sản phẩm>,\"c\":<index danh mục>}, ...]} cho TẤT CẢ sản phẩm.";
        var user = "DANH MỤC:\n" + catSb + "\nSẢN PHẨM:\n" + prodSb;

        var payload = new
        {
            model = _model,
            temperature = 0,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = sys },
                new { role = "user", content = user },
            },
        };

        var json = JsonSerializer.Serialize(payload);
        string body;
        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var resp = await Http.SendAsync(req, ct);
            body = await resp.Content.ReadAsStringAsync(ct);

            // 429 = vượt rate limit (TPM). Chờ đúng thời gian OpenAI gợi ý rồi thử lại.
            if ((int)resp.StatusCode == 429 && attempt < 8)
            {
                await Task.Delay(RetryDelay(resp, body, attempt), ct);
                continue;
            }
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"OpenAI lỗi {(int)resp.StatusCode}: {Trunc(body, 400)}");
            break;
        }

        using var doc = JsonDocument.Parse(body);
        var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "{}";

        var result = new int[names.Count];
        Array.Fill(result, -1);
        try
        {
            using var rd = JsonDocument.Parse(content);
            if (rd.RootElement.TryGetProperty("r", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in arr.EnumerateArray())
                {
                    var pi = e.TryGetProperty("i", out var iv) && iv.TryGetInt32(out var i2) ? i2 : -1;
                    var ci = e.TryGetProperty("c", out var cv) && cv.TryGetInt32(out var c2) ? c2 : -1;
                    if (pi >= 0 && pi < result.Length && ci >= 0 && ci < categoryPaths.Count)
                        result[pi] = ci;
                }
            }
        }
        catch { /* nội dung không phải JSON hợp lệ → giữ -1 */ }
        return result;
    }

    /// <summary>Phân loại TẤT CẢ tên sản phẩm theo lô + chạy song song (cho file lớn). Trả về mảng
    /// đường dẫn danh mục (chuỗi) cùng độ dài names; "" nếu không xác định. <paramref name="onProgress"/>
    /// nhận số dòng đã xong (gọi từ thread nền — caller tự marshal về UI nếu cần).</summary>
    public async Task<string[]> ClassifyAllAsync(
        IReadOnlyList<string> names, IReadOnlyList<string> categoryPaths,
        int batchSize, int maxParallel, Action<int>? onProgress, CancellationToken ct)
    {
        var result = new string[names.Count];
        Array.Fill(result, "");
        var batches = new List<(int Start, int Len)>();
        for (var s = 0; s < names.Count; s += batchSize)
            batches.Add((s, Math.Min(batchSize, names.Count - s)));

        using var sem = new SemaphoreSlim(Math.Max(1, maxParallel));
        var done = 0;
        var tasks = batches.Select(async b =>
        {
            await sem.WaitAsync(ct);
            try
            {
                var slice = new List<string>(b.Len);
                for (var k = 0; k < b.Len; k++) slice.Add(names[b.Start + k]);
                var idx = await ClassifyAsync(slice, categoryPaths, ct);
                for (var k = 0; k < b.Len; k++)
                {
                    var ci = idx[k];
                    if (ci >= 0 && ci < categoryPaths.Count) result[b.Start + k] = categoryPaths[ci];
                }
            }
            finally
            {
                sem.Release();
                onProgress?.Invoke(Interlocked.Add(ref done, b.Len));
            }
        }).ToList();

        await Task.WhenAll(tasks);
        return result;
    }

    /// <summary>Đọc API key từ file (bỏ khoảng trắng/ngoặc kép thừa).</summary>
    public static string ReadKey(string keyPath) =>
        File.ReadAllText(keyPath).Trim().Trim('"').Trim();

    // Thời gian chờ trước khi thử lại 429: ưu tiên header Retry-After, rồi "try again in Xs" trong body,
    // cuối cùng là backoff lũy thừa (tối đa 30s).
    private static TimeSpan RetryDelay(HttpResponseMessage resp, string body, int attempt)
    {
        if (resp.Headers.RetryAfter?.Delta is { } d && d > TimeSpan.Zero)
            return d + TimeSpan.FromMilliseconds(300);
        var m = Regex.Match(body, @"try again in ([\d.]+)\s*s", RegexOptions.IgnoreCase);
        if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var secs))
            return TimeSpan.FromSeconds(secs + 0.6);
        return TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)));
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
