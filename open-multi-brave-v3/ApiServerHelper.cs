using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;

namespace OpenMultiBraveLauncherV3;

internal static class ApiServerHelper
{
    public static string DefaultApiBase => AppSession.ApiBase;
    public static int DefaultPort => AppSession.ApiPort;
    private static string ApiStartMutexName => $@"Local\OpenMultiBraveLauncherV3.ApiServer.{DefaultPort}";
    private static Mutex? _apiStartMutex;
    private static string _workbookPath = "";
    private static Process? _apiProcess;
    private static readonly object StartLock = new();
    private static bool _startRequested;

    public static void ConfigureWorkbookPath(string? workbookPath)
    {
        var normalized = workbookPath?.Trim() ?? "";
        if (string.Equals(_workbookPath, normalized, StringComparison.OrdinalIgnoreCase))
            return;

        _workbookPath = normalized;
        StopOwnedApiServer();
    }

    public static async Task<bool> IsReachableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await AppServices.DirectHttp.GetAsync(
                $"{DefaultApiBase}/sheets",
                cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Chờ API sẵn sàng; nếu chưa chạy thì tự mở python api\main.py rồi poll tới khi reachable hoặc hết timeout.
    /// </summary>
    public static async Task<bool> EnsureReachableAsync(
        Action<string>? log = null,
        bool tryAutoStart = true,
        int waitSeconds = 35,
        CancellationToken cancellationToken = default)
    {
        if (await IsReachableAsync(cancellationToken))
            return true;

        if (tryAutoStart)
        {
            log?.Invoke("API chưa chạy (port 8012) — đang khởi động…");
            TryStartApiInBackground(log ?? (_ => { }));
        }

        var deadline = DateTime.UtcNow.AddSeconds(waitSeconds);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(1500, cancellationToken);
            if (await IsReachableAsync(cancellationToken))
            {
                log?.Invoke("API dữ liệu sẵn sàng (port 8012).");
                return true;
            }
        }

        return false;
    }

    /// <summary>L?y danh s�ch t�n sheet t? workbook qua API /sheets.</summary>
    public static async Task<List<string>> GetSheetNamesAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<string>();
        using var response = await AppServices.DirectHttp.GetAsync(
            $"{DefaultApiBase}/sheets", cancellationToken);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (doc.RootElement.TryGetProperty("sheets", out var sheets) &&
            sheets.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in sheets.EnumerateArray())
            {
                var name = s.GetString();
                if (!string.IsNullOrWhiteSpace(name))
                    result.Add(name);
            }
        }
        return result;
    }

    public static bool IsConnectionRefused(Exception ex)
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is HttpRequestException { InnerException: SocketException se } &&
                se.SocketErrorCode == SocketError.ConnectionRefused)
                return true;

            if (cur is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
                return true;

            if (cur.Message.Contains("actively refused", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static string ConnectionRefusedHelp =>
        "API dữ liệu chưa chạy (port 8012).\n\n" +
        "1. Chạy file StartDataApi.cmd ở thư mục gốc project\n" +
        "   hoặc: python api\\main.py\n\n" +
        "2. �?i v�i gi�y th?y \"Serving on http://127.0.0.1:8012\"\n\n" +
        "3. Bấm Chạy tiếp lại.";

    public static void TryStartApiInBackground(Action<string> log)
    {
        if (IsPortListening(DefaultPort))
        {
            log($"API port {DefaultPort} dang mo, cho server san sang...");
            return;
        }

        lock (StartLock)
        {
            if (_apiProcess is not null && !_apiProcess.HasExited)
            {
                log($"API port {DefaultPort} da duoc phien nay khoi dong, cho server san sang...");
                return;
            }

            if (_startRequested)
            {
                log($"API port {DefaultPort} dang khoi dong, cho server san sang...");
                return;
            }

            _startRequested = true;
        }

        if (!TryAcquireApiStartLock())
        {
            lock (StartLock)
                _startRequested = false;
            log("API dang duoc mot phien v3 khac khoi dong, cho server san sang...");
            return;
        }

        var root = FindRepoRoot();
        if (root is null)
        {
            log("Không tìm thấy thư mục api\\main.py để tự khởi động API.");
            return;
        }

        var python = Path.Combine(root, "update-product-python", ".venv", "Scripts", "python.exe");
        if (!File.Exists(python))
            python = "python";

        var apiScript = Path.Combine(root, "api", "main.py");
        if (!File.Exists(apiScript))
        {
            log($"Không tìm thấy: {apiScript}");
            return;
        }

        try
        {
            _apiProcess = Process.Start(new ProcessStartInfo
            {
                FileName = python,
                Arguments = BuildApiArguments(apiScript),
                WorkingDirectory = root,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal,
            });
            log("�� m? c?a s? API (python api\\main.py). �?i 3�5 gi�y r?i b?m Ch?y ti?p l?i.");
        }
        catch (Exception ex)
        {
            log($"Không khởi động được API: {ex.Message}");
        }
    }

    public static void StopOwnedApiServer()
    {
        if (_apiProcess is not null)
        {
            try
            {
                if (!_apiProcess.HasExited)
                    _apiProcess.Kill(entireProcessTree: true);
            }
            catch { }

            try { _apiProcess.Dispose(); } catch { }
            _apiProcess = null;
        }

        lock (StartLock)
            _startRequested = false;

        ReleaseApiStartLock();
    }

    private static string BuildApiArguments(string apiScript)
    {
        var args = $"{Quote(apiScript)} --port {DefaultPort}";
        if (!string.IsNullOrWhiteSpace(_workbookPath))
            args += $" --workbook {Quote(_workbookPath)}";
        return args;
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    private static bool TryAcquireApiStartLock()
    {
        try
        {
            _apiStartMutex ??= new Mutex(false, ApiStartMutexName);
            return _apiStartMutex.WaitOne(0);
        }
        catch
        {
            return false;
        }
    }

    private static void ReleaseApiStartLock()
    {
        try { _apiStartMutex?.ReleaseMutex(); } catch { }
    }

    private static bool IsPortListening(int port)
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync("127.0.0.1", port);
            return task.Wait(TimeSpan.FromMilliseconds(250)) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "api", "main.py")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
