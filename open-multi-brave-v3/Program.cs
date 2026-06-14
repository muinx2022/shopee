using System.Windows.Forms;

namespace OpenMultiBraveLauncherV3;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => ShowFatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            ShowFatal(e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "Unknown error"));

        try
        {
            AppSession.Initialize();
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            ShowFatal(ex);
        }
        finally
        {
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
