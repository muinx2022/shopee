using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenMultiBraveLauncherV3;

/// <summary>Đọc runnerState của extension Shopee Data Runner từ LevelDB profile Brave.</summary>
internal static class ExtensionProgressReader
{
    private const int TailSampleBytes = 2 * 1024 * 1024;

    private static readonly Regex LastCompletedRowRx =
        new(@"lastCompletedRow[^\d]{0,12}(\d+)", RegexOptions.Compiled);

    private static readonly Regex StartRowRx =
        new(@"startRow[^\d]{0,12}(\d+)", RegexOptions.Compiled);

    private static readonly Regex EndRowRx =
        new(@"endRow[^\d]{0,12}(\d+)", RegexOptions.Compiled);

    private static readonly Regex CurrentRowRx =
        new(@"currentRow[^\d]{0,12}(\d+)", RegexOptions.Compiled);

    private static readonly Regex SheetNameRx =
        new(@"(?:lastSheetName|sheetName)[^\w""]{0,12}""?([a-zA-Z0-9_\s\u00C0-\u024F]+)", RegexOptions.Compiled);

    private static readonly Regex LastSkuRx =
        new(@"lastSku[^\w""]{0,12}""?([A-Z0-9]+)", RegexOptions.Compiled);

    private static readonly Regex PhaseRx =
        new(@"phase[^\w""]{0,12}""?([a-z]+)", RegexOptions.Compiled);

    private static readonly Regex RunningRx =
        new(@"running[^\w]{0,10}(true|false)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LastMessageRx =
        new(@"lastMessage[^\w""]{0,16}""([^""]{8,200})", RegexOptions.Compiled);

    public static string? TryGetRunnerExtensionId(DirectoryInfo profileRoot)
    {
        if (!TryReadBestFromProfile(profileRoot, out _, out var extensionId))
            return null;
        return extensionId;
    }

    public static bool TryRead(DirectoryInfo profileRoot, out ExtensionRunnerState state) =>
        TryReadBestFromProfile(profileRoot, out state, out _);

    private static bool TryReadBestFromProfile(
        DirectoryInfo profileRoot,
        out ExtensionRunnerState state,
        out string? extensionId)
    {
        state = new ExtensionRunnerState();
        extensionId = null;

        var settingsRoot = Path.Combine(profileRoot.FullName, "Default", "Local Extension Settings");
        if (!Directory.Exists(settingsRoot))
            return false;

        ExtensionRunnerState? bestState = null;
        string? bestExtensionId = null;
        DateTime bestTime = DateTime.MinValue;

        foreach (var extDir in Directory.EnumerateDirectories(settingsRoot))
        {
            if (!TryReadRunnerStateFromExtensionDir(extDir, out var candidate, out var latestWrite))
                continue;

            if (bestState is null ||
                latestWrite > bestTime ||
                (latestWrite == bestTime && IsBetterRunnerState(candidate, bestState)))
            {
                bestState = candidate;
                bestExtensionId = Path.GetFileName(extDir);
                bestTime = latestWrite;
            }
        }

        if (bestState is null)
            return false;

        state = bestState;
        extensionId = bestExtensionId;
        return state.LastCompletedRow > 0 ||
               state.StartRow > 0 ||
               state.CurrentRow > 0 ||
               !string.IsNullOrWhiteSpace(state.SheetName);
    }

    private static bool TryReadRunnerStateFromExtensionDir(
        string extDir,
        out ExtensionRunnerState state,
        out DateTime latestWriteUtc)
    {
        state = new ExtensionRunnerState();
        latestWriteUtc = DateTime.MinValue;

        var storageFiles = Directory.EnumerateFiles(extDir)
            .Where(f =>
                f.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".ldb", StringComparison.OrdinalIgnoreCase))
            .Select(f => new FileInfo(f))
            .Where(f => f.Length >= 128)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();

        if (storageFiles.Count == 0)
            return false;

        ExtensionRunnerState? best = null;
        foreach (var file in storageFiles)
        {
            if (file.LastWriteTimeUtc > latestWriteUtc)
                latestWriteUtc = file.LastWriteTimeUtc;

            var text = TryReadStorageTail(file.FullName);
            if (text is null)
                continue;

            if (!text.Contains("runnerState", StringComparison.Ordinal) &&
                !text.Contains("lastCompletedRow", StringComparison.Ordinal) &&
                !text.Contains("lastRunConfig", StringComparison.Ordinal) &&
                !text.Contains("sheetName", StringComparison.Ordinal))
                continue;

            var parsed = ParseState(ExtractLatestRunnerSlice(text));
            if (best is null || IsBetterRunnerState(parsed, best))
                best = parsed;
        }

        if (best is null)
            return false;

        state = best;
        return true;
    }

    /// <summary>LevelDB ghi mới ở cuối file — chỉ parse đoạn sau runnerState cuối cùng.</summary>
    private static string ExtractLatestRunnerSlice(string text)
    {
        var marker = "runnerState";
        var idx = text.LastIndexOf(marker, StringComparison.Ordinal);
        if (idx < 0)
            return text;

        var slice = text[idx..Math.Min(text.Length, idx + 16_384)];

        // Thử parse JSON runnerState trực tiếp nếu có
        var jsonStart = slice.IndexOf('{');
        if (jsonStart >= 0)
        {
            var json = TryExtractJsonObject(slice, jsonStart);
            if (json is not null)
                return json;
        }

        return slice;
    }

    private static string? TryExtractJsonObject(string text, int start)
    {
        var depth = 0;
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    var candidate = text[start..(i + 1)];
                    try
                    {
                        using var doc = JsonDocument.Parse(candidate);
                        if (doc.RootElement.ValueKind == JsonValueKind.Object)
                            return candidate;
                    }
                    catch
                    {
                        return null;
                    }
                }
            }
        }

        return null;
    }

    private static ExtensionRunnerState ParseState(string text)
    {
        if (text.TrimStart().StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                return new ExtensionRunnerState
                {
                    SheetName = GetStringProp(root, "sheetName", "lastSheetName"),
                    StartRow = GetIntProp(root, "startRow"),
                    EndRow = GetIntProp(root, "endRow"),
                    LastCompletedRow = GetIntProp(root, "lastCompletedRow"),
                    CurrentRow = GetIntProp(root, "currentRow"),
                    LastSku = GetStringProp(root, "lastSku"),
                    Phase = GetStringProp(root, "phase"),
                    Running = GetBoolProp(root, "running"),
                    LastMessage = GetStringProp(root, "lastMessage"),
                };
            }
            catch
            {
                // fallback regex
            }
        }

        return new ExtensionRunnerState
        {
            SheetName = LastMatch(SheetNameRx, text),
            StartRow = LastInt(StartRowRx, text),
            EndRow = LastInt(EndRowRx, text),
            LastCompletedRow = LastInt(LastCompletedRowRx, text),
            CurrentRow = LastInt(CurrentRowRx, text),
            LastSku = LastMatch(LastSkuRx, text),
            Phase = LastMatch(PhaseRx, text),
            Running = LastBool(RunningRx, text),
            LastMessage = LastMatch(LastMessageRx, text),
        };
    }

    private static string? GetStringProp(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(s))
                    return s;
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

    private static bool IsBetterRunnerState(ExtensionRunnerState candidate, ExtensionRunnerState current)
    {
        var cLast = candidate.LastCompletedRow ?? 0;
        var curLast = current.LastCompletedRow ?? 0;
        if (cLast != curLast)
            return cLast > curLast;

        var cStart = candidate.StartRow ?? 0;
        var curStart = current.StartRow ?? 0;
        if (cStart != curStart)
            return cStart > curStart;

        var cCur = candidate.CurrentRow ?? 0;
        var curCur = current.CurrentRow ?? 0;
        return cCur > curCur;
    }

    private static string? TryReadStorageTail(string filePath)
    {
        var text = ReadTailFromFile(filePath);
        if (text is not null)
            return text;

        var tempCopy = TryCopyToTemp(filePath);
        if (tempCopy is null)
            return null;

        try
        {
            return ReadTailFromFile(tempCopy);
        }
        finally
        {
            try { File.Delete(tempCopy); } catch { }
        }
    }

    private static string? ReadTailFromFile(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (info.Length < 64)
                return null;

            var toRead = (int)Math.Min(info.Length, TailSampleBytes);
            var buffer = new byte[toRead];
            using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(-toRead, SeekOrigin.End);
            var read = stream.Read(buffer, 0, toRead);
            if (read <= 0)
                return null;
            return Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryCopyToTemp(string filePath)
    {
        try
        {
            var temp = Path.Combine(Path.GetTempPath(), $"mbr-{Guid.NewGuid():N}.ldb");
            File.Copy(filePath, temp, overwrite: true);
            return temp;
        }
        catch
        {
            return null;
        }
    }

    private static int? LastInt(Regex rx, string text)
    {
        var matches = rx.Matches(text);
        if (matches.Count == 0) return null;
        var val = matches[^1].Groups[1].Value;
        return int.TryParse(val, out var n) && n > 0 ? n : null;
    }

    private static string? LastMatch(Regex rx, string text)
    {
        var matches = rx.Matches(text);
        if (matches.Count == 0) return null;
        var val = matches[^1].Groups[1].Value.Trim().Trim('"');
        return string.IsNullOrWhiteSpace(val) ? null : val;
    }

    private static bool? LastBool(Regex rx, string text)
    {
        var matches = rx.Matches(text);
        if (matches.Count == 0) return null;
        return matches[^1].Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
    }
}
