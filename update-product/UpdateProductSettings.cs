using System.Text.Json;
using Microsoft.Win32;

namespace UpdateProduct;

internal static class UpdateProductSettings
{
    private const string FileName = "update-product-settings.json";
    private static readonly object SaveLock = new();

    public static string SettingsPath => Path.Combine(AppSession.ProjectSourceDirectory, FileName);

    public static UpdateProductSettingsFile LoadOrCreate()
    {
        if (File.Exists(SettingsPath))
        {
            try
            {
                var data = JsonSerializer.Deserialize<UpdateProductSettingsFile>(File.ReadAllText(SettingsPath));
                if (data is not null)
                {
                    Normalize(data);
                    Save(data);
                    return data;
                }
            }
            catch (Exception ex)
            {
                AppDiagnostics.LogException("UpdateProductSettings.LoadOrCreate", ex);
            }
        }

        var settings = new UpdateProductSettingsFile();
        Normalize(settings);
        Save(settings);
        return settings;
    }

    public static void Save(UpdateProductSettingsFile settings)
    {
        lock (SaveLock)
        {
            Normalize(settings);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath) ?? AppSession.ProjectSourceDirectory);
            var tmp = $"{SettingsPath}.{Environment.ProcessId}.tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, SettingsPath, overwrite: true);
        }
    }

    public static void Normalize(UpdateProductSettingsFile settings)
    {
        if (string.IsNullOrWhiteSpace(settings.BraveExe))
            settings.BraveExe = DetectBraveExe()?.FullName ?? "";
        if (string.IsNullOrWhiteSpace(settings.SourceUserData))
            settings.SourceUserData = DetectBraveUserData()?.FullName ?? "";
        if (string.IsNullOrWhiteSpace(settings.OpenAiApiKeyFile))
            settings.OpenAiApiKeyFile = Path.Combine(AppSession.RepoRootDirectory, "openai.key");

        settings.Accounts ??= [];
        if (settings.Accounts.Count == 0)
            settings.Accounts.Add(BigSellerAccountConfig.CreateDefault());

        foreach (var account in settings.Accounts)
        {
            if (string.IsNullOrWhiteSpace(account.Id))
                account.Id = Guid.NewGuid().ToString("N");
            account.Shops ??= [];
            if (account.Shops.Count == 0)
                account.Shops.Add(ShopConfig.CreateDefault());
            if (string.IsNullOrWhiteSpace(account.BigSellerCookieFile))
                account.BigSellerCookieFile = ResolveAccountCookieFile(account);

            foreach (var shop in account.Shops)
            {
                if (string.IsNullOrWhiteSpace(shop.Id))
                    shop.Id = Guid.NewGuid().ToString("N");
                shop.UseSharedProfiles = true;
                shop.BigSellerStartRow = Math.Max(2, shop.BigSellerStartRow);
                shop.BigSellerEndRow = Math.Max(0, shop.BigSellerEndRow);
                shop.BigSellerImportMaxProcess = Math.Clamp(shop.BigSellerImportMaxProcess, 1, 10);
                shop.BigSellerUpdateMaxProcess = Math.Clamp(shop.BigSellerUpdateMaxProcess, 1, 10);
                shop.BigSellerListingReloadSeconds = Math.Clamp(shop.BigSellerListingReloadSeconds, 3, 600);
                shop.OpenAiBatchSize = Math.Clamp(shop.OpenAiBatchSize, 1, 500);
            }
        }

        var activeAccount = settings.Accounts.FirstOrDefault(a => a.Id == settings.ActiveAccountId) ?? settings.Accounts[0];
        settings.ActiveAccountId = activeAccount.Id;
        var activeShop = activeAccount.Shops.FirstOrDefault(s => s.Id == settings.ActiveShopId) ?? activeAccount.Shops[0];
        settings.ActiveShopId = activeShop.Id;
    }

    public static string ResolveAccountCookieFile(BigSellerAccountConfig account) =>
        AppSession.ResolvePersistentDataPath("account-cookies", $"{account.Id}-bigseller.json");

    private static FileInfo? DetectBraveExe()
    {
        var candidates = new List<string>();
        foreach (var folder in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        })
        {
            if (!string.IsNullOrWhiteSpace(folder))
                candidates.Add(Path.Combine(folder, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"));
        }

        foreach (var (root, sub) in new[]
        {
            (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\App Paths\brave.exe"),
            (Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\App Paths\brave.exe"),
        })
        {
            try
            {
                using var key = root.OpenSubKey(sub);
                var value = key?.GetValue(string.Empty)?.ToString();
                if (!string.IsNullOrWhiteSpace(value))
                    candidates.Add(value);
            }
            catch { }
        }

        foreach (var candidate in candidates)
            if (File.Exists(candidate))
                return new FileInfo(candidate);

        return null;
    }

    private static DirectoryInfo? DetectBraveUserData()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
            return null;

        var path = Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data");
        return Directory.Exists(path) ? new DirectoryInfo(path) : null;
    }
}
