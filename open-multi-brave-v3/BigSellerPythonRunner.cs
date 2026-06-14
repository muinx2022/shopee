namespace OpenMultiBraveLauncherV3;

using System.Diagnostics;

internal sealed class BigSellerPythonRunner
{
    private readonly object _gate = new();
    private Process? _process;
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
        _ = InjectCookiesAfterBraveReadyAsync(settings, log);

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Không khởi chạy được Python.");
        lock (_gate)
        {
            _process = process;
            _paused = false;
        }

        _ = PipeOutputAsync(process, process.StandardOutput, log);
        _ = PipeOutputAsync(process, process.StandardError, line => log("[stderr] " + line));
        _ = WatchExitAsync(process, log, onExit);
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
            log?.Invoke("Da tam dung BigSeller Python workflow.");
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
            log?.Invoke("Da tiep tuc BigSeller Python workflow.");
    }

    public void Stop(Action<string>? log = null)
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
            _paused = false;
        }

        if (process is null || process.HasExited)
            return;

        try
        {
            process.Kill(entireProcessTree: true);
            log?.Invoke("�� d?ng BigSeller Python workflow.");
        }
        catch (Exception ex)
        {
            log?.Invoke($"Không dừng được Python: {ex.Message}");
        }
        finally
        {
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

            var count = await BigSellerCookieImporter.ImportFromFileAsync(
                settings.DebugPort,
                settings.BigSellerCookieFile ?? "",
                log,
                reloadBigSellerTabs: false,
                navigateUrl: BigSellerCookieImporter.DefaultListingUrl).ConfigureAwait(false);
            if (count <= 0)
                log("Cookie: khong import duoc cookie BigSeller tu account.");
        }
        catch (Exception ex)
        {
            log($"Cookie import loi: {ex.Message}");
        }
    }

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

    private async Task WatchExitAsync(Process process, Action<string> log, Action? onExit)
    {
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            log($"Python exit (code {process.ExitCode}).");
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
                _process = null;
        }
    }
}
