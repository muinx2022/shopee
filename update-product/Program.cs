namespace UpdateProduct;

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

        try
        {
            AppSession.Initialize();
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogException("Program.Main", ex);
            ShowFatal(ex);
        }
        finally
        {
            AppSession.Cleanup();
        }
    }

    private static void ShowFatal(Exception ex)
    {
        MessageBox.Show(ex.ToString(), "Update Product - loi", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
