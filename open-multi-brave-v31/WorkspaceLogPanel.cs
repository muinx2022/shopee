namespace OpenMultiBraveLauncherV3;

internal sealed class WorkspaceLogPanel : GroupBox
{
    public WorkspaceLogPanel()
    {
        Text = "Nh\u1eadt k\u00fd";
        Dock = DockStyle.Bottom;
        Height = 160;

        LogTabs = new TabControl { Dock = DockStyle.Fill };
        TotalLogTextBox = CreateLogTextBox();
        var totalLogPage = new TabPage("T\u1ed5ng");
        totalLogPage.Controls.Add(TotalLogTextBox);
        LogTabs.TabPages.Add(totalLogPage);
        Controls.Add(LogTabs);
    }

    public TabControl LogTabs { get; }

    public RichTextBox TotalLogTextBox { get; }

    public static RichTextBox CreateLogTextBox()
    {
        return new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = Color.FromArgb(22, 22, 26),
            ForeColor = Color.Gainsboro,
            Font = new Font("Consolas", 9F),
            BorderStyle = BorderStyle.None,
        };
    }
}
