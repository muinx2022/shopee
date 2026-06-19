namespace OpenMultiBraveLauncherV3;

internal sealed class PendingScrapeLinksForm : Form
{
    private readonly Label _summaryLabel;
    private readonly TextBox _linksTextBox;

    public PendingScrapeLinksForm(string profileName, IReadOnlyList<PendingScrapeLink> links, int defaultMax)
    {
        Text = $"Nhung link chua scrape - {profileName}";
        StartPosition = FormStartPosition.CenterParent;
        Width = 680;
        Height = 520;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _summaryLabel = new Label
        {
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8),
            Text = $"Tong {links.Count} link chua scrape.",
        };
        root.Controls.Add(_summaryLabel, 0, 0);

        _linksTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font(FontFamily.GenericMonospace, 9f),
            Text = FormatLinks(links),
        };
        root.Controls.Add(_linksTextBox, 0, 1);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0, 10, 0, 0),
        };

        var closeButton = new Button
        {
            Text = "Close",
            DialogResult = DialogResult.Cancel,
            Width = 92,
            Height = 32,
            Margin = new Padding(8, 0, 0, 0),
        };

        RunButton = new Button
        {
            Text = "Chay scrape",
            DialogResult = DialogResult.OK,
            Width = 112,
            Height = 32,
            Enabled = links.Count > 0,
            Margin = new Padding(8, 0, 0, 0),
        };

        MaxInput = new NumericUpDown
        {
            Minimum = 1,
            Maximum = Math.Max(1, links.Count),
            Value = Math.Clamp(defaultMax, 1, Math.Max(1, links.Count)),
            Width = 76,
            Height = 32,
            Enabled = links.Count > 0,
            Margin = new Padding(4, 4, 8, 0),
        };

        var maxLabel = new Label
        {
            Text = "Max",
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 0),
            Margin = new Padding(0),
        };

        buttons.Controls.Add(closeButton);
        buttons.Controls.Add(RunButton);
        buttons.Controls.Add(MaxInput);
        buttons.Controls.Add(maxLabel);
        root.Controls.Add(buttons, 0, 2);

        AcceptButton = RunButton;
        CancelButton = closeButton;
        Controls.Add(root);
    }

    public NumericUpDown MaxInput { get; }
    public Button RunButton { get; }
    public int MaxToRun => (int)MaxInput.Value;

    private static string FormatLinks(IReadOnlyList<PendingScrapeLink> links)
    {
        if (links.Count == 0)
            return "Khong co link chua scrape.";

        return string.Join(
            Environment.NewLine,
            links
                .OrderBy(x => x.RowNumber)
                .Select(x =>
                {
                    var reason = string.IsNullOrWhiteSpace(x.Reason) ? "" : $" | {x.Reason}";
                    return $"{x.RowNumber}\t{x.Link}{reason}";
                }));
    }
}
