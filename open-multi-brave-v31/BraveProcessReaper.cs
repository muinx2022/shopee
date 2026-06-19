using System.Diagnostics;
using System.Management;

namespace OpenMultiBraveLauncherV3;

/// <summary>
/// Dọn sạch tiến trình Brave còn sót của một profile. Brave (Chromium) hay fork rồi thoát tiến
/// trình gốc mà launcher giữ tham chiếu → <see cref="Process.Kill(bool)"/> trên PID gốc bỏ sót
/// browser thật + GPU/renderer/utility con. Reaper tìm theo command-line (chứa đúng
/// <c>--user-data-dir</c> duy nhất của profile) để giết tận gốc, tránh tích tụ zombie khi xoay vòng.
/// </summary>
internal static class BraveProcessReaper
{
    /// <summary>
    /// Giết mọi brave.exe có command-line tham chiếu tới <paramref name="userDataDir"/>.
    /// Khớp theo đường dẫn user-data-dir duy nhất nên không đụng Brave cá nhân của user.
    /// Best-effort, không ném lỗi.
    /// </summary>
    public static int KillByUserDataDir(string? userDataDir, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(userDataDir))
            return 0;

        var needle = userDataDir.Trim().TrimEnd('\\', '/');
        if (needle.Length == 0)
            return 0;

        var killed = 0;
        foreach (var pid in FindBravePidsByCommandLine(needle, log))
        {
            if (TryKillTree(pid))
                killed++;
        }

        if (killed > 0)
            log?.Invoke($"Đã dọn {killed} tiến trình Brave còn sót của profile.");
        return killed;
    }

    private static List<int> FindBravePidsByCommandLine(string userDataDirNeedle, Action<string>? log)
    {
        var pids = new List<int>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'brave.exe'");
            using var results = searcher.Get();
            foreach (var obj in results)
            {
                try
                {
                    var commandLine = obj["CommandLine"] as string;
                    if (string.IsNullOrEmpty(commandLine))
                        continue;

                    if (commandLine.Contains(userDataDirNeedle, StringComparison.OrdinalIgnoreCase))
                    {
                        var pid = Convert.ToInt32(obj["ProcessId"]);
                        if (pid > 0)
                            pids.Add(pid);
                    }
                }
                catch
                {
                    // bỏ qua tiến trình không đọc được thuộc tính
                }
                finally
                {
                    obj.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            // WMI có thể bị tắt/hạn chế quyền — không chặn việc đóng profile.
            log?.Invoke($"Quét tiến trình Brave (WMI) lỗi: {ex.Message}");
        }

        return pids;
    }

    private static bool TryKillTree(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            if (proc.HasExited)
                return false;
            proc.Kill(entireProcessTree: true);
            proc.WaitForExit(2000);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
