using ShopeeStatApp.Forms;

namespace ShopeeStatApp;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show(e.Exception.ToString(), "Lỗi khởi động", MessageBoxButtons.OK, MessageBoxIcon.Error);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            MessageBox.Show(e.ExceptionObject?.ToString() ?? "Unknown error",
                "Lỗi nghiêm trọng", MessageBoxButtons.OK, MessageBoxIcon.Error);

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
