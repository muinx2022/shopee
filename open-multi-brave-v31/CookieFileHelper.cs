using System.Text;
using System.Text.Json;

namespace OpenMultiBraveLauncherV3;

internal static class CookieFileHelper
{
    public static JsonElement ParseCookiesRoot(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return root.TryGetProperty("cookies", out var cp) ? cp.Clone() : root.Clone();
    }

    public static async Task<JsonElement> ParseCookiesRootFromFileAsync(
        string cookieFile,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(cookieFile);
        using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), detectEncodingFromByteOrderMarks: true);
        var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        return ParseCookiesRoot(json);
    }

    public static void ValidateCookiesArray(JsonElement cookiesEl)
    {
        if (cookiesEl.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("File cookie không hợp lệ.");
    }

    public static bool TryWriteCookieFile(
        string cookieFile,
        IReadOnlyCollection<Dictionary<string, object?>> cookies,
        Action<string>? log = null)
    {
        var tmp = $"{cookieFile}.{Environment.ProcessId}-{Guid.NewGuid():N}.tmp";
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(cookieFile));
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(
                new { exportedAt = DateTimeOffset.Now, cookies },
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(tmp, json);

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(tmp, cookieFile, overwrite: true);
                    return true;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(150);
                }
            }
        }
        catch (Exception ex)
        {
            try { File.Delete(tmp); } catch { }
            log?.Invoke($"Cookie: khong luu duoc cookie ra file: {ex.Message}");
            return false;
        }
    }
}
