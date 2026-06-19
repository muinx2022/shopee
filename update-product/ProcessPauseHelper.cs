using System.Diagnostics;
using System.Runtime.InteropServices;

namespace UpdateProduct;

internal static class ProcessPauseHelper
{
    private const int ProcessSuspendResume = 0x0800;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(IntPtr processHandle);

    public static bool TrySuspend(Process process)
    {
        if (process.HasExited)
            return false;

        var handle = OpenProcess(ProcessSuspendResume, false, process.Id);
        if (handle == IntPtr.Zero)
            return false;

        try
        {
            return NtSuspendProcess(handle) == 0;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public static bool TryResume(Process process)
    {
        if (process.HasExited)
            return false;

        var handle = OpenProcess(ProcessSuspendResume, false, process.Id);
        if (handle == IntPtr.Zero)
            return false;

        try
        {
            return NtResumeProcess(handle) == 0;
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}
