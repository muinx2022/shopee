using System.Windows.Forms;

namespace OpenMultiBraveLauncherV3;

internal static class ProxyLaunchGuard
{
    public static bool ConfirmOrAllow(
        InstanceConfig config,
        BraveInstanceSession session,
        IWin32Window? owner,
        string? dialogTitle = null,
        string? messagePrefix = null)
    {
        if (!config.RequireProxy ||
            session.NoProxyAllowed ||
            !string.IsNullOrWhiteSpace(config.KiotProxyKey) ||
            !string.IsNullOrWhiteSpace(config.ManualProxy))
            return true;

        var prefix = string.IsNullOrWhiteSpace(messagePrefix)
            ? "Instance"
            : messagePrefix.TrimEnd(':').Trim();
        var message =
            $"{prefix}: chưa có proxy (KiotProxy key / proxy thủ công).\n\n"
            + "Vẫn mở? Shopee sẽ đăng nhập bằng IP máy — tài khoản có thể bị nghi ngờ.";

        var answer = MessageBox.Show(
            owner,
            message,
            dialogTitle ?? config.DisplayName,
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning);
        if (answer != DialogResult.OK)
            return false;

        session.AllowNoProxyForSession();
        return true;
    }
}
