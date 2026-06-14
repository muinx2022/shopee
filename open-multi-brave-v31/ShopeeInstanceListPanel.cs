namespace OpenMultiBraveLauncherV3;

internal sealed class ShopeeInstanceListPanel : TableLayoutPanel
{
    public ShopeeInstanceListPanel(EventHandler addRequested, EventHandler removeRequested)
    {
        Dock = DockStyle.Fill;
        RowCount = 2;
        ColumnCount = 1;
        Padding = new Padding(0);
        RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        RowStyles.Add(new RowStyle(SizeType.AutoSize));

        InstanceList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = true,
        };
        InstanceList.Columns.Add("T\u00ean", 100);
        InstanceList.Columns.Add("Tr\u1ea1ng th\u00e1i", 80);
        InstanceList.Columns.Add("Ti\u1ebfn \u0111\u1ed9", 200);
        InstanceList.Columns.Add("Proxy", 100);
        Controls.Add(InstanceList, 0, 0);

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
        instanceButtons.Controls.Add(addInstanceButton);
        instanceButtons.Controls.Add(removeInstanceButton);
        Controls.Add(instanceButtons, 0, 1);
    }

    public ListView InstanceList { get; }

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
