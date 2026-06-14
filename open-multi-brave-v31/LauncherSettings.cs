using System.Text.Json;

namespace OpenMultiBraveLauncherV3;

internal static class LauncherSettings
{
    private const string FileName = "launcher-settings.json";
    private static readonly object SaveLock = new();

    public static string SettingsPath =>
        Path.Combine(AppSession.RootDirectory, FileName);

    private static string TemplateSettingsPath =>
        Path.Combine(AppSession.ProjectSourceDirectory, FileName);

    private static string LegacyTemplateSettingsPath =>
        Path.Combine(AppSession.BaseDirectory, FileName);

    public static LauncherSettingsFile LoadOrCreate()
    {
        var loadPath = File.Exists(SettingsPath)
            ? SettingsPath
            : File.Exists(TemplateSettingsPath)
                ? TemplateSettingsPath
                : File.Exists(LegacyTemplateSettingsPath)
                    ? LegacyTemplateSettingsPath
                    : "";

        if (!string.IsNullOrWhiteSpace(loadPath))
        {
            try
            {
                var json = File.ReadAllText(loadPath);
                var data = JsonSerializer.Deserialize<LauncherSettingsFile>(json);
                if (data is not null)
                {
                    NormalizeAccounts(data);
                    IsolateAccountCookieFiles(data);
                    ResetSessionScopedPorts(data);
                    NormalizeRangeSettings(data);
                    foreach (var inst in data.Instances)
                    {
                        inst.EnsureProfileRelativePath();
                        EnsureInstanceShop(data, inst);
                    }
                    return data;
                }
            }
            catch { }
        }

        var imported = TryImportFromV1();
        if (imported is not null)
        {
            NormalizeAccounts(imported);
            IsolateAccountCookieFiles(imported);
            ResetSessionScopedPorts(imported);
            foreach (var inst in imported.Instances)
                EnsureInstanceShop(imported, inst);
            return imported;
        }

        var settings = new LauncherSettingsFile
        {
            Instances = [InstanceConfig.CreateNew(1)],
        };
        NormalizeAccounts(settings);
        IsolateAccountCookieFiles(settings);
        ResetSessionScopedPorts(settings);
        foreach (var inst in settings.Instances)
            EnsureInstanceShop(settings, inst);
        return settings;
    }

    public static void Save(LauncherSettingsFile settings)
    {
        lock (SaveLock)
        {
            NormalizeAccounts(settings);
            NormalizeRangeSettings(settings);
            foreach (var inst in settings.Instances)
            {
                inst.EnsureProfileRelativePath();
                EnsureInstanceShop(settings, inst);
            }

            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            WriteAtomic(SettingsPath, json);
            WriteAtomic(TemplateSettingsPath, json);
        }
    }

    /// <summary>
    /// Ghi qua file tạm rồi move — file settings chứa cấu hình TẤT CẢ account,
    /// crash giữa lúc ghi thẳng sẽ làm hỏng toàn bộ.
    /// </summary>
    private static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppSession.RootDirectory);
        var tmp = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(tmp, content);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(tmp, path, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(100);
            }
        }
    }

    public static LauncherSettingsFile? TryImportFromV1()
    {
        foreach (var path in GetV1ProxyKeysPaths())
        {
            if (!File.Exists(path)) continue;
            try
            {
                var json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("keys", out var keysEl) ||
                    keysEl.ValueKind != JsonValueKind.Array)
                    continue;

                var instances = new List<InstanceConfig>();
                var i = 1;
                foreach (var keyEl in keysEl.EnumerateArray())
                {
                    var key = keyEl.GetString()?.Trim() ?? "";
                    if (string.IsNullOrWhiteSpace(key)) continue;
                    var inst = InstanceConfig.CreateNew(i++);
                    inst.KiotProxyKey = key;
                    instances.Add(inst);
                }

                if (instances.Count == 0) continue;

                return new LauncherSettingsFile { Instances = instances };
            }
            catch { }
        }

        return null;
    }

    private static void NormalizeAccounts(LauncherSettingsFile data)
    {
        data.Accounts ??= [];
        if (data.Accounts.Count == 0)
            data.Accounts.Add(BigSellerAccountConfig.CreateDefault());

        foreach (var account in data.Accounts)
        {
            if (string.IsNullOrWhiteSpace(account.Id))
                account.Id = Guid.NewGuid().ToString("N");
            account.Shops ??= [];
            if (account.Shops.Count == 0)
                account.Shops.Add(ShopConfig.CreateDefault());

            foreach (var shop in account.Shops)
            {
                if (string.IsNullOrWhiteSpace(shop.Id))
                    shop.Id = Guid.NewGuid().ToString("N");
            }

        }

        var activeAccount = data.Accounts.FirstOrDefault(a => a.Id == data.ActiveAccountId) ?? data.Accounts[0];
        data.ActiveAccountId = activeAccount.Id;
        var activeShop = activeAccount.Shops.FirstOrDefault(s => s.Id == data.ActiveShopId) ?? activeAccount.Shops[0];
        data.ActiveShopId = activeShop.Id;
    }

    private static void EnsureInstanceShop(LauncherSettingsFile data, InstanceConfig inst)
    {
        var account = data.Accounts.FirstOrDefault(a => a.Id == inst.AccountId)
            ?? data.Accounts.FirstOrDefault(a => a.Id == data.ActiveAccountId)
            ?? data.Accounts.First();
        inst.AccountId = account.Id;

        var shop = account.Shops.FirstOrDefault(s => s.Id == inst.ShopId)
            ?? account.Shops.FirstOrDefault(s => s.Id == data.ActiveShopId)
            ?? account.Shops.First();
        inst.ShopId = shop.Id;
    }

    private static void NormalizeRangeSettings(LauncherSettingsFile data)
    {
        if (string.IsNullOrWhiteSpace(data.RangeSheetName) && !string.IsNullOrWhiteSpace(data.AutoSheetName))
        {
            data.RangeSheetName = data.AutoSheetName.Trim();
            data.RangeStartRow = Math.Max(2, data.AutoStartRow);
            data.RangeRowsPerProfile = Math.Max(1, data.AutoRowsPerProfile);
        }
        else
        {
            data.RangeStartRow = Math.Max(2, data.RangeStartRow);
            data.RangeRowsPerProfile = Math.Max(1, data.RangeRowsPerProfile);
        }
    }

    private static void ResetSessionScopedPorts(LauncherSettingsFile data)
    {
        foreach (var shop in data.Accounts.SelectMany(a => a.Shops))
        {
            shop.BigSellerDebugPort = 0;
            shop.BigSellerImportDebugPort = 0;
        }
    }

    private static void IsolateAccountCookieFiles(LauncherSettingsFile data)
    {
        MigrateLegacyPersistentData();

        foreach (var account in data.Accounts)
        {
            var source = account.BigSellerCookieFile?.Trim() ?? "";
            var target = AppSession.ResolvePersistentDataPath("account-cookies", $"{account.Id}-bigseller.json");

            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            {
                account.BigSellerCookieFile = target;
                continue;
            }

            if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            {
                account.BigSellerCookieFile = target;
                continue;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target) ?? AppSession.RootDirectory);
                File.Copy(source, target, overwrite: true);
            }
            catch
            {
            }

            account.BigSellerCookieFile = target;
        }
    }

    /// <summary>
    /// Chuyển persistent-data từ bin/ (bị xóa khi Rebuild) sang project source dir (an toàn).
    /// Chỉ chạy một lần — khi thư mục mới chưa có dữ liệu nhưng thư mục cũ vẫn còn.
    /// </summary>
    private static void MigrateLegacyPersistentData()
    {
        var legacyRoot = Path.Combine(AppSession.BaseDirectory, "persistent-data");
        var newRoot = Path.Combine(AppSession.ProjectSourceDirectory, "persistent-data");

        if (string.Equals(Path.GetFullPath(legacyRoot), Path.GetFullPath(newRoot), StringComparison.OrdinalIgnoreCase))
            return;

        if (!Directory.Exists(legacyRoot))
            return;

        try
        {
            CopyDirIfMissing(legacyRoot, newRoot);
        }
        catch { }
    }

    private static void CopyDirIfMissing(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.GetFiles(src))
        {
            var dest = Path.Combine(dst, Path.GetFileName(file));
            if (!File.Exists(dest))
                File.Copy(file, dest);
        }
        foreach (var dir in Directory.GetDirectories(src))
            CopyDirIfMissing(dir, Path.Combine(dst, Path.GetFileName(dir)));
    }

    private static IEnumerable<string> GetV1ProxyKeysPaths()
    {
        var baseDir = AppContext.BaseDirectory;
        yield return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "open-multi-brave", "bin", "Release", "net8.0-windows", "proxy-keys.json"));
        yield return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "open-multi-brave", "bin", "Release", "net8.0-windows", "proxy-keys.json"));

        var repoRoot = FindRepoRoot(baseDir);
        if (repoRoot is not null)
            yield return Path.Combine(repoRoot, "open-multi-brave", "bin", "Release", "net8.0-windows", "proxy-keys.json");
    }

    private static string? FindRepoRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "open-multi-brave")) &&
                Directory.Exists(Path.Combine(dir.FullName, "open-multi-brave-v3")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
