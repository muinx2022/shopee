using System.Windows.Forms;

namespace OpenMultiBraveLauncherV3;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) =>
        {
            AppDiagnostics.LogException("Application.ThreadException", e.Exception);
            ShowFatal(e.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "Unknown error");
            AppDiagnostics.LogException("AppDomain.UnhandledException", ex);
            ShowFatal(ex);
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => AppDiagnostics.Log("AppDomain.ProcessExit.");

        try
        {
            AppSession.Initialize();
            AppDiagnostics.Log("Application starting ManagerForm.");
            Application.Run(new ManagerForm());
            AppDiagnostics.Log("Application.Run returned.");
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogException("Program.Main catch", ex);
            ShowFatal(ex);
        }
        finally
        {
            AppDiagnostics.Log("Application finally cleanup.");
            ApiServerHelper.StopOwnedApiServer();
            AppSession.Cleanup();
        }
    }

    private static void ShowFatal(Exception ex)
    {
        MessageBox.Show(
            ex + Environment.NewLine + Environment.NewLine + ex.StackTrace,
            "Multi Brave Manager v3 — lỗi",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}
