using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace UpdateProduct;

internal static class AppSession
{
    public const int PortBlockSize = 1000;

    private static FileStream? _portLock;

    public static string BaseDirectory { get; } = AppContext.BaseDirectory;
    public static string ProjectSourceDirectory { get; } =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
    public static string RepoRootDirectory { get; } = FindRepoRoot();
    public static string SessionId { get; private set; } = "";
    public static string RootDirectory { get; private set; } = "";
    public static int PortOffset { get; private set; }
    public static int ApiPort { get; private set; }
    public static string ApiBase => $"http://127.0.0.1:{ApiPort}";

    public static void Initialize()
    {
        if (!string.IsNullOrWhiteSpace(RootDirectory))
            return;

        SessionId = $"run-{Process.GetCurrentProcess().Id}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..34];
        RootDirectory = Path.Combine(BaseDirectory, "runtime-sessions", SessionId);
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(ResolvePersistentDataPath());
        PortOffset = AllocatePortOffset();
        ApiPort = 8112 + (PortOffset / PortBlockSize);
        CleanupStaleSessions();
    }

    public static string ResolveDataPath(params string[] parts) =>
        Combine(RootDirectory, parts);

    public static string ResolvePersistentDataPath(params string[] parts) =>
        Combine(Path.Combine(ProjectSourceDirectory, "persistent-data"), parts);

    public static void Cleanup()
    {
        try { _portLock?.Dispose(); } catch { }
        _portLock = null;

        if (string.IsNullOrWhiteSpace(RootDirectory) || !Directory.Exists(RootDirectory))
            return;

        try
        {
            ClearReadOnlyAttributes(RootDirectory);
            Directory.Delete(RootDirectory, recursive: true);
        }
        catch
        {
            try { File.WriteAllText(Path.Combine(RootDirectory, ".delete-on-next-start"), DateTimeOffset.Now.ToString("O")); }
            catch { }
        }
    }

    private static int AllocatePortOffset()
    {
        var lockRoot = Path.Combine(BaseDirectory, "runtime-sessions", "_port-locks");
        Directory.CreateDirectory(lockRoot);

        for (var block = 0; block < 80; block++)
        {
            var offset = block * PortBlockSize;
            if (!PortsLookFree(offset))
                continue;

            var lockPath = Path.Combine(lockRoot, $"block-{block}.lock");
            try
            {
                _portLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                _portLock.SetLength(0);
                using var writer = new StreamWriter(_portLock, leaveOpen: true);
                writer.WriteLine(Process.GetCurrentProcess().Id);
                writer.WriteLine(DateTimeOffset.Now.ToString("O"));
                writer.Flush();
                _portLock.Position = 0;
                return offset;
            }
            catch (IOException)
            {
            }
        }

        throw new InvalidOperationException("Khong tim duoc block port trong cho Update Product.");
    }

    private static bool PortsLookFree(int offset) =>
        IsPortFree(10000 + offset) &&
        IsPortFree(10400 + offset) &&
        IsPortFree(8112 + (offset / PortBlockSize));

    public static bool IsPortFree(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void CleanupStaleSessions()
    {
        var root = Path.Combine(BaseDirectory, "runtime-sessions");
        if (!Directory.Exists(root))
            return;

        foreach (var dir in Directory.EnumerateDirectories(root, "run-*"))
        {
            if (string.Equals(dir, RootDirectory, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var info = new DirectoryInfo(dir);
                if (DateTime.Now - info.LastWriteTime < TimeSpan.FromHours(12) &&
                    !File.Exists(Path.Combine(dir, ".delete-on-next-start")))
                    continue;

                ClearReadOnlyAttributes(dir);
                Directory.Delete(dir, recursive: true);
            }
            catch { }
        }
    }

    private static void ClearReadOnlyAttributes(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
        }
    }

    private static string Combine(string root, string[] parts)
    {
        var all = new string[parts.Length + 1];
        all[0] = root;
        Array.Copy(parts, 0, all, 1, parts.Length);
        return Path.Combine(all);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(ProjectSourceDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "update-product-python")))
                return dir.FullName;
            dir = dir.Parent;
        }

        return Path.GetFullPath(Path.Combine(ProjectSourceDirectory, ".."));
    }
}
