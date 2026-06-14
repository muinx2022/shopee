using System.Diagnostics;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace OpenMultiBraveLauncherV3;

internal sealed class BraveInstanceSession : IDisposable
{
    private const string KiotProxyNewUrl = "https://api.kiotproxy.com/api/v1/proxies/new";
    private const string KiotProxyCurrentUrl = "https://api.kiotproxy.com/api/v1/proxies/current";

    private readonly int _cdpPort;
    private readonly Action<string> _log;

    private Process? _braveProcess;
    private DirectoryInfo? _profileRoot;
    private string? _currentProxyFingerprint;
    private bool _running;
    private bool _busy;
    private bool _restarting;
    private bool _stopping;
    private InstanceConfig? _config;
    private string _braveExe = "";
    private string _sourceUserData = "";
    private string _statusText = "Dừng";
    private string _proxySummary = "";
    private DateTimeOffset? _lastBigSellerImportAt;
    private string? _lastInterruptLogSignature;

    private CancellationTokenSource? _runnerLoopCts;
    private Task? _runnerLoopTask;
    private volatile bool _runnerLoopActive;

    private CancellationTokenSource? _swPinnerCts;
    private Task? _swPinnerTask;

    private readonly System.Windows.Forms.Timer _monitorTimer;
    private readonly System.Windows.Forms.Timer _progressTimer;

    private bool _runnerLoopRequested;

    public event Action? StatusChanged;
    public event Action<string>? LogLine;
    public event Action? ExtensionProgressSynced;
    public event Action<InstanceConfig>? ExtensionInterrupted;
    /// <summary>Runner loop kết thúc (xong / dừng / lỗi) — dùng cho chạy lượt.</summary>
    public event Action<string>? RunnerLoopEnded;

    public bool IsRunning => _running;
    public bool IsBusy => _busy;
    public bool IsRunnerLoopActive => _runnerLoopActive;
    public bool IsRunnerLoopPending => _runnerLoopActive || _runnerLoopRequested;
    public string StatusText => _statusText;
    public string ProxySummary => _proxySummary;
    public DirectoryInfo? ProfileRoot => _profileRoot;

    public BraveInstanceSession(int cdpPort, Action<string> log)
    {
        _cdpPort = cdpPort;
        _log = log;
        _monitorTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
        _monitorTimer.Tick += async (_, _) => await CheckProxyAndRestartIfNeededAsync();
        _progressTimer = new System.Windows.Forms.Timer { Interval = 20_000 };
        _progressTimer.Tick += (_, _) =>
        {
            if (!_running)
                return;
            _ = SyncExtensionProgressAsync(silent: true);
        };
    }

    private int _syncBusy;

    public async Task<bool> SyncExtensionProgressAsync(bool silent = false, CancellationToken cancellationToken = default)
    {
        if (_config is null)
            return false;

        if (Interlocked.CompareExchange(ref _syncBusy, 1, 0) != 0)
            return false;

        try
        {
            return await SyncExtensionProgressCoreAsync(silent, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _syncBusy, 0);
        }
    }

    /// <summary>Đồng bộ từ file profile — không CDP, dùng khi Stop đồng bộ.</summary>
    private bool TrySyncFromFileOnly(bool silent)
    {
        if (_config is null)
            return false;

        var profileRoot = ResolveProfileRoot();
        if (profileRoot is null)
            return false;

        if (!ExtensionProgressReader.TryRead(profileRoot, out var state) || !HasMeaningfulProgress(state))
            return false;

        _config.ApplyExtensionProgress(state);
        ExtensionProgressSynced?.Invoke();
        return true;
    }

    private async Task<bool> SyncExtensionProgressCoreAsync(bool silent, CancellationToken cancellationToken)
    {
        if (_runnerLoopActive && _config is not null)
        {
            _config.ProgressSyncedAt = DateTimeOffset.Now;
            ExtensionProgressSynced?.Invoke();
            return true;
        }

        var profileRoot = ResolveProfileRoot();
        if (profileRoot is null)
        {
            if (!silent)
                Log("Chưa có profile — bấm Start instance này ít nhất một lần (hoặc tạo profile mới).");
            return false;
        }

        try
        {
            ExtensionRunnerState? state = null;
            ExtensionRunnerState fileState;

            if (_running)
            {
                try
                {
                    state = await ExtensionRunnerAutomation.TryReadStateViaCdpAsync(
                        _cdpPort, profileRoot, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (!silent)
                {
                    Log($"CDP chưa đọc được ({ex.Message}) — thử đọc file profile…");
                }
            }

            if (state is null)
            {
                if (!ExtensionProgressReader.TryRead(profileRoot, out fileState))
                {
                    if (!silent)
                        Log("Chưa đọc được tiến độ extension. Reload extension trong brave://extensions rồi thử lại.");
                    return false;
                }
                state = fileState;
            }

            if (state is null || !HasMeaningfulProgress(state))
            {
                if (!silent)
                    Log("Extension chưa có tiến độ (chưa chạy lần nào trên profile này).");
                return false;
            }

            _config.ApplyExtensionProgress(state);

            if (state.Running == true)
                _lastInterruptLogSignature = null;

            if (state.IsInterruptedMidRun())
            {
                var signature =
                    $"{state.Phase}|{state.Running}|{state.CurrentRow}|{state.LastCompletedRow}|{state.StoppedAtRow}";
                if (!string.Equals(signature, _lastInterruptLogSignature, StringComparison.Ordinal))
                {
                    _lastInterruptLogSignature = signature;
                    var at = _config.StoppedAtRow;
                    var resume = _config.SuggestedResumeRow;
                    var sheet = string.IsNullOrWhiteSpace(_config.DataSheet) ? "?" : _config.DataSheet;
                    var sku = string.IsNullOrWhiteSpace(_config.LastSku) ? "" : $", SKU {_config.LastSku}";
                    var phase = string.IsNullOrWhiteSpace(_config.RunnerPhase) ? "" : $" ({_config.RunnerPhase})";
                    Log(
                        $"Bị dừng giữa chừng tại dòng {at} — sheet \"{sheet}\"{sku}{phase}. " +
                        $"Chạy tiếp từ dòng {resume} (bấm nút Chạy tiếp bên phải).");
                    ExtensionInterrupted?.Invoke(_config);
                }
            }
            else if (!silent)
            {
                var resume = _config.SuggestedResumeRow;
                Log(
                    $"Tiến độ: sheet=\"{_config.DataSheet}\", xong dòng {_config.LastCompletedRow}, " +
                    $"chạy tiếp từ {resume} (từ dòng form: {_config.StartRow}).");
            }

            ExtensionProgressSynced?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            if (!silent)
                Log($"Đồng bộ extension lỗi: {ex.Message}");
            return false;
        }
    }

    private static bool HasMeaningfulProgress(ExtensionRunnerState state) =>
        state.LastCompletedRow is > 0 ||
        state.CurrentRow is > 0 ||
        state.StartRow is > 0 ||
        !string.IsNullOrWhiteSpace(state.SheetName);

    public async Task PushFormConfigToExtensionAsync(
        bool logSuccess = false,
        CancellationToken cancellationToken = default)
    {
        if (!_running || _config is null)
            return;

        var profileRoot = ResolveProfileRoot();
        if (profileRoot is null)
            return;

        var sheet = _config.DataSheet?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(sheet))
            return;

        try
        {
            var ok = await ExtensionRunnerAutomation.TryApplyFormConfigAsync(
                _cdpPort,
                profileRoot,
                sheet,
                _config.StartRow,
                _config.EndRow,
                cancellationToken).ConfigureAwait(false);
            if (ok && logSuccess)
                Log($"Đã c?p nh?t extension: sheet \"{sheet}\", dòng {_config.StartRow}–{_config.EndRow ?? 0}.");
            if (ok)
                await ExtensionRunnerAutomation.TryBroadcastRunnerStateAsync(
                    _cdpPort, profileRoot, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log($"Cập nhật extension: {ex.Message}");
        }
    }

    public void ApplyConfig(InstanceConfig config) => _config = config;

    public async Task<bool> WaitForCdpReadyAsync(
        int attempts = 20,
        int delayMs = 500,
        CancellationToken cancellationToken = default)
    {
        if (!_running)
            return false;

        for (var i = 0; i < attempts; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_braveProcess is not null && _braveProcess.HasExited)
            {
                StopSwPinner();
                _running = false;
                _statusText = "Da tat";
                StatusChanged?.Invoke();
                return false;
            }

            try
            {
                _ = await GetBrowserWebSocketUrlAsync().ConfigureAwait(false);
                return true;
            }
            catch
            {
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }
        }

        return false;
    }

    /// <summary>Profile trên đĩa — không cần Brave đang chạy từ launcher.</summary>
    private DirectoryInfo? ResolveProfileRoot()
    {
        if (_profileRoot is not null && _profileRoot.Exists)
            return _profileRoot;

        if (_config is null)
            return null;

        _config.EnsureProfileRelativePath();
        var path = Path.Combine(
            AppSession.RootDirectory,
            _config.ProfileRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var root = new DirectoryInfo(path);
        if (!root.Exists)
            return null;

        var defaultDir = Path.Combine(root.FullName, "Default");
        return Directory.Exists(defaultDir) ? root : null;
    }

    public Task ResumeContinueAsync(
        string braveExe,
        string sourceUserData,
        bool preferSuggestedResume = true,
        bool retryExtensionStart = false,
        CancellationToken cancellationToken = default)
    {
        if (_config is null)
            throw new InvalidOperationException("Chưa chọn cấu hình instance.");

        if (_runnerLoopActive)
        {
            Log("Runner đang chạy trên launcher.");
            return Task.CompletedTask;
        }

        _runnerLoopCts?.Cancel();
        _runnerLoopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _runnerLoopTask = Task.Run(async () =>
        {
            _runnerLoopRequested = true;
            try
            {
                var braveDied = _running && _braveProcess is not null && _braveProcess.HasExited;
                if (braveDied)
                {
                    Log("Brave đã t?t � dang kh?i d?ng l?i�");
                    StopSwPinner();
                    _running = false;
                    try { _braveProcess!.Dispose(); } catch { }
                    _braveProcess = null;
                }

                if (!_running)
                {
                    Log("Brave chưa chạy — đang khởi động…");
                    await StartAsync(braveExe, sourceUserData).ConfigureAwait(false);
                    await Task.Delay(3000, _runnerLoopCts.Token).ConfigureAwait(false);
                }

                var profileRoot = ResolveProfileRoot()
                    ?? throw new InvalidOperationException("Profile chưa sẵn sàng — Start instance này trước.");

                _runnerLoopActive = true;
                Log("Bắt đầu chạy (launcher điều khiển)…");
                ExtensionProgressSynced?.Invoke();

                var extensionRetryCount = 0;
                for (var proxyAttempt = 0;
                     proxyAttempt < 4 && !_runnerLoopCts.Token.IsCancellationRequested;
                     proxyAttempt++)
                {
                    try
                    {
                        await EnsureBigSellerCookiesReadyAsync().ConfigureAwait(false);
                        await LauncherRunnerLoop.RunAsync(
                            _cdpPort,
                            profileRoot,
                            _config,
                            Log,
                            () => ExtensionProgressSynced?.Invoke(),
                            EnsureBigSellerCookiesReadyAsync,
                            preferSuggestedResume: proxyAttempt > 0 || preferSuggestedResume,
                            _runnerLoopCts.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (
                        retryExtensionStart &&
                        extensionRetryCount < 2 &&
                        IsExtensionConnectionError(ex.Message))
                    {
                        extensionRetryCount++;
                        Log($"Extension/CDP không phản hồi — tự đóng/mở lại profile rồi thử chạy lại ({extensionRetryCount}/2)…");
                        await RestartProfileForExtensionErrorAsync().ConfigureAwait(false);
                        await Task.Delay(3500, _runnerLoopCts.Token).ConfigureAwait(false);
                        await EnsureBigSellerCookiesReadyAsync().ConfigureAwait(false);
                        profileRoot = ResolveProfileRoot()
                            ?? throw new InvalidOperationException("Profile chưa sẵn sàng sau khi mở lại.");
                        proxyAttempt--;
                        continue;
                    }

                    if (_config is null || !IsProxyPausedDuringRun(_config) || proxyAttempt >= 3)
                        break;

                    Log("Lỗi proxy khi mở link — tự khởi động lại profile với proxy mới…");
                    _restarting = true;
                    try
                    {
                        await RestartProfileForProxyErrorAsync().ConfigureAwait(false);
                        await Task.Delay(3000, _runnerLoopCts.Token).ConfigureAwait(false);
                        await EnsureBigSellerCookiesReadyAsync().ConfigureAwait(false);
                        Log("Chạy tiếp sau khi đổi proxy…");
                    }
                    catch (Exception restartEx)
                    {
                        if (_config is not null)
                        {
                            _config.RunnerRunning = false;
                            _config.RunnerPhase = "error";
                            _config.LastRunnerMessage = $"Không lấy được proxy mới: {restartEx.Message}";
                        }
                        Log($"Không restart được sau lỗi proxy: {restartEx.Message}");
                        break;
                    }
                    finally
                    {
                        _restarting = false;
                    }
                }

                Log("Runner hoàn tất.");

                // Tự đóng profile sau khi chạy xong (nếu bật)
                if (_config is not null &&
                    _config.AutoCloseProfileOnFinish &&
                    string.Equals(_config.RunnerPhase, "finished", StringComparison.OrdinalIgnoreCase))
                {
                    Log("T? dừng profile vì đã ch?y xong.");
                    await StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                if (_config is not null)
                {
                    _config.RunnerRunning = false;
                    _config.RunnerPhase = "stopped";
                }
                Log("Đã d?ng ch?y.");
            }
            catch (ApiNotRunningException ex)
            {
                if (_config is not null)
                {
                    _config.RunnerRunning = false;
                    _config.RunnerPhase = "paused";
                    _config.LastRunnerMessage = "API dữ liệu chưa chạy (port 8012).";
                }
                Log($"Lỗi runner: {ex.Message}");
            }
            catch (Exception ex)
            {
                if (_config is not null)
                {
                    _config.RunnerRunning = false;
                    _config.RunnerPhase = "error";
                    _config.LastRunnerMessage = ex.Message;
                }
                Log($"Lỗi runner: {ex.Message}");
            }
            finally
            {
                _runnerLoopActive = false;
                ExtensionProgressSynced?.Invoke();
                if (_runnerLoopRequested && _config is not null)
                {
                    RunnerLoopEnded?.Invoke(_config.Id);
                    _runnerLoopRequested = false;
                }
            }
        }, _runnerLoopCts.Token);

        return Task.CompletedTask;
    }

    public async Task StopRunnerAsync(CancellationToken cancellationToken = default)
    {
        if (!_runnerLoopActive)
        {
            Log("Runner chưa chạy — không có gì để dừng.");
            return;
        }

        Log("Đang dừng runner…");
        _runnerLoopCts?.Cancel();

        var profileRoot = ResolveProfileRoot();
        if (profileRoot is not null)
        {
            try
            {
                await ExtensionRunnerAutomation.AbortScrapeStepAsync(
                    _cdpPort, profileRoot, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"Hủy bước scrape: {ex.Message}");
            }
        }

        if (_runnerLoopTask is not null)
        {
            try
            {
                await _runnerLoopTask.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Log("Runner chưa dừng hẳn trong 8s — có thể cần bấm lại.");
            }
        }

        if (_config is not null)
        {
            _config.RunnerRunning = false;
            _config.RunnerPhase = "stopped";
            var last = _config.LastCompletedRow;
            var resume = _config.SuggestedResumeRow;
            var sheet = string.IsNullOrWhiteSpace(_config.DataSheet) ? "?" : _config.DataSheet;
            Log(
                last is > 0
                    ? $"Tr?ng thái cu?i: sheet \"{sheet}\", xong dòng {last}, ch?y ti?p t? {resume}."
                    : "Đã d?ng ch?y.");
        }

        ExtensionProgressSynced?.Invoke();
    }

    public async Task StartAsync(string braveExe, string sourceUserData)
    {
        if (_busy || _config is null) return;
        _busy = true;
        _braveExe = braveExe.Trim();
        _sourceUserData = sourceUserData.Trim();
        SetStatus("Đang khởi động…");

        try
        {
            if (!File.Exists(_braveExe))
                throw new FileNotFoundException("Không tìm thấy brave.exe.", _braveExe);

            var sourceData = new DirectoryInfo(_sourceUserData);
            if (!sourceData.Exists)
                throw new DirectoryNotFoundException("Không tìm thấy thư mục User Data mẫu.");

            _profileRoot = EnsureProfile(sourceData, _config);
            Log($"Profile: {_profileRoot.FullName}");

            var (proxyServer, proxyData) = await ResolveProxyForLaunchAsync().ConfigureAwait(false);
            _proxySummary = proxyServer ?? "(không proxy)";
            var args = BuildBraveArguments(_profileRoot.FullName, proxyServer);

            PrepareProfileForLaunch(_profileRoot.FullName);
            LaunchBrave(_braveExe, args);
            _currentProxyFingerprint = proxyData is not null
                ? BuildFingerprint(proxyData)
                : proxyServer ?? "";
            _monitorTimer.Start();
            _progressTimer.Start();
            _running = true;
            SetStatus(proxyServer is not null ? $"Đang chạy — {proxyServer}" : "Đang chạy — không proxy");
            StartSwPinner();
            await AutoImportBigSellerCookiesAsync().ConfigureAwait(false);
            ScheduleDeferredSyncAfterStart();
        }
        catch (Exception ex)
        {
            _running = false;
            SetStatus($"Lỗi: {ex.Message}");
            throw;
        }
        finally
        {
            _busy = false;
            RaiseStatusChanged();
        }
    }

    private void StartSwPinner()
    {
        _swPinnerCts?.Cancel();
        _swPinnerCts = new CancellationTokenSource();
        var ct = _swPinnerCts.Token;
        var extensionId = _profileRoot is null
            ? RunnerExtensionPaths.TryGetLoadedExtensionId()
            : ExtensionRunnerAutomation.TryGetRunnerExtensionIdFromProfile(_profileRoot)
              ?? RunnerExtensionPaths.TryGetLoadedExtensionId();
        if (extensionId is null) return;

        _swPinnerTask = Task.Run(async () =>
        {
            await ExtensionRunnerAutomation.PinSwWithFlatSessionAsync(
                _cdpPort, extensionId, Log, ct).ConfigureAwait(false);
        }, ct);
    }

    private void StopSwPinner()
    {
        _swPinnerCts?.Cancel();
        _swPinnerCts = null;
    }

    private static void PrepareProfileForLaunch(string profileRoot)
    {
        ClearSwScriptCache(profileRoot);
        ClearSessionRestoreState(profileRoot);
        MarkProfileCleanShutdown(profileRoot);
    }

    /// <summary>Xóa script cache của service worker để Brave load lại extension mới nhất từ disk.</summary>
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

    private void ScheduleDeferredSyncAfterStart()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(2500).ConfigureAwait(false);
            await SyncExtensionProgressAsync(silent: true).ConfigureAwait(false);
        });
    }

    /// <summary>Đóng nhanh (khi thoát app) – không ch? CDP.</summary>
    public void Stop()
    {
        _runnerLoopCts?.Cancel();
        StopSwPinner();
        _monitorTimer.Stop();
        _progressTimer.Stop();
        _running = false;
        KillBraveProcess(maxWaitMs: 1500);
        TrySyncFromFileOnly(silent: true);
        _proxySummary = "";
        SetStatus("Dừng");
        RaiseStatusChanged();
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_running || _stopping)
            return;

        _stopping = true;
        StopSwPinner();
        _monitorTimer.Stop();
        _progressTimer.Stop();
        SetStatus("Đang đóng profile…");
        RaiseStatusChanged();

        try
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));

                if (ShouldStopExtensionRunnerBeforeExit())
                {
                    await TryStopRunnerBeforeBraveExitAsync(timeout.Token).ConfigureAwait(false);
                    await Task.Delay(350, CancellationToken.None).ConfigureAwait(false);
                }
                await SyncExtensionProgressAsync(silent: true, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Log("Hết thời gian chờ extension — đang tắt Brave…");
            }
            catch (Exception ex)
            {
                Log($"Dừng extension: {ex.Message}");
            }

            await Task.Run(() => KillBraveProcess(maxWaitMs: 2500), CancellationToken.None);

            _running = false;
            _proxySummary = "";
            SetStatus("Dừng");
            RaiseStatusChanged();
        }
        finally
        {
            _stopping = false;
        }
    }

    private bool ShouldStopExtensionRunnerBeforeExit()
    {
        if (_runnerLoopActive || _runnerLoopRequested)
            return true;

        return _config is not null &&
               (_config.RunnerRunning == true ||
                string.Equals(_config.RunnerPhase, "starting", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_config.RunnerPhase, "opening", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_config.RunnerPhase, "scraping", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_config.RunnerPhase, "saving", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_config.RunnerPhase, "paused", StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        _monitorTimer.Stop();
        _monitorTimer.Dispose();
        KillBraveProcess();
    }

    private async Task TryStopRunnerBeforeBraveExitAsync(CancellationToken cancellationToken)
    {
        var profileRoot = ResolveProfileRoot();
        if (profileRoot is null)
            return;

        try
        {
            await ExtensionRunnerAutomation.StopRunAsync(_cdpPort, profileRoot, Log, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log($"Không gửi được lệnh dừng extension: {ex.Message}");
        }
    }

    private DirectoryInfo EnsureProfile(DirectoryInfo sourceUserData, InstanceConfig config)
    {
        config.EnsureProfileRelativePath();
        var profileRoot = new DirectoryInfo(
            Path.Combine(AppSession.RootDirectory, config.ProfileRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        var targetDefault = new DirectoryInfo(Path.Combine(profileRoot.FullName, "Default"));

        var sourceDefault = new DirectoryInfo(Path.Combine(sourceUserData.FullName, "Default"));
        if (!sourceDefault.Exists)
            throw new DirectoryNotFoundException($"Không tìm thấy profile Default: {sourceDefault.FullName}");

        if (config.CreateNewProfileOnNextStart || !targetDefault.Exists)
        {
            if (profileRoot.Exists)
            {
                try { Directory.Delete(profileRoot.FullName, recursive: true); }
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
            Log("Đã t?o profile m?i t? User Data m?u.");
        }
        else
        {
            profileRoot.Create();
            Log("Tôi s? d?ng profile hi?n có.");
        }

        return profileRoot;
    }

    private void LaunchBrave(string exePath, string arguments)
    {
        KillBraveProcess();
        _braveProcess = Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments,
            UseShellExecute = false,
        });
        Log($"Brave PID={_braveProcess?.Id}");
    }

    private void KillBraveProcess(int maxWaitMs = 1500)
    {
        if (_braveProcess is null) return;
        try
        {
            if (!_braveProcess.HasExited)
            {
                TryCloseBraveGracefully(maxWaitMs);
                if (!_braveProcess.HasExited)
                {
                    _braveProcess.Kill(entireProcessTree: true);
                    if (maxWaitMs > 0)
                        _braveProcess.WaitForExit(maxWaitMs);
                }
            }
        }
        catch { }
        finally
        {
            _braveProcess.Dispose();
            _braveProcess = null;
        }
    }

    private void TryCloseBraveGracefully(int maxWaitMs)
    {
        if (_braveProcess is null || _braveProcess.HasExited)
            return;

        var waitMs = Math.Max(2500, maxWaitMs);
        try
        {
            _braveProcess.CloseMainWindow();
            if (_braveProcess.WaitForExit(waitMs))
                return;
        }
        catch
        {
            // fall through to CDP Browser.close
        }

        try
        {
            using var browser = new ClientWebSocket();
            browser.ConnectAsync(new Uri(GetBrowserWebSocketUrlAsync().GetAwaiter().GetResult()), CancellationToken.None)
                .GetAwaiter().GetResult();
            SendCdpAsync(browser, 501, "Browser.close", null).GetAwaiter().GetResult();
            _braveProcess.WaitForExit(waitMs);
        }
        catch
        {
            // fallback kill happens in caller
        }
    }

    private string BuildBraveArguments(string userDataDir, string? proxyServer)
    {
        var parts = new List<string>
        {
            $"--user-data-dir=\"{userDataDir}\"",
            "--profile-directory=Default",
            "--new-window",
            "--no-first-run",
            "--no-default-browser-check",
            "--hide-crash-restore-bubble",
            $"--remote-debugging-port={_cdpPort}",
        };
        if (!string.IsNullOrWhiteSpace(proxyServer))
            parts.Add($"--proxy-server={proxyServer}");
        var runnerPath = RunnerExtensionPaths.ResolveLoadDirectory();
        if (runnerPath is null)
            Log("Cảnh báo: không tìm thấy thư mục ext đầy đủ (thiếu background.js) – Shopee Data Runner có thể không load.");
        var extPaths = CollectExtensionLoadPaths(runnerPath);
        if (extPaths.Count > 0)
            parts.Add($"--load-extension=\"{string.Join(",", extPaths)}\"");
        return string.Join(" ", parts);
    }

    private List<string> CollectExtensionLoadPaths(string? runnerPath)
    {
        var paths = new List<string>();
        if (runnerPath is not null)
            paths.Add(runnerPath);

        if (!string.IsNullOrWhiteSpace(_sourceUserData))
        {
            var sourceExtDir = Path.Combine(_sourceUserData, "Default", "Extensions");
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

    private async Task<(string? proxyServer, Dictionary<string, object>? proxyData)> ResolveProxyForLaunchAsync()
    {
        if (_config is null)
            throw new InvalidOperationException("Chưa cấu hình instance.");

        var kiotKey = _config.KiotProxyKey.Trim();
        if (!string.IsNullOrWhiteSpace(kiotKey))
        {
            var proxy = await GetWorkingProxyAsync().ConfigureAwait(false);
            var server = BuildProxyServer(proxy, _config.ProxyType);
            return (server, proxy);
        }

        var manual = _config.ManualProxy.Trim();
        if (!string.IsNullOrWhiteSpace(manual))
        {
            var server = NormalizeManualProxy(manual, _config.ProxyType);
            Log($"Dùng proxy thủ công: {server}");
            if (await IsProxyWorkingAsync(server).ConfigureAwait(false))
                return (server, null);
            Log("Proxy thủ công không phản hồi kiểm tra — vẫn thử dùng.");
            return (server, null);
        }

        Log("Không có Kiot key / proxy thủ công — mở profile không proxy.");
        return (null, null);
    }

    private static string NormalizeManualProxy(string input, string proxyType)
    {
        var s = input.Trim();
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("socks4://", StringComparison.OrdinalIgnoreCase))
            return s;

        var scheme = proxyType.Equals("socks5", StringComparison.OrdinalIgnoreCase) ? "socks5://" : "http://";
        return scheme + s;
    }

    private static bool IsProxyPausedDuringRun(InstanceConfig config) =>
        string.Equals(config.RunnerPhase, "paused", StringComparison.OrdinalIgnoreCase) &&
        (config.LastRunnerMessage?.Contains("proxy", StringComparison.OrdinalIgnoreCase) == true ||
         config.LastRunnerMessage?.Contains("không tải được trang", StringComparison.OrdinalIgnoreCase) == true);

    private static bool IsExtensionConnectionError(string message) =>
        message.Contains("extension", StringComparison.OrdinalIgnoreCase) &&
        (message.Contains("CDP", StringComparison.OrdinalIgnoreCase) ||
         message.Contains("không kết nối", StringComparison.OrdinalIgnoreCase) ||
         message.Contains("không kết nối", StringComparison.OrdinalIgnoreCase) ||
         message.Contains("không phản hồi", StringComparison.OrdinalIgnoreCase) ||
         message.Contains("không phản hồi", StringComparison.OrdinalIgnoreCase));

    private async Task RestartProfileForExtensionErrorAsync()
    {
        if (_profileRoot is null)
            throw new InvalidOperationException("Profile chưa sẵn sàng.");

        var server = string.IsNullOrWhiteSpace(_proxySummary) || _proxySummary.StartsWith('(')
            ? null
            : _proxySummary;

        StopSwPinner();
        ExtensionRunnerAutomation.ClearResolvedExtension(_cdpPort);
        _lastBigSellerImportAt = null;
        PrepareProfileForLaunch(_profileRoot.FullName);
        KillBraveProcess();
        await Task.Delay(700).ConfigureAwait(false);

        var args = BuildBraveArguments(_profileRoot.FullName, server);
        LaunchBrave(_braveExe, args);
        _running = true;
        SetStatus(server is not null ? $"Đang chạy — {server}" : "Đang chạy — không proxy");
        StartSwPinner();
    }

    private async Task RestartProfileForProxyErrorAsync()
    {
        if (_profileRoot is null || _config is null)
            return;

        Dictionary<string, object>? proxyData = null;
        string? server;
        if (!string.IsNullOrWhiteSpace(_config.KiotProxyKey.Trim()))
        {
            var avoidFingerprint = _currentProxyFingerprint;
            proxyData = await GetWorkingProxyAsync(preferFresh: true, avoidFingerprint: avoidFingerprint)
                .ConfigureAwait(false);
            server = BuildProxyServer(proxyData, _config.ProxyType);
        }
        else
        {
            (server, proxyData) = await ResolveProxyForLaunchAsync().ConfigureAwait(false);
        }

        StopSwPinner();
        ExtensionRunnerAutomation.ClearResolvedExtension(_cdpPort);
        _lastBigSellerImportAt = null;
        PrepareProfileForLaunch(_profileRoot.FullName);
        KillBraveProcess();
        await Task.Delay(700).ConfigureAwait(false);
        var args = BuildBraveArguments(_profileRoot.FullName, server);
        LaunchBrave(_braveExe, args);
        _proxySummary = server ?? "(không proxy)";
        _currentProxyFingerprint = proxyData is not null ? BuildFingerprint(proxyData) : server ?? "";
        SetStatus(server is not null ? $"Đang chạy — {server}" : "Đang chạy — không proxy");
        StartSwPinner();
    }

    private async Task<Dictionary<string, object>> GetProxyAsync()
    {
        if (_config is null) throw new InvalidOperationException("Chưa cấu hình instance.");
        var key = _config.KiotProxyKey.Trim();
        var region = string.IsNullOrWhiteSpace(_config.Region) ? "random" : _config.Region.Trim();
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Cần nhập KiotProxy key.");

        var url = $"{KiotProxyNewUrl}?key={Uri.EscapeDataString(key)}&region={Uri.EscapeDataString(region)}";
        Log($"Lấy proxy: {region}");

        using var response = await AppServices.Http.GetAsync(url);
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"KiotProxy new {(int)response.StatusCode}: {ExtractError(json)}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("success", out var s) || !s.GetBoolean())
        {
            var msg = root.TryGetProperty("message", out var m) ? m.GetString() : null;
            var err = root.TryGetProperty("error", out var e) ? e.GetString() : null;
            throw new InvalidOperationException(msg ?? err ?? "KiotProxy trả về lỗi.");
        }

        return ParseProxyData(root.GetProperty("data"));
    }

    private async Task<Dictionary<string, object>> GetWorkingProxyAsync(
        int maxAttempts = 5,
        bool preferFresh = false,
        string? avoidFingerprint = null)
    {
        // Normal launch can reuse /current after the first /new attempt to avoid Kiot rate limits.
        // Proxy-error recovery must prefer /new and reject the same proxy that just failed in Brave.
        Exception? lastError = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            Dictionary<string, object> proxy;
            try
            {
                proxy = preferFresh || attempt == 1
                    ? await GetProxyAsync()
                    : await GetCurrentProxyAsync();
            }
            catch (Exception ex)
            {
                lastError = ex;
                try
                {
                    proxy = await GetCurrentProxyAsync();
                }
                catch (Exception currentEx)
                {
                    lastError = currentEx;
                    if (attempt < maxAttempts)
                        await Task.Delay(preferFresh ? 10_000 : 2_000).ConfigureAwait(false);
                    continue;
                }
            }

            var proxyServer = BuildProxyServer(proxy, _config!.ProxyType);
            var fingerprint = BuildFingerprint(proxy);
            if (!string.IsNullOrWhiteSpace(avoidFingerprint) &&
                string.Equals(fingerprint, avoidFingerprint, StringComparison.Ordinal))
            {
                lastError = new InvalidOperationException($"KiotProxy vẫn trả về proxy cũ đang lỗi: {proxyServer}");
                if (attempt < maxAttempts)
                    await Task.Delay(2000).ConfigureAwait(false);
                continue;
            }

            Log($"Kiểm tra proxy {attempt}/{maxAttempts}: {proxyServer}");
            if (await IsProxyWorkingAsync(proxyServer))
                return proxy;
            lastError = new InvalidOperationException($"Proxy không hoạt động: {proxyServer}");

            if (attempt < maxAttempts)
                await Task.Delay(2000).ConfigureAwait(false);
        }
        throw lastError ?? new InvalidOperationException("Không lấy được proxy.");
    }

    private async Task<Dictionary<string, object>> GetCurrentProxyAsync()
    {
        if (_config is null) throw new InvalidOperationException("Chưa cấu hình instance.");
        var key = _config.KiotProxyKey.Trim();
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Cần nhập KiotProxy key.");

        var url = $"{KiotProxyCurrentUrl}?key={Uri.EscapeDataString(key)}";
        using var response = await AppServices.Http.GetAsync(url);
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"KiotProxy current {(int)response.StatusCode}: {ExtractError(json)}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("success", out var s) || !s.GetBoolean())
        {
            var msg = root.TryGetProperty("message", out var m) ? m.GetString() : null;
            var err = root.TryGetProperty("error", out var e) ? e.GetString() : null;
            throw new InvalidOperationException(msg ?? err ?? "KiotProxy trả về lỗi.");
        }

        return ParseProxyData(root.GetProperty("data"));
    }

    private static Dictionary<string, object> ParseProxyData(JsonElement data)
    {
        var proxy = new Dictionary<string, object>();
        foreach (var prop in data.EnumerateObject())
        {
            proxy[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                JsonValueKind.Number => prop.Value.TryGetInt64(out var i) ? i : prop.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => prop.Value.ToString() ?? string.Empty,
            };
        }
        return proxy;
    }

    private static string BuildProxyServer(Dictionary<string, object> proxy, string selectedType)
    {
        var type = (selectedType ?? "http").Trim().ToLowerInvariant();
        string? value = type == "socks5"
            ? proxy.TryGetValue("socks5", out var s) ? s?.ToString() : null
            : proxy.TryGetValue("http", out var h) ? h?.ToString() : null;

        value ??= proxy.TryGetValue("http", out var hf) ? hf?.ToString() : null;
        value ??= proxy.TryGetValue("socks5", out var sf) ? sf?.ToString() : null;

        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("KiotProxy không trả về endpoint.");

        return value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase)
            ? value
            : $"{(type == "socks5" ? "socks5" : "http")}://{value}";
    }

    private static async Task<bool> IsProxyWorkingAsync(string proxyServer)
    {
        try
        {
            var handler = new HttpClientHandler
            {
                Proxy = new System.Net.WebProxy(proxyServer),
                UseProxy = true,
                AutomaticDecompression = System.Net.DecompressionMethods.All,
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            using var response = await client.GetAsync("https://api.ipify.org?format=json");
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private static string BuildFingerprint(Dictionary<string, object> proxy)
    {
        static string Get(Dictionary<string, object> p, string k) =>
            p.TryGetValue(k, out var v) ? v?.ToString() ?? "" : "";
        return string.Join("|", Get(proxy, "realIpAddress"), Get(proxy, "host"),
            Get(proxy, "http"), Get(proxy, "socks5"));
    }

    private static string ExtractError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var parts = new List<string>();
            if (root.TryGetProperty("message", out var m) && !string.IsNullOrWhiteSpace(m.GetString()))
                parts.Add(m.GetString()!);
            if (root.TryGetProperty("error", out var e) && !string.IsNullOrWhiteSpace(e.GetString()))
                parts.Add(e.GetString()!);
            if (parts.Count > 0) return string.Join(" | ", parts);
        }
        catch { }
        return string.IsNullOrWhiteSpace(json) ? "L?i không xác d?nh." : json.Trim();
    }

    private static bool IsProxyExpiredError(string msg) =>
        msg.Contains("KiotProxy current", StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("KiotProxy new", StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("PROXY_NOT_FOUND_BY_KEY", StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("Could not find the proxy being used by key", StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("hết hạn", StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("het han", StringComparison.OrdinalIgnoreCase);

    private async Task CheckProxyAndRestartIfNeededAsync()
    {
        if (!_running || _restarting || _busy || _profileRoot is null || _braveProcess is null || _config is null)
            return;

        var taskRunDispatched = false;
        try
        {
            var hasKiot = !string.IsNullOrWhiteSpace(_config.KiotProxyKey.Trim());
            string? server;

            if (!hasKiot)
            {
                server = string.IsNullOrWhiteSpace(_config.ManualProxy)
                    ? null
                    : NormalizeManualProxy(_config.ManualProxy, _config.ProxyType);

                if (await HasChromeProxyErrorPageAsync().ConfigureAwait(false))
                {
                    _restarting = true;
                    Log("Phát hi?n ERR_PROXY/No internet – t? kh?i d?ng l?i profile…");
                    var wasRunnerActive = _runnerLoopActive;
                    try { _runnerLoopCts?.Cancel(); } catch { }
                    await RestartProfileForProxyErrorAsync().ConfigureAwait(false);
                    if (wasRunnerActive)
                    {
                        await Task.Delay(2500).ConfigureAwait(false);
                        await ResumeContinueAsync(_braveExe, _sourceUserData, preferSuggestedResume: true)
                            .ConfigureAwait(false);
                    }
                }
                return;
            }

            Dictionary<string, object> current;
            try
            {
                current = await GetCurrentProxyAsync();
            }
            catch (HttpRequestException ex)
            {
                SetStatus("Không kết nối KiotProxy API");
                Log($"Monitor: {ex.Message}");
                return;
            }
            catch (Exception ex) when (IsProxyExpiredError(ex.Message))
            {
                _restarting = true;
                try { await RestartWithFreshProxyAsync(); }
                catch (Exception rfEx) { Log($"Refresh: {rfEx.Message}"); }
                return;
            }

            var fp = BuildFingerprint(current);
            server = BuildProxyServer(current, _config.ProxyType);

            if (!string.Equals(fp, _currentProxyFingerprint, StringComparison.Ordinal))
            {
                _restarting = true;
                Log($"Proxy đổi → khởi động lại");
                var args = BuildBraveArguments(_profileRoot.FullName, server);
                StopSwPinner();
                ExtensionRunnerAutomation.ClearResolvedExtension(_cdpPort);
                PrepareProfileForLaunch(_profileRoot.FullName);
                KillBraveProcess();
                await Task.Delay(700).ConfigureAwait(false);
                LaunchBrave(_braveExe, args);
                _currentProxyFingerprint = fp;
                _proxySummary = server;
                SetStatus($"Đang chạy — {server}");
                StartSwPinner();
                return;
            }

            if (!await IsProxyWorkingAsync(server))
            {
                _restarting = true;
                try { await RestartWithFreshProxyAsync(); }
                catch (Exception rfEx) { SetStatus($"Lỗi proxy: {rfEx.Message}"); }
                return;
            }

            // Proxy API báo OK nhung Brave v?n hi?n "No internet"/ERR_PROXY... (tab chrome-error).
            // Trường hợp này user đang phải Đóng profile → Mở lại thủ công. Tự động làm tương tự.
            if (await HasChromeProxyErrorPageAsync().ConfigureAwait(false))
            {
                _restarting = true;
                taskRunDispatched = true;
                var args = BuildBraveArguments(_profileRoot.FullName, server);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        Log("Phát hi?n ERR_PROXY/No internet trên tab – t? kh?i d?ng l?i profile…");
                        var wasRunnerActive = _runnerLoopActive;
                        try { _runnerLoopCts?.Cancel(); } catch { }

                        StopSwPinner();
                        ExtensionRunnerAutomation.ClearResolvedExtension(_cdpPort);
                        PrepareProfileForLaunch(_profileRoot.FullName);
                        KillBraveProcess();
                        await Task.Delay(700).ConfigureAwait(false);
                        LaunchBrave(_braveExe, args);
                        _proxySummary = server;
                        SetStatus($"Đang chạy — {server}");
                        StartSwPinner();

                        if (wasRunnerActive)
                        {
                            await Task.Delay(2500).ConfigureAwait(false);
                            await ResumeContinueAsync(_braveExe, _sourceUserData, preferSuggestedResume: true)
                                .ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Restart profile (proxy error): {ex.Message}");
                    }
                    finally
                    {
                        _restarting = false;
                        RaiseStatusChanged();
                    }
                });
                return;
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Monitor: {ex.Message}");
            Log(ex.Message);
        }
        finally
        {
            if (!taskRunDispatched)
            {
                _restarting = false;
                RaiseStatusChanged();
            }
        }
    }

    private async Task RestartWithFreshProxyAsync()
    {
        if (_profileRoot is null || _config is null)
            return;

        if (string.IsNullOrWhiteSpace(_config.KiotProxyKey.Trim()))
        {
            await RestartProfileForProxyErrorAsync().ConfigureAwait(false);
            return;
        }

        var proxy = await GetWorkingProxyAsync(preferFresh: true, avoidFingerprint: _currentProxyFingerprint);
        var server = BuildProxyServer(proxy, _config.ProxyType);
        var args = BuildBraveArguments(_profileRoot.FullName, server);
        StopSwPinner();
        ExtensionRunnerAutomation.ClearResolvedExtension(_cdpPort);
        PrepareProfileForLaunch(_profileRoot.FullName);
        KillBraveProcess();
        await Task.Delay(700).ConfigureAwait(false);
        LaunchBrave(_braveExe, args);
        _currentProxyFingerprint = BuildFingerprint(proxy);
        _proxySummary = server;
        SetStatus($"Đang chạy — {server}");
        StartSwPinner();
    }

    private async Task<bool> HasChromeProxyErrorPageAsync()
    {
        try
        {
            using var response = await AppServices.DirectHttp.GetAsync(
                $"http://127.0.0.1:{_cdpPort}/json/list",
                CancellationToken.None).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var type = item.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                if (!string.Equals(type, "page", StringComparison.OrdinalIgnoreCase))
                    continue;

                var url = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                var title = item.TryGetProperty("title", out var ti) ? ti.GetString() ?? "" : "";
                if (url.StartsWith("chrome-error://", StringComparison.OrdinalIgnoreCase) ||
                    title.Contains("No internet", StringComparison.OrdinalIgnoreCase) ||
                    title.Contains("ERR_PROXY_CONNECTION_FAILED", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { }
        return false;
    }

    private static void CopyExtensionState(DirectoryInfo src, DirectoryInfo dst)
    {
        foreach (var file in new[] { "Preferences", "Secure Preferences", "Bookmarks" })
        {
            var s = Path.Combine(src.FullName, file);
            var d = Path.Combine(dst.FullName, file);
            if (File.Exists(s)) File.Copy(s, d, overwrite: true);
        }

        foreach (var dir in new[]
        {
            "Extensions", "Extension Rules", "Extension State",
            "Local Extension Settings", "Sync Extension Settings", "Managed Extension State",
        })
        {
            var s = Path.Combine(src.FullName, dir);
            var d = Path.Combine(dst.FullName, dir);
            if (Directory.Exists(s)) CopyDir(s, d);
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

    public async Task ExportCookiesAsync(IWin32Window? owner, string defaultFileName)
    {
        var cookies = await GetAllCookiesFromBraveAsync();
        using var dlg = new SaveFileDialog
        {
            Filter = "JSON (*.json)|*.json|All files|*.*",
            FileName = defaultFileName,
            InitialDirectory = AppSession.RootDirectory,
            Title = "Xuất cookie",
        };
        if (dlg.ShowDialog(owner) != DialogResult.OK) return;

        var payload = new { exportedAt = DateTimeOffset.Now, cookies };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(dlg.FileName, json, Encoding.UTF8);
        Log($"Đã xu?t {cookies.Count} cookie ? {dlg.FileName}");
    }

    public async Task ExportCookiesToFileAsync(string fileName)
    {
        var cookies = await GetAllCookiesFromBraveAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(fileName) ?? AppSession.RootDirectory);
        var payload = new { exportedAt = DateTimeOffset.Now, cookies };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(fileName, json, Encoding.UTF8);
        Log($"Đã luu {cookies.Count} cookie ? {fileName}");
    }

    public async Task OpenBigSellerPageAsync()
    {
        var wsUrl = await EnsureBigSellerPageTargetAsync().ConfigureAwait(false);
        try
        {
            using var page = new ClientWebSocket();
            await page.ConnectAsync(new Uri(wsUrl), CancellationToken.None).ConfigureAwait(false);
            await SendCdpAsync(page, 92, "Page.navigate",
                new { url = "https://www.bigseller.com/web/" }).ConfigureAwait(false);
        }
        catch { }
    }

    public async Task ImportCookiesAsync(IWin32Window? owner)
    {
        if (_config is null) return;
        using var dlg = new OpenFileDialog
        {
            Filter = "JSON (*.json)|*.json|All files|*.*",
            Title = "Nhập cookie",
        };
        if (dlg.ShowDialog(owner) != DialogResult.OK) return;

        var json = await File.ReadAllTextAsync(dlg.FileName, Encoding.UTF8);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var cookiesEl = root.TryGetProperty("cookies", out var cp) ? cp : root;
        if (cookiesEl.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("File cookie không hợp lệ.");

        if (!_config.ExportShopee && !_config.ExportBigSeller)
            throw new InvalidOperationException("Chọn ít nhất Shopee hoặc BigSeller.");

        var count = await SetCookiesToBraveAsync(
            cookiesEl,
            _config.ExportShopee,
            _config.ExportBigSeller,
            preferredWsUrl: _config.ExportBigSeller ? await EnsureBigSellerPageTargetAsync() : null);
        if (_config.ExportBigSeller)
            await ReloadBigSellerTabsAsync();
        Log($"Đã nh?p {count} cookie.");
    }

    public async Task<int> ImportCookiesFromFileAsync(
        string fileName,
        bool includeShopee,
        bool includeBigSeller,
        bool prepareBigSellerPage = false)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return 0;
        if (!File.Exists(fileName))
            throw new FileNotFoundException("Không tìm thấy file cookie.", fileName);

        var json = await File.ReadAllTextAsync(fileName, Encoding.UTF8);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var cookiesEl = root.TryGetProperty("cookies", out var cp) ? cp : root;
        if (cookiesEl.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("File cookie không hợp lệ.");

        if (!await WaitForCdpReadyAsync().ConfigureAwait(false))
            throw new InvalidOperationException("Profile khong con ket noi CDP (co the dang restart hoac da tat).");

        var preferredWsUrl = prepareBigSellerPage && includeBigSeller
            ? await EnsureBigSellerPageTargetAsync()
            : null;
        var count = await SetCookiesToBraveAsync(cookiesEl, includeShopee, includeBigSeller, preferredWsUrl);
        if (prepareBigSellerPage && includeBigSeller)
        {
            await ReloadBigSellerTabsAsync();
            await Task.Delay(1500).ConfigureAwait(false);
            var verified = await CountBigSellerCookiesAsync().ConfigureAwait(false);
            Log($"Cookie BigSeller trong Brave sau import: {verified}.");
        }
        return count;
    }

    public async Task OpenShopeeAccountLoginAsync()
    {
        if (_config is null)
            return;

        try
        {
            if (!TryParseShopeeAccountLogin(_config.ShopeeAccountLogin, out var login, out var parseError))
            {
                Log($"Shopee login: {parseError}");
                return;
            }

            for (var i = 0; i < 20; i++)
            {
                try
                {
                    _ = await GetBrowserWebSocketUrlAsync().ConfigureAwait(false);
                    break;
                }
                catch
                {
                    await Task.Delay(500).ConfigureAwait(false);
                }
            }

            await SetShopeeSpcFCookieAsync(login).ConfigureAwait(false);
            await OpenShopeeLoginPageAsync().ConfigureAwait(false);
            await FillShopeeLoginFormAsync(login).ConfigureAwait(false);
            Log($"Shopee login: đã m? trang dang nh?p và đi?n tài kho?n {login.Username}.");
        }
        catch (Exception ex)
        {
            Log($"Shopee login lỗi: {ex.Message}");
        }
        finally
        {
            if (_config is not null)
                _config.OpenWithShopeeAccount = false;
        }
    }

    private static bool TryParseShopeeAccountLogin(
        string? raw,
        out ShopeeAccountLogin login,
        out string error)
    {
        login = default;
        error = "";

        var parts = (raw ?? "").Trim().Split('|', 3, StringSplitOptions.TrimEntries);
        if (parts.Length != 3 ||
            string.IsNullOrWhiteSpace(parts[0]) ||
            string.IsNullOrWhiteSpace(parts[1]) ||
            string.IsNullOrWhiteSpace(parts[2]))
        {
            error = "chuỗi tài khoản phải có dạng username|password|.shopee.vn=SPC_F=value.";
            return false;
        }

        var firstEq = parts[2].IndexOf('=');
        if (firstEq <= 0 || firstEq >= parts[2].Length - 1)
        {
            error = "cookie phải có dạng .shopee.vn=SPC_F=value.";
            return false;
        }

        var domain = parts[2][..firstEq].Trim();
        var nameValue = parts[2][(firstEq + 1)..].Trim();
        var secondEq = nameValue.IndexOf('=');
        if (secondEq <= 0 || secondEq >= nameValue.Length - 1)
        {
            error = "cookie phải có dạng .shopee.vn=SPC_F=value.";
            return false;
        }

        var cookieName = nameValue[..secondEq].Trim();
        var cookieValue = nameValue[(secondEq + 1)..].Trim();
        if (!string.Equals(cookieName, "SPC_F", StringComparison.OrdinalIgnoreCase))
        {
            error = "hiện chỉ hỗ trợ cookie SPC_F.";
            return false;
        }

        login = new ShopeeAccountLogin(parts[0], parts[1], domain, cookieValue);
        return true;
    }

    private async Task SetShopeeSpcFCookieAsync(ShopeeAccountLogin login)
    {
        var payload = new Dictionary<string, object?>
        {
            ["name"] = "SPC_F",
            ["value"] = login.SpcF,
            ["domain"] = string.IsNullOrWhiteSpace(login.CookieDomain) ? ".shopee.vn" : login.CookieDomain,
            ["path"] = "/",
            ["secure"] = true,
            ["httpOnly"] = false,
            ["sameSite"] = "Lax",
            ["expires"] = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds(),
        };

        using var browser = new ClientWebSocket();
        await browser.ConnectAsync(new Uri(await GetBrowserWebSocketUrlAsync().ConfigureAwait(false)), CancellationToken.None);
        await SendCdpAsync(browser, 710, "Storage.setCookies", new { cookies = new[] { payload } });
    }

    private async Task OpenShopeeLoginPageAsync()
    {
        const string loginUrl = "https://shopee.vn/buyer/login?next=https%3A%2F%2Fshopee.vn";

        using var browser = new ClientWebSocket();
        await browser.ConnectAsync(new Uri(await GetBrowserWebSocketUrlAsync().ConfigureAwait(false)), CancellationToken.None);
        var created = await SendCdpAsync(browser, 720, "Target.createTarget", new { url = loginUrl });
        var targetId = created.TryGetProperty("targetId", out var targetEl)
            ? targetEl.GetString()
            : null;

        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(300).ConfigureAwait(false);
            var ws = await FindPageWebSocketUrlAsync(url =>
                (!string.IsNullOrWhiteSpace(targetId) && url.Contains(targetId, StringComparison.OrdinalIgnoreCase)) ||
                url.StartsWith("https://shopee.vn/buyer/login", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(ws))
                return;
        }
    }

    private async Task FillShopeeLoginFormAsync(ShopeeAccountLogin login)
    {
        var usernameJson = JsonSerializer.Serialize(login.Username);
        var passwordJson = JsonSerializer.Serialize(login.Password);
        var expression =
            "(async () => {" +
            $"const username = {usernameJson};" +
            $"const password = {passwordJson};" +
            "const sleep = ms => new Promise(r => setTimeout(r, ms));" +
            "const setValue = (el, value) => {" +
            "  const proto = Object.getPrototypeOf(el);" +
            "  const desc = Object.getOwnPropertyDescriptor(proto, 'value') || Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value');" +
            "  desc.set.call(el, value);" +
            "  el.dispatchEvent(new Event('input', { bubbles: true }));" +
            "  el.dispatchEvent(new Event('change', { bubbles: true }));" +
            "};" +
            "for (let i = 0; i < 80; i++) {" +
            "  const u = document.querySelector('input[name=\"loginKey\"]');" +
            "  const p = document.querySelector('input[name=\"password\"]');" +
            "  if (u && p) {" +
            "    u.focus(); setValue(u, username);" +
            "    await sleep(120);" +
            "    p.focus(); setValue(p, password);" +
            "    await sleep(700);" +
            "    const buttons = [...document.querySelectorAll('button')];" +
            "    const loginButton = buttons.find(b => /log\\s*in|đăng\\s*nhập/i.test((b.textContent || '').trim())) || buttons.find(b => b.type === 'submit') || buttons.at(-1);" +
            "    if (loginButton) { loginButton.removeAttribute('disabled'); loginButton.click(); }" +
            "    return { ok: true };" +
            "  }" +
            "  await sleep(250);" +
            "}" +
            "return { ok: false, message: 'Không tìm thấy form login Shopee.' };" +
            "})()";

        Exception? lastError = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                var wsUrl = await FindPageWebSocketUrlAsync(url =>
                    url.StartsWith("https://shopee.vn/buyer/login", StringComparison.OrdinalIgnoreCase) ||
                    url.StartsWith("https://shopee.vn/", StringComparison.OrdinalIgnoreCase)).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(wsUrl))
                {
                    await Task.Delay(700).ConfigureAwait(false);
                    continue;
                }

                using var page = new ClientWebSocket();
                await page.ConnectAsync(new Uri(wsUrl), CancellationToken.None);
                await SendCdpAsync(page, 730, "Runtime.enable", null);
                await Task.Delay(500).ConfigureAwait(false);
                await SendCdpAsync(page, 731, "Runtime.evaluate", new
                {
                    expression,
                    awaitPromise = true,
                    returnByValue = true,
                });
                return;
            }
            catch (Exception ex) when (
                ex.Message.Contains("Execution context was destroyed", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("Cannot find context", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("Target closed", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("WebSocket", StringComparison.OrdinalIgnoreCase))
            {
                lastError = ex;
                await Task.Delay(900).ConfigureAwait(false);
            }
        }

        if (lastError is not null)
            throw lastError;
    }

    private async Task EnsureBigSellerCookiesReadyAsync()
    {
        if (_lastBigSellerImportAt is not null &&
            DateTimeOffset.Now - _lastBigSellerImportAt.Value < TimeSpan.FromSeconds(60))
            return;

        await AutoImportBigSellerCookiesAsync(force: false).ConfigureAwait(false);
    }

    private async Task AutoImportBigSellerCookiesAsync(bool force = true)
    {
        if (_config is null ||
            !_config.ExportBigSeller ||
            string.IsNullOrWhiteSpace(_config.BigSellerCookieFile))
            return;

        if (!force &&
            _lastBigSellerImportAt is not null &&
            DateTimeOffset.Now - _lastBigSellerImportAt.Value < TimeSpan.FromSeconds(60))
            return;

        try
        {
            for (var i = 0; i < 20; i++)
            {
                try
                {
                    _ = await GetCdpWebSocketUrlAsync().ConfigureAwait(false);
                    break;
                }
                catch
                {
                    await Task.Delay(500).ConfigureAwait(false);
                }
            }

            var count = await ImportCookiesFromFileAsync(
                _config.BigSellerCookieFile,
                includeShopee: false,
                includeBigSeller: true,
                prepareBigSellerPage: true).ConfigureAwait(false);
            _lastBigSellerImportAt = DateTimeOffset.Now;
            Log($"Đã t? nh?p {count} cookie BigSeller.");
        }
        catch (Exception ex)
        {
            Log($"Tự nhập cookie BigSeller lỗi: {ex.Message}");
        }
    }

    private async Task<string> GetCdpWebSocketUrlAsync()
    {
        using var response = await AppServices.DirectHttp.GetAsync($"http://127.0.0.1:{_cdpPort}/json/list");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("CDP /json/list không hợp lệ.");

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var type = item.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (!string.Equals(type, "page", StringComparison.OrdinalIgnoreCase)) continue;
            if (item.TryGetProperty("webSocketDebuggerUrl", out var wsProp))
            {
                var ws = wsProp.GetString();
                if (!string.IsNullOrWhiteSpace(ws)) return ws;
            }
        }

        throw new InvalidOperationException($"Không có tab trên CDP port {_cdpPort}.");
    }

    private async Task<string> GetBrowserWebSocketUrlAsync()
    {
        using var response = await AppServices.DirectHttp.GetAsync($"http://127.0.0.1:{_cdpPort}/json/version");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("webSocketDebuggerUrl").GetString()
               ?? throw new InvalidOperationException("CDP /json/version thiếu browser WebSocket.");
    }

    private async Task<string?> FindPageWebSocketUrlAsync(Func<string, bool> urlMatches)
    {
        using var response = await AppServices.DirectHttp.GetAsync($"http://127.0.0.1:{_cdpPort}/json/list");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var type = item.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (!string.Equals(type, "page", StringComparison.OrdinalIgnoreCase)) continue;

            var url = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            if (!urlMatches(url)) continue;

            var ws = item.TryGetProperty("webSocketDebuggerUrl", out var wsProp)
                ? wsProp.GetString()
                : null;
            if (!string.IsNullOrWhiteSpace(ws))
                return ws;
        }

        return null;
    }

    private async Task<string> EnsureBigSellerPageTargetAsync()
    {
        var existing = await FindPageWebSocketUrlAsync(IsBigSellerUrl);
        if (!string.IsNullOrWhiteSpace(existing))
            return existing;

        using var browser = new ClientWebSocket();
        await browser.ConnectAsync(new Uri(await GetBrowserWebSocketUrlAsync()), CancellationToken.None);
        await SendCdpAsync(browser, 90, "Target.createTarget", new
        {
            url = "https://www.bigseller.com/",
            background = true,
        });

        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(300);
            var ws = await FindPageWebSocketUrlAsync(IsBigSellerUrl);
            if (!string.IsNullOrWhiteSpace(ws))
                return ws;
        }

        return await GetCdpWebSocketUrlAsync();
    }

    private async Task ReloadBigSellerTabsAsync()
    {
        try
        {
            using var response = await AppServices.DirectHttp.GetAsync($"http://127.0.0.1:{_cdpPort}/json/list");
            if (!response.IsSuccessStatusCode)
                return;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var type = item.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (!string.Equals(type, "page", StringComparison.OrdinalIgnoreCase)) continue;

                var url = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                if (!IsBigSellerUrl(url)) continue;

                var ws = item.TryGetProperty("webSocketDebuggerUrl", out var wsProp)
                    ? wsProp.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(ws)) continue;

                using var page = new ClientWebSocket();
                await page.ConnectAsync(new Uri(ws), CancellationToken.None);
                await SendCdpAsync(page, 91, "Page.reload", new { ignoreCache = true });
            }
        }
        catch
        {
            // reload chỉ để BigSeller nhận cookie mới; lỗi reload không chặn scrape.
        }
    }

    private static bool IsBigSellerUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Host.Contains("bigseller", StringComparison.OrdinalIgnoreCase);

    private async Task<int> CountBigSellerCookiesAsync()
    {
        try
        {
            var cookies = await GetAllCookiesFromBraveAsync().ConfigureAwait(false);
            return cookies.Count(c =>
                c.TryGetValue("domain", out var domain) &&
                (domain?.ToString() ?? "").Contains("bigseller", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return 0;
        }
    }

    private static async Task<JsonElement> SendCdpAsync(ClientWebSocket socket, int id, string method, object? @params)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { id, method, @params }));
        await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);

        var buffer = new byte[1024 * 512];
        while (true)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult recv;
            do
            {
                recv = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (recv.MessageType == WebSocketMessageType.Close)
                    throw new InvalidOperationException("CDP socket đóng.");
                ms.Write(buffer, 0, recv.Count);
            } while (!recv.EndOfMessage);

            using var doc = JsonDocument.Parse(ms.ToArray());
            var root = doc.RootElement;
            if (!root.TryGetProperty("id", out var idProp) || idProp.GetInt32() != id)
                continue;
            if (root.TryGetProperty("error", out var err))
                throw new InvalidOperationException($"CDP error: {err}");
            if (!root.TryGetProperty("result", out var result))
                throw new InvalidOperationException("CDP result thiếu.");
            return result.Clone();
        }
    }

    private async Task<List<Dictionary<string, object?>>> GetAllCookiesFromBraveAsync()
    {
        var wsUrl = await GetCdpWebSocketUrlAsync();
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(wsUrl), CancellationToken.None);

        var result = await SendCdpAsync(socket, 1, "Network.getAllCookies", null);
        if (!result.TryGetProperty("cookies", out var cookiesEl) || cookiesEl.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<Dictionary<string, object?>>();
        foreach (var cookie in cookiesEl.EnumerateArray())
        {
            var domain = cookie.TryGetProperty("domain", out var dp) ? (dp.GetString() ?? "") : "";
            var lower = domain.ToLowerInvariant();
            if (!lower.Contains("shopee") && !lower.Contains("bigseller")) continue;

            var map = new Dictionary<string, object?>();
            foreach (var p in cookie.EnumerateObject())
            {
                map[p.Name] = p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString(),
                    JsonValueKind.Number => p.Value.TryGetInt64(out var i) ? i : p.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => p.Value.ToString(),
                };
            }
            list.Add(map);
        }
        return list;
    }

    private async Task<int> SetCookiesToBraveAsync(
        JsonElement cookiesArray,
        bool includeShopee,
        bool includeBigseller,
        string? preferredWsUrl = null)
    {
        var wsUrl = string.IsNullOrWhiteSpace(preferredWsUrl)
            ? await GetCdpWebSocketUrlAsync()
            : preferredWsUrl;
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(wsUrl), CancellationToken.None);
        await SendCdpAsync(socket, 1, "Network.enable", new { });

        var attempted = 0;
        var succeeded = 0;
        var cmdId = 1000;

        foreach (var cookie in cookiesArray.EnumerateArray())
        {
            if (cookie.ValueKind != JsonValueKind.Object) continue;

            var domain = cookie.TryGetProperty("domain", out var dp) ? (dp.GetString() ?? "") : "";
            var lower = domain.ToLowerInvariant();
            var isShopee = lower.Contains("shopee");
            var isBigseller = lower.Contains("bigseller");
            if (isShopee && !includeShopee) continue;
            if (isBigseller && !includeBigseller) continue;
            if (!isShopee && !isBigseller) continue;

            var payload = new Dictionary<string, object?>();
            foreach (var k in new[]
            {
                "name", "value", "url", "domain", "path",
                "secure", "httpOnly", "sameSite", "expires",
                "priority", "sourceScheme", "sourcePort",
            })
            {
                if (!cookie.TryGetProperty(k, out var v)) continue;
                payload[k] = v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString(),
                    JsonValueKind.Number => v.TryGetInt64(out var i) ? i : v.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => null,
                };
            }

            if (!payload.ContainsKey("name") || !payload.ContainsKey("value")) continue;
            if (!payload.ContainsKey("url") && !payload.ContainsKey("domain")) continue;

            if (!payload.ContainsKey("url") && payload.TryGetValue("domain", out var dv))
            {
                var ds = (dv as string ?? "").TrimStart('.');
                if (!string.IsNullOrEmpty(ds))
                    payload["url"] = $"https://{ds}/";
            }

            var cookieName = payload.TryGetValue("name", out var nv) ? nv as string ?? "" : "";
            if (cookieName.StartsWith("__Host-", StringComparison.OrdinalIgnoreCase))
            {
                payload.Remove("domain");
                payload["path"] = "/";
            }

            SanitizeCookiePayloadForCdp(payload, persistSessionCookie: isBigseller);

            attempted++;
            try
            {
                var storageOk = await TrySetCookieWithBrowserStorageAsync(payload).ConfigureAwait(false);
                var result = await SendCdpAsync(socket, cmdId++, "Network.setCookie", payload);
                var ok = result.TryGetProperty("success", out var sp) && sp.GetBoolean();
                if (!ok)
                {
                    var fb = new Dictionary<string, object?>(payload);
                    fb.Remove("sourceScheme");
                    fb.Remove("sourcePort");
                    var fbResult = await SendCdpAsync(socket, cmdId++, "Network.setCookie", fb);
                    ok = fbResult.TryGetProperty("success", out var fp) && fp.GetBoolean();
                }
                if (!ok && storageOk)
                    ok = true;
                if (isBigseller && TryBuildBigSellerProPayload(payload, out var proPayload))
                {
                    try
                    {
                        var proStorageOk = await TrySetCookieWithBrowserStorageAsync(proPayload).ConfigureAwait(false);
                        var proResult = await SendCdpAsync(socket, cmdId++, "Network.setCookie", proPayload);
                        var proOk = proResult.TryGetProperty("success", out var psp) && psp.GetBoolean();
                        if (!proOk)
                        {
                            var fb = new Dictionary<string, object?>(proPayload);
                            fb.Remove("sourceScheme");
                            fb.Remove("sourcePort");
                            var fbResult = await SendCdpAsync(socket, cmdId++, "Network.setCookie", fb);
                            proOk = fbResult.TryGetProperty("success", out var pfp) && pfp.GetBoolean();
                        }
                        _ = proOk || proStorageOk;
                    }
                    catch
                    {
                        // Compatibility copy only; .com remains the primary BigSeller cookie import.
                    }
                }
                if (ok) succeeded++;
            }
            catch (Exception ex)
            {
                Log($"Cookie lỗi {cookieName}: {ex.Message}");
            }
        }

        Log($"Cookie import: thử {attempted}, thành công {succeeded}.");
        return succeeded;
    }

    private async Task<bool> TrySetCookieWithBrowserStorageAsync(Dictionary<string, object?> payload)
    {
        try
        {
            using var browser = new ClientWebSocket();
            await browser.ConnectAsync(new Uri(await GetBrowserWebSocketUrlAsync()), CancellationToken.None);
            await SendCdpAsync(browser, 700, "Storage.setCookies", new { cookies = new[] { payload } });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryBuildBigSellerProPayload(
        Dictionary<string, object?> source,
        out Dictionary<string, object?> payload)
    {
        payload = new Dictionary<string, object?>(source);
        var changed = false;

        if (payload.TryGetValue("domain", out var domainValue) &&
            domainValue is string domain &&
            domain.Contains("bigseller.com", StringComparison.OrdinalIgnoreCase))
        {
            payload["domain"] = domain.Replace("bigseller.com", "bigseller.pro", StringComparison.OrdinalIgnoreCase);
            changed = true;
        }

        if (payload.TryGetValue("url", out var urlValue) &&
            urlValue is string url &&
            url.Contains("bigseller.com", StringComparison.OrdinalIgnoreCase))
        {
            payload["url"] = url.Replace("bigseller.com", "bigseller.pro", StringComparison.OrdinalIgnoreCase);
            changed = true;
        }

        return changed;
    }

    private readonly record struct ShopeeAccountLogin(
        string Username,
        string Password,
        string CookieDomain,
        string SpcF);

    private static void SanitizeCookiePayloadForCdp(
        Dictionary<string, object?> payload,
        bool persistSessionCookie)
    {
        foreach (var key in payload.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList())
            payload.Remove(key);

        foreach (var key in new[] { "name", "value", "url", "domain", "path", "sameSite", "priority", "sourceScheme" })
        {
            if (payload.TryGetValue(key, out var value) &&
                value is string s &&
                string.IsNullOrWhiteSpace(s))
                payload.Remove(key);
        }

        if (payload.TryGetValue("sameSite", out var sameSite) && sameSite is string ss)
        {
            var normalized = ss.Trim();
            if (normalized.Equals("no_restriction", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("none", StringComparison.OrdinalIgnoreCase))
                payload["sameSite"] = "None";
            else if (normalized.Equals("lax", StringComparison.OrdinalIgnoreCase))
                payload["sameSite"] = "Lax";
            else if (normalized.Equals("strict", StringComparison.OrdinalIgnoreCase))
                payload["sameSite"] = "Strict";
            else
                payload.Remove("sameSite");
        }

        if (payload.TryGetValue("expires", out var expires))
        {
            var value = expires switch
            {
                long l => l,
                int i => i,
                double d => d,
                _ => 0,
            };
            if (value <= 0)
            {
                if (persistSessionCookie)
                    payload["expires"] = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
                else
                    payload.Remove("expires");
            }
        }
        else if (persistSessionCookie)
        {
            payload["expires"] = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
        }

        if (payload.TryGetValue("sourcePort", out var sourcePort))
        {
            var value = sourcePort switch
            {
                long l => l,
                int i => i,
                double d => d,
                _ => 0,
            };
            if (value < 0)
                payload.Remove("sourcePort");
        }
    }

    private void SetStatus(string text)
    {
        _statusText = text;
        RaiseStatusChanged();
    }

    private void RaiseStatusChanged() => StatusChanged?.Invoke();

    private void Log(string message)
    {
        LogLine?.Invoke(message);
        _log(message);
    }
}

internal static class AppServices
{
    public static readonly HttpClient Http = new();
    public static readonly HttpClient DirectHttp = new(new HttpClientHandler
    {
        UseProxy = false,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };
}
