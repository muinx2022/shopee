namespace ShopeeStatApp.Services;

public sealed class AppSettingsService
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _settingsPath;
    private readonly string _apiConfigPath;
    // Parallel auto-run logs in concurrently (login write-back), so serialize disk writes.
    private readonly object _saveLock = new();

    public LauncherSettings Settings { get; private set; } = new();
    public ShopeeApiConfig ApiConfig { get; private set; } = new();

    public AppSettingsService()
    {
        var appDir = Path.GetDirectoryName(Application.ExecutablePath) ?? ".";
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ShopeeStatApp");
        Directory.CreateDirectory(dataDir);
        _settingsPath = Path.Combine(dataDir, "settings.json");
        _apiConfigPath = Path.Combine(appDir, "appsettings.json");
    }

    public void Load()
    {
        if (File.Exists(_settingsPath))
        {
            try { Settings = JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(_settingsPath), Opts) ?? new(); }
            catch { Settings = new(); }
        }

        if (File.Exists(_apiConfigPath))
        {
            try { ApiConfig = JsonSerializer.Deserialize<ShopeeApiConfig>(File.ReadAllText(_apiConfigPath), Opts) ?? new(); }
            catch { ApiConfig = new(); }
        }

        NormalizeSettings();
    }

    public void SaveSettings()
    {
        lock (_saveLock)
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(Settings, Opts), Encoding.UTF8);
    }

    public void SaveApiConfig(string json)
    {
        File.WriteAllText(_apiConfigPath, json, Encoding.UTF8);
        try { ApiConfig = JsonSerializer.Deserialize<ShopeeApiConfig>(json, Opts) ?? new(); }
        catch { }
    }

    public string GetApiConfigJson() =>
        JsonSerializer.Serialize(ApiConfig, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

    public string GetProfileDir(InstanceConfig config)
    {
        config.EnsureProfileRelativePath();
        var dir = Path.GetFullPath(config.ProfileRelativePath);
        Directory.CreateDirectory(Path.Combine(dir, "Default"));
        return dir;
    }

    private void NormalizeSettings()
    {
        Settings.Keywords = (Settings.Keywords ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (Settings.Keywords.Count == 0)
            Settings.Keywords.Add("giày nữ");

        Settings.UsedKeywords = (Settings.UsedKeywords ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(x => Settings.Keywords.Any(k => string.Equals(k, x, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }
}
