namespace OpenMultiBraveLauncherV3;

using System.Text.RegularExpressions;

internal sealed class ImportManagerForm : Form
{
    private readonly TextBox _shopeeTextBox;
    private readonly TextBox _proxyTextBox;
    private bool _normalizingShopeeText;

    public string ShopeeAccountsText => _shopeeTextBox.Text;
    public string ProxyKeysText => _proxyTextBox.Text;

    public ImportManagerForm()
    {
        Text = "Import";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(900, 620);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 3,
            ColumnCount = 1,
            Padding = new Padding(10),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _shopeeTextBox = CreateMultilineBox(
            "username|password|.shopee.vn=SPC_F=value\r\nusername2|password2|.shopee.vn=SPC_F=value");
        _shopeeTextBox.TextChanged += (_, _) => NormalizeShopeeTextBox();
        root.Controls.Add(CreateGroup("Shopee account", _shopeeTextBox), 0, 0);

        _proxyTextBox = CreateMultilineBox("kiotproxy-key-1\r\nkiotproxy-key-2\r\nkiotproxy-key-3");
        root.Controls.Add(CreateGroup("Proxy", _proxyTextBox), 0, 1);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
        };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 88, Height = 32 };
        var cancel = new Button { Text = "Hủy", DialogResult = DialogResult.Cancel, Width = 88, Height = 32 };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        root.Controls.Add(buttons, 0, 2);
        Controls.Add(root);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private static GroupBox CreateGroup(string title, TextBox textBox)
    {
        var group = new GroupBox
        {
            Text = title,
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
        };
        group.Controls.Add(textBox);
        return group;
    }

    private static TextBox CreateMultilineBox(string placeholder) => new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        AcceptsReturn = true,
        AcceptsTab = true,
        PlaceholderText = placeholder,
        Font = new Font(FontFamily.GenericMonospace, 9f),
    };

    private void NormalizeShopeeTextBox()
    {
        if (_normalizingShopeeText)
            return;

        var normalized = NormalizeShopeeAccountsText(_shopeeTextBox.Text);
        if (string.Equals(normalized, _shopeeTextBox.Text, StringComparison.Ordinal))
            return;

        _normalizingShopeeText = true;
        try
        {
            var selectionStart = Math.Min(normalized.Length, _shopeeTextBox.SelectionStart);
            _shopeeTextBox.Text = normalized;
            _shopeeTextBox.SelectionStart = selectionStart;
            _shopeeTextBox.SelectionLength = 0;
        }
        finally
        {
            _normalizingShopeeText = false;
        }
    }

    private static string NormalizeShopeeAccountsText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var matches = Regex.Matches(
            text,
            @"[^\s|]+\|[^\s|]+\|\.shopee\.vn=SPC_F=[^\s|]+",
            RegexOptions.IgnoreCase);
        if (matches.Count <= 1)
            return text;

        return string.Join(Environment.NewLine, matches.Select(m => m.Value.Trim()));
    }
}
