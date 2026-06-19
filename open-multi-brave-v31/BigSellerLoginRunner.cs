using System.Diagnostics;
using System.Net.WebSockets;

namespace OpenMultiBraveLauncherV3;

/// <summary>
/// Mở một Brave riêng KHÔNG proxy (dùng IP máy thật) để đăng nhập BigSeller và lưu cookie.
/// BigSeller cần được đăng nhập từ IP cố định của máy — KHÔNG dùng proxy của các Shopee instance.
/// </summary>
internal sealed class BigSellerLoginRunner : IDisposable
{
    private readonly int _cdpPort;
    private Process? _braveProcess;
    private bool _disposed;

    private BigSellerLoginRunner(int cdpPort, Process? process)
    {
        _cdpPort = cdpPort;
        _braveProcess = process;
    }

    public bool IsBraveRunning => _braveProcess is { HasExited: false };

    private static readonly string ProfileDir =
        AppSession.ResolvePersistentDataPath("bigseller-login", "default");

    /// <summary>
    /// Khởi động Brave không proxy rồi mở trang BigSeller.
    /// Caller phải Dispose runner sau khi xong.
    /// </summary>
    public static async Task<BigSellerLoginRunner> LaunchAsync(
        string braveExe,
        string sourceUserData,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        // Kill tất cả Brave còn sót từ phiên trước dùng cùng profile này.
        // Nếu không kill, lần sau launch Brave sẽ forward sang instance cũ rồi stub thoát → CDP port mới không ai nghe.
        KillLingeringBraveForProfile(log);
        await WaitForProfileUnlockedAsync(log, cancellationToken).ConfigureAwait(false);

        var port = PortAllocator.Shared.AllocateCookiePort();
        Process? process = null;
        try
        {
            Directory.CreateDirectory(ProfileDir);

            // Tạo sub-folder Default nếu chưa có (Brave sẽ tự init khi lần đầu chạy)
            var targetDefault = Path.Combine(ProfileDir, "Default");
            if (!Directory.Exists(targetDefault))
                Directory.CreateDirectory(targetDefault);

            BraveProfileManager.PrepareProfileForLaunch(ProfileDir);

            // Không truyền proxyServer → Brave chạy bằng IP máy thật
            var args = BraveProfileManager.BuildBraveArguments(
                port, ProfileDir, proxyServer: null, log, sourceUserData,
                loadRunnerExtension: false);

            process = Process.Start(new ProcessStartInfo
            {
                FileName = braveExe,
                Arguments = args,
                UseShellExecute = false,
            });
            log?.Invoke($"Đã mở Brave BigSeller (PID={process?.Id}, CDP port={port}, KHÔNG proxy).");

            var runner = new BigSellerLoginRunner(port, process);

            // Chờ CDP sẵn sàng
            var cdpClient = new CdpClient(port);
            var ready = await cdpClient.WaitForReadyAsync(attempts: 40, delayMs: 500, cancellationToken)
                .ConfigureAwait(false);
            if (!ready)
                throw new InvalidOperationException("Không kết nối được CDP BigSeller profile (Brave chưa khởi động xong).");

            // Mở trang BigSeller để user đăng nhập
            try
            {
                var wsUrl = await cdpClient.EnsurePageTargetAsync(
                    url => url.Contains("bigseller", StringComparison.OrdinalIgnoreCase),
                    BigSellerCookieImporter.DefaultListingUrl).ConfigureAwait(false);

                using var page = new ClientWebSocket();
                await page.ConnectAsync(new Uri(wsUrl), cancellationToken).ConfigureAwait(false);
                await CdpClient.SendAsync(page, 1, "Page.navigate",
                    new { url = BigSellerCookieImporter.DefaultListingUrl }).ConfigureAwait(false);

                log?.Invoke("Đã mở trang BigSeller. Vui lòng đăng nhập trong cửa sổ Brave vừa mở.");
            }
            catch (Exception ex)
            {
                log?.Invoke($"Mở trang BigSeller: {ex.Message} — hãy tự điều hướng đến bigseller.com.");
            }

            return runner;
        }
        catch
        {
            PortAllocator.Shared.Release(port);
            try { process?.Kill(entireProcessTree: true); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Theo dõi cookie muc_token. Khi phát hiện đăng nhập thành công → lưu cookie ra file.
    /// Chạy tối đa ~10 phút.
    /// </summary>
    public async Task<bool> PollForLoginAndSaveCookiesAsync(
        string cookieFilePath,
        Action<string>? log = null,
        Action<bool>? onLoginDetected = null,
        CancellationToken cancellationToken = default)
    {
        const int pollIntervalMs = 3000;
        const int maxPolls = 200; // ~10 phút

        for (var i = 0; i < maxPolls; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(pollIntervalMs, cancellationToken).ConfigureAwait(false);

            if (_braveProcess?.HasExited == true)
            {
                log?.Invoke("Brave đã đóng — dừng theo dõi đăng nhập.");
                return false;
            }

            try
            {
                var cookies = await BigSellerCookieImporter.GetBigSellerCookiesAsync(_cdpPort).ConfigureAwait(false);
                if (!BigSellerCookieImporter.HasAuthCookie(cookies))
                {
                    if (i % 10 == 0 && i > 0)
                        log?.Invoke($"Đang chờ đăng nhập... ({i * pollIntervalMs / 1000}s)");
                    continue;
                }

                if (!BigSellerCookieImporter.TryWriteCookieFile(cookieFilePath, cookies, log))
                    return false;

                log?.Invoke($"Đăng nhập BigSeller thành công! Cookie đã lưu: {Path.GetFileName(cookieFilePath)}");
                onLoginDetected?.Invoke(true);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (i % 10 == 0)
                    log?.Invoke($"Kiểm tra cookie: {ex.Message}");
            }
        }

        log?.Invoke("Hết thời gian chờ đăng nhập BigSeller (10 phút).");
        return false;
    }

    /// <summary>Dừng Brave BigSeller login (không xóa profile để giữ session).</summary>
    public void Stop()
    {
        try
        {
            if (_braveProcess is { HasExited: false })
            {
                _braveProcess.CloseMainWindow();
                if (!_braveProcess.WaitForExit(2000))
                    _braveProcess.Kill(entireProcessTree: true);
            }
        }
        catch { }
        finally
        {
            try { _braveProcess?.Dispose(); } catch { }
            _braveProcess = null;
        }

        // Kill tất cả tiến trình Brave còn lại dùng profile này (GPU, renderer, utility...)
        // Nếu không kill, lần sau launch sẽ forward sang instance cũ rồi stub thoát → CDP port mới không ai nghe.
        KillLingeringBraveForProfile(null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        PortAllocator.Shared.Release(_cdpPort);
    }

    private static void KillLingeringBraveForProfile(Action<string>? log)
    {
        try { BraveProcessReaper.KillByUserDataDir(ProfileDir, log); } catch { }
    }

    private static async Task WaitForProfileUnlockedAsync(Action<string>? log, CancellationToken cancellationToken)
    {
        // Brave tạo SingletonLock khi đang chạy. Chờ file biến mất (tối đa 5 giây).
        var lockFile = Path.Combine(ProfileDir, "SingletonLock");
        for (var i = 0; i < 10; i++)
        {
            if (!File.Exists(lockFile))
                return;
            if (i == 0) log?.Invoke("Đợi profile BigSeller được giải phóng...");
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
    }
}
