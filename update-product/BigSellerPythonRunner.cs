namespace UpdateProduct;

using System.Diagnostics;

internal sealed class BigSellerPythonRunner
{
    private readonly object _gate = new();
    private Process? _process;
    private BigSellerWorkflowSettings? _settings;
    private bool _paused;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
                return _process is { HasExited: false };
        }
    }

    public void Start(
        BigSellerWorkflowSettings settings,
        bool nameOnly,
        Action<string> log,
        Action? onExit)
    {
        lock (_gate)
        {
            if (_process is { HasExited: false })
                throw new InvalidOperationException("BigSeller Python workflow đang chạy.");
        }

        var mainPy = Path.Combine(settings.PythonDir, "main.py");
        if (!File.Exists(mainPy))
            throw new FileNotFoundException($"Không tìm thấy main.py: {mainPy}");

        var args = BuildArgs(settings, mainPy, nameOnly);
        var psi = new ProcessStartInfo
        {
            FileName = ResolvePythonExe(settings),
            Arguments = string.Join(" ", args),
            WorkingDirectory = settings.PythonDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

        ApplyEnvironment(psi, settings);
        log($"Chay: {psi.FileName} {psi.Arguments}");
        TryDeleteCookieStatusFile(settings);
        _ = InjectCookiesAfterBraveReadyAsync(settings, log);

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Không khởi chạy được Python.");
        lock (_gate)
        {
            _process = process;
            _settings = settings;
            _paused = false;
        }

        _ = PipeOutputAsync(process, process.StandardOutput, log);
        _ = PipeOutputAsync(process, process.StandardError, line => log("[stderr] " + line));
        _ = WatchExitAsync(process, settings, log, onExit);
    }

    public bool IsPaused
    {
        get
        {
            lock (_gate)
                return _paused;
        }
    }

    public void Pause(Action<string>? log = null)
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
            _paused = true;
        }

        if (process is { HasExited: false } && ProcessPauseHelper.TrySuspend(process))
            log?.Invoke("Đã tạm dừng BigSeller Python workflow.");
    }

    public void Resume(Action<string>? log = null)
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
            _paused = false;
        }

        if (process is { HasExited: false } && ProcessPauseHelper.TryResume(process))
            log?.Invoke("Đã tiếp tục BigSeller Python workflow.");
    }

    public void Stop(Action<string>? log = null)
    {
        Process? process;
        var settingsSnapshot = default(BigSellerWorkflowSettings?);
        lock (_gate)
        {
            process = _process;
            settingsSnapshot = _settings;
            _paused = false;
        }

        if (process is null)
        {
            TryKillBraveProfile(settingsSnapshot, log);
            return;
        }

        if (process.HasExited)
        {
            TryKillBraveProfile(settingsSnapshot, log);
            ClearProcessIfCurrent(process);
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
            log?.Invoke("Đã dừng BigSeller Python workflow.");
        }
        catch (Exception ex)
        {
            log?.Invoke($"Không dừng được Python: {ex.Message}");
        }
        finally
        {
            TryKillBraveProfile(settingsSnapshot, log);
            ClearProcessIfCurrent(process);
        }
    }

    private static List<string> BuildArgs(BigSellerWorkflowSettings settings, string mainPy, bool nameOnly)
    {
        var args = new List<string>
        {
            Quote(mainPy),
            "--workbook", Quote(settings.WorkbookPath),
            "--sheet", Quote(settings.DataSheet),
            "--start-row", settings.StartRow.ToString(),
            "--end-row", Math.Max(0, settings.EndRow).ToString(),
            "--shop", Quote(settings.ShopName),
            "--image", Quote(settings.ImagePath),
            "--video-folder", Quote(settings.VideoFolder),
            "--debug-port", settings.DebugPort.ToString(),
            "--api-key-file", Quote(settings.OpenAiApiKeyFile),
            "--model", Quote(settings.OpenAiModel),
        };

        if (nameOnly)
        {
            args.Add("--name-only");
        }
        else
        {
            args.Add("--draft-reload-seconds");
            args.Add(Math.Max(3, settings.ListingReloadSeconds).ToString());
            args.Add("--skip-name-update");
        }

        return args;
    }

    private static void ApplyEnvironment(ProcessStartInfo psi, BigSellerWorkflowSettings settings)
    {
        psi.Environment["SHOPEE_WORKBOOK_PATH"] = settings.WorkbookPath;
        psi.Environment["SHOPEE_DATA_SHEET"] = settings.DataSheet;
        psi.Environment["SHOPEE_START_ROW"] = settings.StartRow.ToString();
        psi.Environment["SHOPEE_END_ROW"] = Math.Max(0, settings.EndRow).ToString();
        psi.Environment["BIGSELLER_SHOP_NAME"] = settings.ShopName;
        psi.Environment["BIGSELLER_DEBUG_PORT"] = settings.DebugPort.ToString();
        psi.Environment["BIGSELLER_PROFILE_DIR"] = settings.ProfileDir;
        psi.Environment["BIGSELLER_COOKIE_FILE"] = settings.BigSellerCookieFile ?? "";
        psi.Environment["BIGSELLER_UPDATE_BATCH_ID"] = settings.BatchId ?? "";
        psi.Environment["BIGSELLER_IMAGE_PATH"] = settings.ImagePath;
        psi.Environment["BIGSELLER_VIDEO_FOLDER"] = settings.VideoFolder;
        psi.Environment["BRAVE_PATH"] = settings.BravePath;
        psi.Environment["BIGSELLER_DRAFT_RELOAD_SECONDS"] = Math.Max(3, settings.ListingReloadSeconds).ToString();
        psi.Environment["OPENAI_API_KEY_FILE"] = settings.OpenAiApiKeyFile ?? "";
        psi.Environment["BIGSELLER_FORCE_COOKIE_IMPORT"] = "0";
        psi.Environment["BIGSELLER_SKIP_COOKIE_IMPORT"] = "1";
        psi.Environment["BIGSELLER_LISTING_URL"] = BigSellerCookieImporter.DefaultListingUrl;
        if (!string.IsNullOrWhiteSpace(settings.OpenAiModel))
            psi.Environment["OPENAI_PRODUCT_NAME_MODEL"] = settings.OpenAiModel.Trim();
    }

    /// <summary>
    /// Dọn trạng thái Update product: đóng Brave worker còn sót + xóa claim store/cache của batch.
    /// Gọi lúc khởi động (trước khi spawn worker) và lúc kết thúc/Stop để restart luôn sạch.
    /// </summary>
    public static async Task RunCleanupStateAsync(
        BigSellerWorkflowSettings settings,
        Action<string>? log,
        bool includeBase = false)
    {
        var profileDir = (settings.ProfileDir ?? "").Trim();
        if (string.IsNullOrWhiteSpace(profileDir))
            return;

        try
        {
            var python = ResolvePythonExe(settings);
            var script = Path.Combine(settings.PythonDir, "main.py");
            if (!File.Exists(script))
                return;

            var psi = new ProcessStartInfo
            {
                FileName = python,
                Arguments = $"\"{script}\" --cleanup-state",
                WorkingDirectory = settings.PythonDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            psi.Environment["BIGSELLER_PROFILE_DIR"] = settings.ProfileDir;
            psi.Environment["BIGSELLER_DEBUG_PORT"] = settings.DebugPort.ToString();
            psi.Environment["BIGSELLER_UPDATE_BATCH_ID"] = settings.BatchId ?? "";
            psi.Environment["BIGSELLER_CLEANUP_INCLUDE_BASE"] = includeBase ? "1" : "0";

            using var proc = Process.Start(psi);
            if (proc is null)
                return;
            await proc.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log?.Invoke($"Cleanup state loi: {ex.Message}");
        }
    }

    private static void TryKillBraveProfile(BigSellerWorkflowSettings? settings, Action<string>? log)
    {
        if (settings is null) return;
        var profileDir = (settings.ProfileDir ?? "").Trim();
        if (string.IsNullOrWhiteSpace(profileDir) || settings.DebugPort <= 0)
            return;

        try
        {
            var python = ResolvePythonExe(settings);
            var script = Path.Combine(settings.PythonDir, "main.py");
            var psi = new ProcessStartInfo
            {
                FileName = python,
                Arguments = $"\"{script}\" --kill-profile-only",
                WorkingDirectory = settings.PythonDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.Environment["BIGSELLER_PROFILE_DIR"] = settings.ProfileDir;
            psi.Environment["BIGSELLER_DEBUG_PORT"] = settings.DebugPort.ToString();
            psi.Environment["BIGSELLER_UPDATE_BATCH_ID"] = settings.BatchId ?? "";
            using var proc = Process.Start(psi);
            proc?.WaitForExit(10000);
        }
        catch (Exception ex)
        {
            log?.Invoke($"Khong dong duoc Brave profile: {ex.Message}");
        }
    }

    private static async Task InjectCookiesAfterBraveReadyAsync(
        BigSellerWorkflowSettings settings,
        Action<string> log)
    {
        try
        {
            for (var i = 0; i < 120; i++)
            {
                await Task.Delay(500).ConfigureAwait(false);
                try
                {
                    using var response = await AppServices.DirectHttp
                        .GetAsync($"http://127.0.0.1:{settings.DebugPort}/json/version")
                        .ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                        break;
                }
                catch
                {
                    // wait for brave
                }
            }

            // Profile đã đăng nhập sẵn -> giữ phiên hiện tại, không ghi đè bằng cookie cũ trong file.
            // Có token (muc_token) chưa đủ — token có thể đã bị server thu hồi, nên probe trang app
            // để chắc chắn phiên còn sống; chết thì nạp lại từ file account (vừa login tay là file mới).
            var hasLiveSession = await HasBigSellerAuthCookieAsync(settings.DebugPort).ConfigureAwait(false);
            if (hasLiveSession)
            {
                var probe = await BigSellerCookieImporter.ProbeLoggedInAsync(settings.DebugPort, log: log)
                    .ConfigureAwait(false);
                if (probe == false)
                {
                    hasLiveSession = false;
                    log("Cookie: token trong profile da bi server thu hoi — nap lai cookie tu file account.");
                }
            }

            if (hasLiveSession)
            {
                log("Cookie: profile dang co phien BigSeller song — giu nguyen, khong ghi de tu file.");
                await TryExportBigSellerCookiesAsync(settings, log).ConfigureAwait(false);
                WriteCookieStatusFile(settings, "ok");
                return;
            }

            var count = await BigSellerCookieImporter.ImportFromFileAsync(
                settings.DebugPort,
                settings.BigSellerCookieFile ?? "",
                log,
                reloadBigSellerTabs: false,
                navigateUrl: BigSellerCookieImporter.DefaultListingUrl).ConfigureAwait(false);
            if (count <= 0)
            {
                log("Cookie: khong import duoc cookie BigSeller tu account.");
                WriteCookieStatusFile(settings, "expired");
            }
            else if (false && await BigSellerCookieImporter.ProbeLoggedInAsync(settings.DebugPort, log: log)
                         .ConfigureAwait(false) == false)
            {
                log("Cookie: file account cung da het han — mo tab Account, bam Open BigSeller, login lai roi bam Save & close.");
                WriteCookieStatusFile(settings, "expired");
            }
            else
            {
                log("Cookie: da nap cookie vao profile worker, cho Python tiep tuc kiem tra trang.");
                WriteCookieStatusFile(settings, "ok");
            }
        }
        catch (Exception ex)
        {
            log($"Cookie import loi: {ex.Message}");
            // Khong chac chan ket qua -> bao "ok" de python chay tiep nhu hanh vi cu.
            WriteCookieStatusFile(settings, "ok");
        }
    }

    private const string CookieStatusFileName = "bigseller-cookie-status.json";

    private static string? GetCookieStatusFilePath(BigSellerWorkflowSettings settings)
    {
        var dir = (settings.ProfileDir ?? "").Trim();
        return string.IsNullOrWhiteSpace(dir) ? null : Path.Combine(dir, CookieStatusFileName);
    }

    private static void TryDeleteCookieStatusFile(BigSellerWorkflowSettings settings)
    {
        try
        {
            var path = GetCookieStatusFilePath(settings);
            if (path is not null && File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }

    /// <summary>
    /// Báo kết quả cookie cho python: main.py chờ file này thay vì sleep mù 5s —
    /// "expired" thì python dừng ngay với hướng dẫn login thay vì reload vô hạn.
    /// </summary>
    private static void WriteCookieStatusFile(BigSellerWorkflowSettings settings, string status)
    {
        try
        {
            var path = GetCookieStatusFilePath(settings);
            if (path is null)
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new { status, at = DateTimeOffset.Now }));
        }
        catch { }
    }

    private static async Task<bool> HasBigSellerAuthCookieAsync(int debugPort)
    {
        try
        {
            return BigSellerCookieImporter.HasAuthCookie(
                await BigSellerCookieImporter.GetBigSellerCookiesAsync(debugPort).ConfigureAwait(false));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Lưu cookie BigSeller hiện tại trong Brave ngược lại file account để file luôn "tươi".
    /// Nhờ đó lần khởi động sau (kể cả khi profile bị xóa) vẫn đăng nhập được mà không phải login tay.
    /// </summary>
    private static Task TryExportBigSellerCookiesAsync(
        BigSellerWorkflowSettings settings,
        Action<string> log,
        bool verifySessionAlive = false) =>
        BigSellerCookieImporter.TryExportProfileCookiesToFileAsync(
            settings.DebugPort, settings.BigSellerCookieFile, log, verifySessionAlive);

    private static string ResolvePythonExe(BigSellerWorkflowSettings settings)
    {
        var configured = (settings.PythonExe ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(configured) &&
            !string.Equals(configured, "python", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(configured))
            return configured;

        var venvPython = Path.Combine(settings.PythonDir, ".venv", "Scripts", "python.exe");
        return File.Exists(venvPython) ? venvPython : (string.IsNullOrWhiteSpace(configured) ? "python" : configured);
    }

    private static string Quote(string value) => $"\"{(value ?? "").Replace("\"", "\\\"")}\"";

    private static async Task PipeOutputAsync(Process process, StreamReader reader, Action<string> log)
    {
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line is null) break;
                log(line);
            }
        }
        catch { }
    }

    private async Task WatchExitAsync(Process process, BigSellerWorkflowSettings settings, Action<string> log, Action? onExit)
    {
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            log($"Python exit (code {process.ExitCode}).");
            // Brave có thể vẫn đang chạy -> tranh thủ lưu cookie mới nhất (vd. vừa login tay) về file.
            // verifySessionAlive: chỉ lưu khi phiên còn sống thật — không "đóng dấu tươi" cookie chết.
            await TryExportBigSellerCookiesAsync(settings, log, verifySessionAlive: true).ConfigureAwait(false);
        }
        catch { }
        finally
        {
            ClearProcessIfCurrent(process);
            process.Dispose();
            onExit?.Invoke();
        }
    }

    private void ClearProcessIfCurrent(Process process)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_process, process))
            {
                _process = null;
                _settings = null;
            }
        }
    }
}
