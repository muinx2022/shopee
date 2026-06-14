using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenMultiBraveLauncherV3;

internal static class BraveProfileManager
{
    public static DirectoryInfo EnsureProfile(DirectoryInfo sourceUserData, InstanceConfig config, Action<string>? log = null)
    {
        config.EnsureProfileRelativePath();
        // Profile bền (BigSeller login dùng chung per-account) sống trong persistent-data để KHÔNG bị
        // xoá mỗi phiên; còn lại dùng runtime-sessions (ephemeral) như cũ cho profile Shopee.
        var rootBase = config.UsePersistentSharedProfile
            ? AppSession.ResolvePersistentDataPath()
            : AppSession.RootDirectory;
        var profileRoot = new DirectoryInfo(
            Path.Combine(rootBase, config.ProfileRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        var targetDefault = new DirectoryInfo(Path.Combine(profileRoot.FullName, "Default"));

        var sourceDefault = new DirectoryInfo(Path.Combine(sourceUserData.FullName, "Default"));
        if (!sourceDefault.Exists)
            throw new DirectoryNotFoundException($"Khong tim thay profile Default: {sourceDefault.FullName}");

        if (config.CreateNewProfileOnNextStart || !targetDefault.Exists)
        {
            if (profileRoot.Exists)
            {
                try
                {
                    Directory.Delete(profileRoot.FullName, recursive: true);
                }
                catch
                {
                    foreach (var f in Directory.EnumerateFiles(profileRoot.FullName, "*", SearchOption.AllDirectories))
                    {
                        try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                    }
                    Directory.Delete(profileRoot.FullName, recursive: true);
                }
            }

            profileRoot.Create();
            targetDefault.Create();
            CopyExtensionState(sourceDefault, targetDefault);
            config.CreateNewProfileOnNextStart = false;
            log?.Invoke("Da tao profile moi tu User Data mau.");
        }
        else
        {
            profileRoot.Create();
            log?.Invoke("Toi se dung profile hien co.");
        }

        return profileRoot;
    }

    public static void PrepareProfileForLaunch(string profileRoot)
    {
        ClearCopiedExtensionInstallState(profileRoot);
        ClearSwScriptCache(profileRoot);
        ClearSessionRestoreState(profileRoot);
        MarkProfileCleanShutdown(profileRoot);
    }

    public static string BuildBraveArguments(int cdpPort, string userDataDir, string? proxyServer, Action<string>? log = null, string? sourceUserData = null)
    {
        var parts = new List<string>
        {
            $"--user-data-dir=\"{userDataDir}\"",
            "--profile-directory=Default",
            "--new-window",
            "--no-first-run",
            "--no-default-browser-check",
            "--hide-crash-restore-bubble",
            $"--remote-debugging-port={cdpPort}",
        };
        if (!string.IsNullOrWhiteSpace(proxyServer))
            parts.Add($"--proxy-server={proxyServer}");

        var runnerPath = RunnerExtensionPaths.ResolveLoadDirectory();
        if (runnerPath is null)
            log?.Invoke("Canh bao: khong tim thay thu muc extension day du (thieu background.js) - Shopee Data Runner co the khong load.");

        var extPaths = CollectExtensionLoadPaths(runnerPath, sourceUserData);
        if (extPaths.Count > 0)
            parts.Add($"--load-extension=\"{string.Join(",", extPaths)}\"");

        return string.Join(" ", parts);
    }

    private static List<string> CollectExtensionLoadPaths(string? runnerPath, string? sourceUserData)
    {
        var paths = new List<string>();
        if (runnerPath is not null)
            paths.Add(runnerPath);

        if (!string.IsNullOrWhiteSpace(sourceUserData))
        {
            var sourceExtDir = Path.Combine(sourceUserData, "Default", "Extensions");
            if (Directory.Exists(sourceExtDir))
            {
                foreach (var extIdDir in Directory.EnumerateDirectories(sourceExtDir))
                {
                    if (Path.GetFileName(extIdDir).Equals("Temp", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var versionDir = Directory.EnumerateDirectories(extIdDir)
                        .Where(d => File.Exists(Path.Combine(d, "manifest.json")))
                        .OrderByDescending(d => d)
                        .FirstOrDefault();
                    if (versionDir is not null)
                        paths.Add(versionDir);
                }
            }
        }

        return paths;
    }

    private static void ClearSwScriptCache(string profileRoot)
    {
        try
        {
            var defaultDir = Path.Combine(profileRoot, "Default");
            foreach (var subDir in new[] { Path.Combine("Service Worker", "ScriptCache"), "Code Cache" })
            {
                var dir = Path.Combine(defaultDir, subDir);
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { File.Delete(f); } catch { }
                }
            }
        }
        catch { }
    }

    private static void ClearSessionRestoreState(string profileRoot)
    {
        try
        {
            var defaultDir = Path.Combine(profileRoot, "Default");
            foreach (var subDir in new[] { "Sessions" })
            {
                var dir = Path.Combine(defaultDir, subDir);
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }
        catch { }
    }

    private static void MarkProfileCleanShutdown(string profileRoot)
    {
        var preferencesPath = Path.Combine(profileRoot, "Default", "Preferences");
        if (!File.Exists(preferencesPath))
            return;

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(preferencesPath)) as JsonObject;
            if (root is null) return;

            var profile = root["profile"] as JsonObject;
            if (profile is null)
            {
                profile = new JsonObject();
                root["profile"] = profile;
            }

            profile["exit_type"] = "Normal";
            profile["exited_cleanly"] = true;

            File.WriteAllText(
                preferencesPath,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }),
                Encoding.UTF8);
        }
        catch { }
    }

    private static void CopyExtensionState(DirectoryInfo src, DirectoryInfo dst)
    {
        foreach (var file in new[] { "Preferences", "Secure Preferences", "Bookmarks" })
        {
            var s = Path.Combine(src.FullName, file);
            var d = Path.Combine(dst.FullName, file);
            if (File.Exists(s))
                File.Copy(s, d, overwrite: true);
        }

        SanitizeCopiedExtensionPreferences(Path.Combine(dst.FullName, "Preferences"));
        SanitizeCopiedExtensionPreferences(Path.Combine(dst.FullName, "Secure Preferences"));
    }

    private static void ClearCopiedExtensionInstallState(string profileRoot)
    {
        var defaultDir = Path.Combine(profileRoot, "Default");
        foreach (var dir in new[]
        {
            "Extensions",
            "Extension Rules",
            "Extension State",
            "Managed Extension State",
            "Sync Extension Settings",
        })
        {
            DeleteDirectoryQuietly(Path.Combine(defaultDir, dir));
        }

        SanitizeCopiedExtensionPreferences(Path.Combine(defaultDir, "Preferences"));
        SanitizeCopiedExtensionPreferences(Path.Combine(defaultDir, "Secure Preferences"));
    }

    private static void SanitizeCopiedExtensionPreferences(string fileName)
    {
        if (!File.Exists(fileName))
            return;

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(fileName)) as JsonObject;
            if (root is null)
                return;

            var extensions = root["extensions"] as JsonObject;
            if (extensions is null)
            {
                extensions = new JsonObject();
                root["extensions"] = extensions;
            }

            extensions.Remove("alerts");
            extensions.Remove("chrome_url_overrides");
            extensions.Remove("commands");
            extensions.Remove("last_chrome_version");
            extensions.Remove("pinned_extensions");
            extensions.Remove("settings");
            extensions.Remove("toolbar");

            var ui = extensions["ui"] as JsonObject;
            if (ui is null)
            {
                ui = new JsonObject();
                extensions["ui"] = ui;
            }

            ui["developer_mode"] = true;

            if (root["protection"] is JsonObject protection &&
                protection["macs"] is JsonObject macs)
            {
                macs.Remove("extensions");
            }

            File.WriteAllText(
                fileName,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }),
                Encoding.UTF8);
        }
        catch
        {
        }
    }

    private static void DeleteDirectoryQuietly(string path)
    {
        if (!Directory.Exists(path))
            return;

        try
        {
            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            }

            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.GetDirectories(src))
            CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
    }
}
