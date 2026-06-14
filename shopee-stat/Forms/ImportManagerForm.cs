using System.Text.RegularExpressions;

namespace ShopeeStatApp.Forms;

internal sealed class ImportManagerForm : Form
{
    private readonly TextBox _shopeeTextBox;
    private readonly TextBox _proxyTextBox;
    private bool _normalizing;

    public string ShopeeAccountsText => _shopeeTextBox.Text;
    public string ProxyKeysText => _proxyTextBox.Text;

    public ImportManagerForm()
    {
        Text = "Import tài khoản";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(900, 600);

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

        _shopeeTextBox = MakeBox("username|password|.shopee.vn=SPC_F=value");
        _shopeeTextBox.TextChanged += (_, _) => NormalizeShopee();
        root.Controls.Add(Wrap("Shopee account  (username|password|.shopee.vn=SPC_F=value)", _shopeeTextBox), 0, 0);

        _proxyTextBox = MakeBox("kiotproxy-key-1\r\nkiotproxy-key-2");
        root.Controls.Add(Wrap("Proxy keys  (kiotproxy, một key mỗi dòng)", _proxyTextBox), 0, 1);

        var btns = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
        };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 88, Height = 32 };
        var cancel = new Button { Text = "Hủy", DialogResult = DialogResult.Cancel, Width = 88, Height = 32 };
        btns.Controls.Add(cancel);
        btns.Controls.Add(ok);
        root.Controls.Add(btns, 0, 2);

        Controls.Add(root);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void NormalizeShopee()
    {
        if (_normalizing) return;
        var normalized = NormalizeAccounts(_shopeeTextBox.Text);
        if (string.Equals(normalized, _shopeeTextBox.Text, StringComparison.Ordinal)) return;
        _normalizing = true;
        try
        {
            var sel = Math.Min(normalized.Length, _shopeeTextBox.SelectionStart);
            _shopeeTextBox.Text = normalized;
            _shopeeTextBox.SelectionStart = sel;
            _shopeeTextBox.SelectionLength = 0;
        }
        finally { _normalizing = false; }
    }

    private static string NormalizeAccounts(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var matches = Regex.Matches(text,
            @"[^\s|]+\|[^\s|]+\|\.shopee\.vn=SPC_F=[^\s|]+",
            RegexOptions.IgnoreCase);
        if (matches.Count <= 1) return text;
        return string.Join(Environment.NewLine, matches.Select(m => m.Value.Trim()));
    }

    private static GroupBox Wrap(string title, TextBox tb)
    {
        var g = new GroupBox { Text = title, Dock = DockStyle.Fill, Padding = new Padding(8) };
        g.Controls.Add(tb);
        return g;
    }

    private static TextBox MakeBox(string placeholder) => new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        AcceptsReturn = true,
        PlaceholderText = placeholder,
        Font = new Font(FontFamily.GenericMonospace, 9f),
    };
}
