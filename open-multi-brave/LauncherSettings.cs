using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace OpenMultiBraveLauncher;

internal static class LauncherSettings
{
    private const string FileName = "proxy-keys.json";

    public static string SettingsPath =>
        Path.Combine(AppContext.BaseDirectory, FileName);

    public static IReadOnlyList<string> LoadProxyKeys()
    {
        var path = SettingsPath;
        if (!File.Exists(path))
            return [];

        try
        {
            var json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<ProxyKeysFile>(json);
            if (data?.Keys is null || data.Keys.Count == 0)
                return [];

            var keys = new List<string>();
            foreach (var k in data.Keys)
                keys.Add(k?.Trim() ?? "");
            return keys;
        }
        catch
        {
            return [];
        }
    }

    public static void SaveProxyKeys(IReadOnlyList<string> keys)
    {
        var list = keys.Select(k => k.Trim()).ToList();
        var payload = new ProxyKeysFile { Keys = list };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }

    private sealed class ProxyKeysFile
    {
        public List<string> Keys { get; set; } = [];
    }
}
