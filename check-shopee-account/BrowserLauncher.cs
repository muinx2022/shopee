namespace CheckShopeeAccount;

/// <summary>
/// Mở một cửa sổ Edge riêng, có bật CDP (remote debugging) và proxy. Mỗi tài khoản dùng
/// một profile sạch (thư mục riêng) để cookie/đăng nhập không lẫn giữa các lần check.
/// </summary>
public sealed class BrowserLauncher
{
    private Process? _process;
    private int _cdpPort;
    private string? _profileDir;

    public int CdpPort => _cdpPort;

    public static string? DetectEdgePath()
    {
        var candidates = new[]
        {
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Mở Edge tới <paramref name="startUrl"/>. Ném exception nếu không tìm thấy msedge.exe.</summary>
    public void Launch(string profileDir, string? proxyServer, string startUrl)
    {
        var edgePath = DetectEdgePath()
            ?? throw new FileNotFoundException(
                "Không tìm thấy msedge.exe. Hãy cài Microsoft Edge.");

        Kill();
        Directory.CreateDirectory(profileDir);
        _profileDir = Path.GetFullPath(profileDir);

        _cdpPort = FindFreePort();
        var args = BuildArgs(_cdpPort, profileDir, proxyServer, startUrl);
        _process = Process.Start(new ProcessStartInfo
        {
            FileName = edgePath,
            Arguments = args,
            UseShellExecute = false,
        });
    }

    private static string BuildArgs(int cdpPort, string userDataDir, string? proxy, string startUrl)
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
            "--proxy-bypass-list=\"localhost;127.0.0.1\"",
            $"--remote-debugging-port={cdpPort}",
        };

        if (!string.IsNullOrWhiteSpace(proxy))
            parts.Add($"--proxy-server={proxy}");

        parts.Add($"\"{startUrl}\"");
        return string.Join(" ", parts);
    }

    public void Kill()
    {
        try
        {
            if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: true);
        }
        catch { }

        // Edge tách tiến trình: msedge.exe vừa Start thường thoát ngay sau khi bàn giao cho
        // tiến trình cửa sổ thật (PID khác) → _process.Kill() không giết được cửa sổ. Phải kill
        // theo --user-data-dir (mỗi tk 1 profile tạm riêng nên match chính xác, không đụng Edge khác).
        if (!string.IsNullOrWhiteSpace(_profileDir))
            KillEdgeProcessesForProfile(_profileDir);

        // Giải phóng handle tiến trình trước khi bỏ tham chiếu (tránh rò handle qua mỗi lần check tk).
        try { _process?.Dispose(); } catch { }
        _process = null;
        ReleasePort(_cdpPort);
        _cdpPort = 0;
    }

    private static void KillEdgeProcessesForProfile(string profileDir)
    {
        var needle = profileDir.TrimEnd('\\', '/');
        var killedAny = false;
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'msedge.exe'");
            foreach (var obj in searcher.Get())
            {
                try
                {
                    var commandLine = obj["CommandLine"] as string;
                    if (string.IsNullOrEmpty(commandLine) ||
                        !commandLine.Contains(needle, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var pid = Convert.ToInt32(obj["ProcessId"]);
                    if (pid <= 0) continue;
                    using var p = Process.GetProcessById(pid);
                    if (!p.HasExited)
                    {
                        p.Kill(entireProcessTree: true);
                        killedAny = true;
                    }
                }
                catch { }
                finally { obj.Dispose(); }
            }

            if (killedAny)
                Thread.Sleep(400);
        }
        catch { }
    }

    // Port CDP đã cấp nhưng Edge chưa kịp bind. Khi check song song, 2 launcher gọi FindFreePort
    // gần như cùng lúc có thể nhận CÙNG một cổng ephemeral (listener dò đã đóng ngay) → 2 cửa sổ
    // Edge tranh cùng cổng debug. Giữ chỗ tới khi Kill() để mỗi luồng có cổng riêng.
    private static readonly object _portLock = new();
    private static readonly HashSet<int> _reservedPorts = [];

    private static int FindFreePort()
    {
        lock (_portLock)
        {
            for (var attempt = 0; attempt < 50; attempt++)
            {
                var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
                l.Start();
                var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
                l.Stop();
                if (_reservedPorts.Add(port))
                    return port;
            }
            throw new InvalidOperationException("Không cấp phát được cổng CDP trống.");
        }
    }

    private static void ReleasePort(int port)
    {
        if (port <= 0) return;
        lock (_portLock) _reservedPorts.Remove(port);
    }
}
