namespace ShopeeStatApp.Services;

public sealed class EdgeManager(AppSettingsService appSettings)
{
    private Process? _process;
    private int _cdpPort;
    private string? _currentProfileDir;

    public bool IsRunning => _process is { HasExited: false };
    public int CdpPort => _cdpPort;

    public static string? DetectEdgePath()
    {
        var candidates = new[]
        {
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        };
        return candidates.FirstOrDefault(File.Exists) ?? FindExecutableOnPath("msedge.exe");
    }

    /// <summary>
    /// Resolves proxy. Returns null if not configured.
    /// Throws ProxyUnavailableException if kiotproxy key is set but API returns nothing.
    /// </summary>
    public async Task<string?> ResolveProxyAsync(InstanceConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.KiotProxyKey))
        {
            var (proxy, error) = await FetchKiotProxyAsync(config.KiotProxyKey, config.ProxyType);
            if (proxy is null)
                throw new InvalidOperationException(
                    $"Proxy lỗi: không lấy được proxy từ kiotproxy key \"{config.KiotProxyKey}\".\n" +
                    (string.IsNullOrWhiteSpace(error) ? "Kiểm tra key hoặc thử lại sau." : error));
            return proxy;
        }

        if (!string.IsNullOrWhiteSpace(config.ManualProxy))
        {
            var p = config.ManualProxy.Trim();
            return p.Contains("://") ? p : $"http://{p}";
        }

        return null; // no proxy configured — ok
    }

    public void Launch(InstanceConfig config, string profileDir, string? proxyServer, int wsPort)
    {
        var edgePath = appSettings.Settings.EdgePath;
        if (string.IsNullOrWhiteSpace(edgePath) || !File.Exists(edgePath))
            edgePath = DetectEdgePath()
                ?? throw new FileNotFoundException(
                    "Khong tim thay msedge.exe. Hay cai Microsoft Edge hoac cau hinh edgePath trong settings.json.");

        var extPath = ResolveExtensionPath();

        // Close only the Edge instance/profile managed by this app.
        Kill();
        ClearExtensionRuntimeCache(profileDir);

        _cdpPort = FindFreePort();
        _currentProfileDir = Path.GetFullPath(profileDir);
        var args = BuildArgs(_cdpPort, profileDir, proxyServer, extPath, wsPort);
        _process = Process.Start(new ProcessStartInfo
        {
            FileName = edgePath,
            Arguments = args,
            UseShellExecute = false,
        });
    }

    public async Task CleanupRestoredTabsAsync(int wsPort, CancellationToken ct = default)
    {
        if (_cdpPort <= 0) return;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        JsonDocument? targetsDoc = null;

        try
        {
            var deadline = Environment.TickCount64 + 6_000;
            while (Environment.TickCount64 < deadline)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var json = await http.GetStringAsync($"http://localhost:{_cdpPort}/json/list", ct);
                    targetsDoc = JsonDocument.Parse(json);
                    if (targetsDoc.RootElement.GetArrayLength() > 0) break;
                }
                catch
                {
                    targetsDoc?.Dispose();
                    targetsDoc = null;
                    await Task.Delay(300, ct);
                }
            }

            if (targetsDoc is null) return;

            var pageTargets = targetsDoc.RootElement.EnumerateArray()
                .Where(t => GetJsonString(t, "type") == "page")
                .Select(t => new
                {
                    Id = GetJsonString(t, "id"),
                    Url = GetJsonString(t, "url"),
                })
                .Where(t => !string.IsNullOrWhiteSpace(t.Id))
                .ToList();

            if (pageTargets.Count <= 1) return;

            var keep =
                pageTargets.FirstOrDefault(t => t.Url.Contains($"_ss_ws={wsPort}", StringComparison.OrdinalIgnoreCase))
                ?? pageTargets.FirstOrDefault(t =>
                    t.Url.Contains("_ss_ws=", StringComparison.OrdinalIgnoreCase))
                ?? pageTargets.FirstOrDefault(t =>
                    t.Url.Contains("shopee.vn", StringComparison.OrdinalIgnoreCase)
                    && !t.Url.Contains("shopee.vn/api/", StringComparison.OrdinalIgnoreCase))
                ?? pageTargets[0];

            foreach (var target in pageTargets.Where(t => t.Id != keep.Id))
            {
                try
                {
                    await http.GetStringAsync(
                        $"http://localhost:{_cdpPort}/json/close/{Uri.EscapeDataString(target.Id)}",
                        ct);
                }
                catch { }
            }
        }
        finally
        {
            targetsDoc?.Dispose();
        }
    }

    private static string GetJsonString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) ? value.GetString() ?? "" : "";

    private static string? FindExecutableOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch { }
        }

        return null;
    }

    private string ResolveExtensionPath()
    {
        var tried = new List<string>();
        var configured = appSettings.Settings.ExtensionPath;

        if (!string.IsNullOrWhiteSpace(configured))
        {
            var resolved = Path.GetFullPath(configured);
            tried.Add(resolved);
            if (IsExtensionDir(resolved))
                return resolved;
        }

        foreach (var candidate in GetExtensionPathCandidates())
        {
            var resolved = Path.GetFullPath(candidate);
            if (tried.Any(x => string.Equals(x, resolved, StringComparison.OrdinalIgnoreCase)))
                continue;

            tried.Add(resolved);
            if (!IsExtensionDir(resolved))
                continue;

            appSettings.Settings.ExtensionPath = resolved;
            try { appSettings.SaveSettings(); } catch { }
            return resolved;
        }

        throw new DirectoryNotFoundException(
            "Khong tim thay extension Shopee Stat. Cau hinh extensionPath trong settings.json hoac dat thu muc shopee-stat-ext canh repo/exe." +
            Environment.NewLine + "Da thu:" + Environment.NewLine +
            string.Join(Environment.NewLine, tried.Select(x => "- " + x)));
    }

    private static IEnumerable<string> GetExtensionPathCandidates()
    {
        var baseDir = Path.GetFullPath(AppContext.BaseDirectory);
        for (var dir = new DirectoryInfo(baseDir); dir is not null; dir = dir.Parent)
            yield return Path.Combine(dir.FullName, "..", "shopee-stat-ext");

        yield return Path.Combine(
            Path.GetDirectoryName(Application.ExecutablePath) ?? ".",
            "shopee-stat-ext");
    }

    private static bool IsExtensionDir(string path) =>
        Directory.Exists(path) && File.Exists(Path.Combine(path, "manifest.json"));

    private static void KillEdgeProcessesForProfile(string profileDir)
    {
        var fullProfileDir = Path.GetFullPath(profileDir).TrimEnd('\\', '/');
        var killedAny = false;
        try
        {
            // One WMI query collects msedge.exe processes and command lines, then kills
            // only processes whose command line points to this managed profile.
            foreach (var pid in FindEdgePidsByCommandLine(fullProfileDir))
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    if (!p.HasExited)
                    {
                        p.Kill(entireProcessTree: true);
                        killedAny = true;
                    }
                }
                catch { }
            }

            if (killedAny)
                Thread.Sleep(400);
        }
        catch { }
    }

    private static List<int> FindEdgePidsByCommandLine(string profileDirNeedle)
    {
        var pids = new List<int>();
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'msedge.exe'");
            foreach (var obj in searcher.Get())
            {
                try
                {
                    var commandLine = obj["CommandLine"] as string;
                    if (!string.IsNullOrEmpty(commandLine) &&
                        commandLine.Contains(profileDirNeedle, StringComparison.OrdinalIgnoreCase))
                    {
                        var pid = Convert.ToInt32(obj["ProcessId"]);
                        if (pid > 0)
                            pids.Add(pid);
                    }
                }
                catch { }
                finally { obj.Dispose(); }
            }
        }
        catch { }

        return pids;
    }

    private static void ClearExtensionRuntimeCache(string profileDir)
    {
        try
        {
            var defaultDir = Path.Combine(Path.GetFullPath(profileDir), "Default");
            var cacheDirs = new[]
            {
                Path.Combine(defaultDir, "Service Worker"),
                Path.Combine(defaultDir, "Code Cache"),
                Path.Combine(defaultDir, "Cache"),
                Path.Combine(defaultDir, "GPUCache"),
            };

            foreach (var dir in cacheDirs)
            {
                try
                {
                    if (Directory.Exists(dir))
                        Directory.Delete(dir, recursive: true);
                }
                catch { }
            }
        }
        catch { }
    }

    private static string BuildArgs(int cdpPort, string userDataDir, string? proxy, string extPath, int wsPort)
    {
        var parts = new List<string>
        {
            $"--user-data-dir=\"{userDataDir}\"",
            "--profile-directory=Default",
            "--new-window",
            "--no-first-run",
            "--no-default-browser-check",
            "--hide-crash-restore-bubble",
            "--no-restore-last-session",
            "--restore-last-session=false",
            "--disable-background-mode",
            $"--remote-debugging-port={cdpPort}",
            $"--load-extension=\"{extPath}\"",
        };

        if (!string.IsNullOrWhiteSpace(proxy))
            parts.Add($"--proxy-server={proxy}");

        // Extension reads WS port from URL hash on first tab
        parts.Add($"\"https://shopee.vn/#_ss_ws={wsPort}\"");

        return string.Join(" ", parts);
    }

    private static async Task<(string? Proxy, string? Error)> FetchKiotProxyAsync(string key, string proxyType)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };

        var currentUrl = $"https://api.kiotproxy.com/api/v1/proxies/current?key={Uri.EscapeDataString(key)}";
        var current = await TryFetchKiotProxyAsync(http, currentUrl, proxyType);
        if (current.Proxy is not null)
            return current;

        var newUrl = $"https://api.kiotproxy.com/api/v1/proxies/new?key={Uri.EscapeDataString(key)}&region=random";
        var fresh = await TryFetchKiotProxyAsync(http, newUrl, proxyType);
        return fresh.Proxy is not null ? fresh : (null, fresh.Error ?? current.Error);
    }

    private static async Task<(string? Proxy, string? Error)> TryFetchKiotProxyAsync(
        HttpClient http,
        string url,
        string proxyType)
    {
        string response;
        try
        {
            response = await http.GetStringAsync(url);
        }
        catch (HttpRequestException ex)
        {
            response = ex.Message;
            if (ex.StatusCode is not null)
            {
                try { response = await http.GetStringAsync(url); } catch { }
            }
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }

        try
        {
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;
            if (root.TryGetProperty("success", out var successProp) && !successProp.GetBoolean())
            {
                var message = root.TryGetProperty("message", out var msg) ? msg.GetString() : null;
                return (null, message);
            }

            var data = root.TryGetProperty("data", out var dataProp) ? dataProp : root;
            var fieldName = string.Equals(proxyType, "socks5", StringComparison.OrdinalIgnoreCase)
                ? "socks5"
                : "http";

            if (data.TryGetProperty(fieldName, out var proxyProp))
            {
                var proxy = proxyProp.GetString();
                if (!string.IsNullOrWhiteSpace(proxy))
                    return ($"{fieldName}://{proxy}", null);
            }

            if (!data.TryGetProperty("host", out var hostProp)) return (null, "KiotProxy không trả về host/proxy.");
            var host = hostProp.GetString();
            if (string.IsNullOrWhiteSpace(host)) return (null, "KiotProxy trả về host rỗng.");

            var portField = string.Equals(fieldName, "socks5", StringComparison.OrdinalIgnoreCase)
                ? "socks5Port"
                : "httpPort";
            if (!data.TryGetProperty(portField, out var portProp)) return (null, $"KiotProxy không trả về {portField}.");

            return ($"{fieldName}://{host}:{portProp.GetInt32()}", null);
        }
        catch (JsonException)
        {
            return (null, response);
        }
    }

    // Ports handed out by FindFreePort but not yet bound by their consumer (Edge's CDP
    // server or a lane's HttpListener). The OS frees an ephemeral port the instant the
    // probe listener closes, so without this two lanes starting together — and binding
    // seconds later, after proxy resolution — can be handed the SAME number and collide
    // (one lane's Edge then talks to another lane's WS/CDP). Guarded by _portLock.
    private static readonly object _portLock = new();
    private static readonly HashSet<int> _reservedPorts = [];

    /// <summary>
    /// Allocates a free loopback TCP port (used for CDP and per-lane WS servers), unique
    /// across concurrent callers: a port handed out here won't be handed out again until
    /// <see cref="ReleasePort"/> frees it (call that once the consumer has bound, or torn down).
    /// </summary>
    public static int FindFreePort()
    {
        lock (_portLock)
        {
            for (var attempt = 0; attempt < 50; attempt++)
            {
                var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
                l.Start();
                var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
                l.Stop();
                // Skip ports already reserved by a sibling lane that hasn't bound yet —
                // a fresh probe listener gets a different ephemeral port on retry.
                if (_reservedPorts.Add(port))
                    return port;
            }
            throw new InvalidOperationException("Không cấp phát được cổng trống cho lane.");
        }
    }

    /// <summary>Frees a port previously returned by <see cref="FindFreePort"/> so it can be reused.</summary>
    public static void ReleasePort(int port)
    {
        lock (_portLock) _reservedPorts.Remove(port);
    }

    public void Kill()
    {
        try
        {
            if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: true);
        }
        catch { }

        // Giải phóng handle tiến trình. Trước đây chỉ gán _process=null → mỗi lần relaunch (tab
        // "Tìm theo file" relaunch MỖI link) rò một SafeProcessHandle → tích luỹ → góp phần đơ máy.
        try { _process?.Dispose(); } catch { }

        if (!string.IsNullOrWhiteSpace(_currentProfileDir))
            KillEdgeProcessesForProfile(_currentProfileDir);

        // The CDP port was reserved for this browser's lifetime; free it now that Edge is gone.
        if (_cdpPort > 0)
        {
            ReleasePort(_cdpPort);
            _cdpPort = 0;
        }

        _process = null;
    }
}

