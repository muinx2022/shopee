using System.Diagnostics;

namespace UpdateProduct;

internal sealed class BigSellerLoginSession : IAsyncDisposable
{
    private readonly BigSellerWorkflowSettings _settings;
    private readonly Action<string> _log;
    private Process? _process;

    public BigSellerLoginSession(BigSellerWorkflowSettings settings, Action<string> log)
    {
        _settings = settings;
        _log = log;
    }

    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_settings.BravePath))
            throw new FileNotFoundException($"Khong tim thay Brave: {_settings.BravePath}");

        Directory.CreateDirectory(_settings.ProfileDir);

        var args = string.Join(" ", [
            $"--remote-debugging-port={_settings.DebugPort}",
            $"--user-data-dir=\"{_settings.ProfileDir}\"",
            "--profile-directory=Default",
            "--new-window",
            "--no-first-run",
            "--no-default-browser-check",
            "--hide-crash-restore-bubble",
            $"\"{BigSellerCookieImporter.DefaultListingUrl}\"",
        ]);

        _process = Process.Start(new ProcessStartInfo
        {
            FileName = _settings.BravePath,
            Arguments = args,
            UseShellExecute = false,
        });

        _log($"Da mo BigSeller PID={_process?.Id.ToString() ?? "unknown"}, CDP={_settings.DebugPort}.");

        if (!await new CdpClient(_settings.DebugPort)
                .WaitForReadyAsync(30, 500, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Brave BigSeller chua san sang CDP.");

        await SeedCookieIfNeededAsync(cancellationToken).ConfigureAwait(false);
        await new CdpClient(_settings.DebugPort)
            .EnsurePageTargetAsync(url => url.Contains("bigseller", StringComparison.OrdinalIgnoreCase),
                BigSellerCookieImporter.DefaultListingUrl).ConfigureAwait(false);
    }

    public async Task SaveAndCloseAsync(CancellationToken cancellationToken = default)
    {
        if (!await new CdpClient(_settings.DebugPort)
                .WaitForReadyAsync(20, 500, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Brave BigSeller chua san sang CDP.");

        await BigSellerCookieImporter.TryExportProfileCookiesToFileAsync(
            _settings.DebugPort,
            _settings.BigSellerCookieFile,
            _log,
            verifySessionAlive: true).ConfigureAwait(false);

        Stop();
    }

    public void Stop()
    {
        try
        {
            if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: true);
        }
        catch { }
        finally
        {
            _process?.Dispose();
            _process = null;
        }
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }

    private async Task SeedCookieIfNeededAsync(CancellationToken cancellationToken)
    {
        try
        {
            var liveCookies = await BigSellerCookieImporter.GetBigSellerCookiesAsync(_settings.DebugPort)
                .ConfigureAwait(false);
            if (BigSellerCookieImporter.HasAuthCookie(liveCookies))
                return;

            await BigSellerCookieImporter.ImportFromFileAsync(
                _settings.DebugPort,
                _settings.BigSellerCookieFile,
                _log,
                reloadBigSellerTabs: false,
                navigateUrl: BigSellerCookieImporter.DefaultListingUrl,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
        }
    }
}
