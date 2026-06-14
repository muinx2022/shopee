using System.Text.RegularExpressions;

namespace OpenMultiBraveLauncherV3;

internal static class ShopeeImportService
{
    public static List<string> SplitImportLines(string text) =>
        (text ?? "")
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(line => !string.IsNullOrWhiteSpace(line))
        .ToList();

    public static List<string> SplitShopeeImportLines(string text)
    {
        var lines = SplitImportLines(text);
        if (lines.Count > 1)
            return lines;

        var matches = Regex.Matches(
            text ?? "",
            @"[^\s|]+\|[^\s|]+\|\.shopee\.vn=SPC_F=[^\s|]+",
            RegexOptions.IgnoreCase);
        return matches.Count > 0
            ? matches.Select(m => m.Value.Trim()).ToList()
            : lines;
    }

    public static void ApplyProxyImport(
        IReadOnlyList<InstanceEntry> targets,
        IReadOnlyList<string> proxyKeys,
        Action<string> refreshListItem)
    {
        for (var i = 0; i < targets.Count; i++)
        {
            targets[i].Config.KiotProxyKey = proxyKeys[i % proxyKeys.Count].Trim();
            targets[i].Config.ManualProxy = "";
            targets[i].Session.ApplyConfig(targets[i].Config);
            refreshListItem(targets[i].Config.Id);
        }
    }
}
