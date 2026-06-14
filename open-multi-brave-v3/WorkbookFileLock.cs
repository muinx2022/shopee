namespace OpenMultiBraveLauncherV3;

/// <summary>
/// Cross-process lock for XLSX access. Uses a sidecar "{workbook}.lock" file,
/// matching update-product-python workbook_file_lock.
/// </summary>
internal sealed class WorkbookFileLockHandle : IDisposable
{
    private FileStream? _stream;

    public static async Task<WorkbookFileLockHandle> AcquireAsync(
        string workbookPath,
        CancellationToken ct,
        int timeoutMs = 120_000)
    {
        if (string.IsNullOrWhiteSpace(workbookPath))
            throw new ArgumentException("Workbook path is required.", nameof(workbookPath));

        var lockPath = $"{Path.GetFullPath(workbookPath)}.lock";
        var lockDir = Path.GetDirectoryName(lockPath);
        if (!string.IsNullOrWhiteSpace(lockDir))
            Directory.CreateDirectory(lockDir);

        var deadline = Environment.TickCount64 + timeoutMs;
        Exception? lastError = null;

        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
                return new WorkbookFileLockHandle { _stream = stream };
            }
            catch (IOException ex)
            {
                lastError = ex;
                await Task.Delay(100, ct);
            }
            catch (UnauthorizedAccessException ex)
            {
                lastError = ex;
                await Task.Delay(100, ct);
            }
        }

        throw new TimeoutException(
            $"Không acquire được workbook lock sau {timeoutMs}ms: {lockPath}",
            lastError);
    }

    public void Dispose()
    {
        try
        {
            _stream?.Dispose();
        }
        catch
        {
            // ignore
        }
        finally
        {
            _stream = null;
        }
    }
}
