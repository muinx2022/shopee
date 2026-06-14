namespace OpenMultiBraveLauncherV3;

internal sealed class ShopeeActionToolbar : TableLayoutPanel
{
    public ShopeeActionToolbar(LauncherSettingsFile settings, string sheetLabelText)
    {
        Dock = DockStyle.Top;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        ColumnCount = 1;
        RowCount = 3;
        Margin = new Padding(0, 0, 0, 8);
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var infoGroup = new GroupBox
        {
            Text = "Th\u00f4ng tin chung",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8),
            Margin = new Padding(0, 0, 0, 8),
        };
        var infoRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = true,
            Margin = new Padding(0),
        };
        infoRow.Controls.Add(new Label
        {
            Text = "Sheet \u0111ang ch\u1ecdn:",
            AutoSize = true,
            Padding = new Padding(0, 4, 6, 0),
        });
        AutoShopSheetLabel = new Label
        {
            AutoSize = true,
            ForeColor = Color.DimGray,
            Padding = new Padding(0, 4, 12, 0),
            Text = sheetLabelText,
        };
        infoRow.Controls.Add(AutoShopSheetLabel);
        infoGroup.Controls.Add(infoRow);

        var actionGroup = new GroupBox
        {
            Text = "Action",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8),
            Margin = new Padding(0, 0, 0, 8),
        };
        var actionRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = true,
            Margin = new Padding(0),
        };

        RunSelectedButton = CreateTopActionButton("Ch\u1ea1y \u0111\u00e3 ch\u1ecdn", UiButtonHelper.Run());
        StartAutoButton = CreateTopActionButton("Ch\u1ea1y t\u1ef1 \u0111\u1ed9ng", UiButtonHelper.Run());
        StopAutoButton = CreateTopActionButton("D\u1eebng t\u1ef1 \u0111\u1ed9ng", UiButtonHelper.Stop());
        StopAllButton = CreateTopActionButton("D\u1eebng t\u1ea5t c\u1ea3", UiButtonHelper.Stop());
        actionRow.Controls.Add(RunSelectedButton);
        actionRow.Controls.Add(StopAllButton);
        actionGroup.Controls.Add(actionRow);

        var autoGroup = new GroupBox
        {
            Text = "Auto",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8),
            Margin = new Padding(0),
        };
        var autoTable = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 8,
            RowCount = 3,
            Margin = new Padding(0),
        };
        autoTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        autoTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
        autoTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        autoTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
        autoTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        autoTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
        autoTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        autoTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        autoTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        autoTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        autoTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        AutoRunCheckBox = new CheckBox
        {
            Text = "Ch\u1ea1y t\u1ef1 \u0111\u1ed9ng",
            AutoSize = true,
            Checked = settings.AutoRunEnabled,
            Margin = new Padding(0, 6, 12, 0),
        };
        MaxConcurrentInput = CreateAutoNumeric(1, 50, Math.Clamp(settings.MaxConcurrentProfiles, 1, 50));
        AutoFromInput = CreateAutoNumeric(1, 999, Math.Max(1, settings.AutoRunFromInstance));
        AutoToInput = CreateAutoNumeric(0, 999, Math.Max(0, settings.AutoRunToInstance));
        RangeStartRowInput = CreateAutoNumeric(2, 1_000_000, Math.Max(2, settings.RangeStartRow), width: 96);
        RangeRowsPerProfileInput = CreateAutoNumeric(1, 100_000, Math.Max(1, settings.RangeRowsPerProfile), width: 96);
        AutoSetRowButton = new Button
        {
            Text = "Set row",
            AutoSize = true,
            MinimumSize = new Size(88, 28),
            Margin = new Padding(0, 2, 0, 2),
        };

        autoTable.Controls.Add(AutoRunCheckBox, 0, 0);
        autoTable.SetColumnSpan(AutoRunCheckBox, 2);
        autoTable.Controls.Add(StartAutoButton, 2, 0);
        autoTable.SetColumnSpan(StartAutoButton, 2);
        autoTable.Controls.Add(StopAutoButton, 4, 0);
        autoTable.SetColumnSpan(StopAutoButton, 2);
        autoTable.Controls.Add(new Label { Text = "Instance t\u1eeb", AutoSize = true, Padding = new Padding(0, 7, 6, 0) }, 0, 1);
        autoTable.Controls.Add(AutoFromInput, 1, 1);
        autoTable.Controls.Add(new Label { Text = "\u0111\u1ebfn", AutoSize = true, Padding = new Padding(10, 7, 6, 0) }, 2, 1);
        autoTable.Controls.Add(AutoToInput, 3, 1);
        autoTable.Controls.Add(new Label { Text = "Max", AutoSize = true, Padding = new Padding(10, 7, 6, 0) }, 4, 1);
        autoTable.Controls.Add(MaxConcurrentInput, 5, 1);
        autoTable.Controls.Add(new Label { Text = "T\u1eeb d\u00f2ng", AutoSize = true, Padding = new Padding(0, 7, 6, 0) }, 0, 2);
        autoTable.Controls.Add(RangeStartRowInput, 1, 2);
        autoTable.Controls.Add(new Label { Text = "S\u1ed1 d\u00f2ng", AutoSize = true, Padding = new Padding(10, 7, 6, 0) }, 2, 2);
        var rowPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        rowPanel.Controls.Add(RangeRowsPerProfileInput);
        rowPanel.Controls.Add(AutoSetRowButton);
        autoTable.Controls.Add(rowPanel, 3, 2);
        autoTable.SetColumnSpan(rowPanel, 5);

        autoGroup.Controls.Add(autoTable);
        Controls.Add(infoGroup, 0, 0);
        Controls.Add(actionGroup, 0, 1);
        Controls.Add(autoGroup, 0, 2);
    }

    public Button StartAutoButton { get; }
    public Button StopAutoButton { get; }
    public Button RunSelectedButton { get; }
    public Button StopAllButton { get; }
    public Button AutoSetRowButton { get; }
    public Label AutoShopSheetLabel { get; }
    public CheckBox AutoRunCheckBox { get; }
    public NumericUpDown MaxConcurrentInput { get; }
    public NumericUpDown AutoFromInput { get; }
    public NumericUpDown AutoToInput { get; }
    public NumericUpDown RangeStartRowInput { get; }
    public NumericUpDown RangeRowsPerProfileInput { get; }

    private static Button CreateTopActionButton(string text, Bitmap icon)
    {
        var button = new Button
        {
            Width = 132,
            Height = 34,
            Margin = new Padding(0, 0, 8, 0),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        UiButtonHelper.Style(button, icon, text);
        return button;
    }

    private static NumericUpDown CreateAutoNumeric(int min, int max, int value, int width = 76)
    {
        return new NumericUpDown
        {
            Minimum = min,
            Maximum = max,
            Width = width,
            Value = Math.Clamp(value, min, max),
            Margin = new Padding(0, 2, 8, 2),
        };
    }
}
