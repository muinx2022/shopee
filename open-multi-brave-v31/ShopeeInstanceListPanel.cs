namespace OpenMultiBraveLauncherV3;

internal sealed class ShopeeInstanceListPanel : TableLayoutPanel
{
    public ShopeeInstanceListPanel(
        EventHandler addRequested,
        EventHandler removeRequested,
        EventHandler resolveCaptchaRequested)
    {
        Dock = DockStyle.Fill;
        RowCount = 2;
        ColumnCount = 1;
        Padding = new Padding(0);
        RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        RowStyles.Add(new RowStyle(SizeType.AutoSize));

        InstanceTabs = new TabControl
        {
            Dock = DockStyle.Fill,
            DrawMode = TabDrawMode.OwnerDrawFixed,
            ItemSize = new Size(108, 28),
            SizeMode = TabSizeMode.Fixed,
        };
        InstanceTabs.DrawItem += DrawInstanceTab;
        InstanceList = CreateInstanceList();
        ErrorInstanceList = CreateInstanceList();
        InstanceTabs.TabPages.Add(new TabPage("Normal") { Controls = { InstanceList } });
        InstanceTabs.TabPages.Add(new TabPage("Error") { Controls = { ErrorInstanceList } });
        Controls.Add(InstanceTabs, 0, 0);

        var instanceButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(0, 6, 0, 0),
        };
        var addInstanceButton = CreateTopActionButton("Th\u00eam instance", UiButtonHelper.Add(), 138);
        addInstanceButton.Click += addRequested;
        var removeInstanceButton = CreateTopActionButton("X\u00f3a instance", UiButtonHelper.Remove(), 138);
        removeInstanceButton.Click += removeRequested;
        ResolveCaptchaButton = CreateTopActionButton("\u0110\u00e3 gi\u1ea3i captcha", UiButtonHelper.Run(), 150);
        ResolveCaptchaButton.Click += resolveCaptchaRequested;
        instanceButtons.Controls.Add(addInstanceButton);
        instanceButtons.Controls.Add(removeInstanceButton);
        instanceButtons.Controls.Add(ResolveCaptchaButton);
        Controls.Add(instanceButtons, 0, 1);
    }

    public TabControl InstanceTabs { get; }
    public ListView InstanceList { get; }
    public ListView ErrorInstanceList { get; }
    public Button ResolveCaptchaButton { get; }

    private static ListView CreateInstanceList()
    {
        var list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = true,
        };
        list.Columns.Add("#", 40);
        list.Columns.Add("T\u00ean", 130);
        list.Columns.Add("Tr\u1ea1ng th\u00e1i", 130);
        list.Columns.Add("Ti\u1ebfn \u0111\u1ed9", 250);
        list.Columns.Add("Proxy", 140);
        return list;
    }

    private static void DrawInstanceTab(object? sender, DrawItemEventArgs e)
    {
        if (sender is not TabControl tabs)
            return;

        var selected = e.Index == tabs.SelectedIndex;
        var bounds = e.Bounds;
        var back = selected ? Color.FromArgb(0, 120, 215) : SystemColors.Control;
        var fore = selected ? Color.White : SystemColors.ControlText;

        using var backBrush = new SolidBrush(back);
        e.Graphics.FillRectangle(backBrush, bounds);
        TextRenderer.DrawText(
            e.Graphics,
            tabs.TabPages[e.Index].Text,
            selected ? new Font(tabs.Font, FontStyle.Bold) : tabs.Font,
            bounds,
            fore,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private static Button CreateTopActionButton(string text, Bitmap icon, int width)
    {
        var button = new Button
        {
            Width = width,
            Height = 34,
            Margin = new Padding(0, 0, 8, 0),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        UiButtonHelper.Style(button, icon, text);
        return button;
    }
}
