namespace UpdateProduct;

internal static class AppDiagnostics
{
    private static readonly object LockObj = new();

    public static string LogPath =>
        Path.Combine(AppSession.ProjectSourceDirectory, "runtime-diagnostics.log");

    public static void Log(string message)
    {
        try
        {
            lock (LockObj)
            {
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }

    public static void LogException(string context, Exception ex) =>
        Log($"{context}: {ex}");
}
