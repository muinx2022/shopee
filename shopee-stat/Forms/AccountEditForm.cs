namespace ShopeeStatApp.Forms;

internal sealed class AccountEditForm : Form
{
    private readonly TextBox _accountLoginBox;
    private readonly TextBox _kiotKeyBox;
    private readonly TextBox _manualProxyBox;
    private readonly ComboBox _proxyTypeCombo;
    private readonly TextBox _labelBox;
    private readonly CheckBox _needsLoginBox;

    public InstanceConfig Result { get; private set; } = new();

    /// <summary>Edit existing instance.</summary>
    public AccountEditForm(InstanceConfig config) : this()
    {
        Result = config;
        _labelBox.Text = config.Label;
        _accountLoginBox.Text = config.ShopeeAccountLogin;
        _kiotKeyBox.Text = config.KiotProxyKey;
        _manualProxyBox.Text = config.ManualProxy;
        _proxyTypeCombo.SelectedItem = config.ProxyType is "socks5" ? "socks5" : "http";
        _needsLoginBox.Checked = config.OpenWithShopeeAccount;
    }

    /// <summary>Create new instance.</summary>
    public AccountEditForm()
    {
        Text = "Tài khoản";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(560, 340);

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 8,
            ColumnCount = 2,
            Padding = new Padding(12),
            AutoSize = false,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 7; i++)
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _labelBox = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "Tên hiển thị (tuỳ chọn)" };
        _accountLoginBox = new TextBox
        {
            Dock = DockStyle.Fill,
            PlaceholderText = "username|password|.shopee.vn=SPC_F=value",
            Font = new Font(FontFamily.GenericMonospace, 8.5f),
        };
        _kiotKeyBox = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "kiotproxy-key-abc123" };
        _manualProxyBox = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "host:port hoặc http://host:port" };
        _proxyTypeCombo = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _proxyTypeCombo.Items.AddRange(["http", "socks5"]);
        _proxyTypeCombo.SelectedIndex = 0;

        _needsLoginBox = new CheckBox
        {
            Text = "Cần đăng nhập lần tiếp",
            Checked = true,
            Dock = DockStyle.Fill,
        };

        var note = new Label
        {
            Text = "Kiot key ưu tiên hơn proxy thủ công. Bỏ trống cả 2 nếu không dùng proxy.",
            Dock = DockStyle.Fill,
            ForeColor = Color.Gray,
            AutoSize = false,
        };

        AddRow(table, 0, "Tên:", _labelBox);
        AddRow(table, 1, "Tài khoản *:", _accountLoginBox);
        AddRow(table, 2, "Kiot proxy key:", _kiotKeyBox);
        AddRow(table, 3, "Manual proxy:", _manualProxyBox);
        AddRow(table, 4, "Loại proxy:", _proxyTypeCombo);
        AddRow(table, 5, "Trạng thái:", _needsLoginBox);
        AddRow(table, 6, "", note);

        var btns = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
        };
        var ok = new Button { Text = "Lưu", DialogResult = DialogResult.OK, Width = 80, Height = 28 };
        var cancel = new Button { Text = "Hủy", DialogResult = DialogResult.Cancel, Width = 80, Height = 28 };
        btns.Controls.Add(cancel);
        btns.Controls.Add(ok);
        table.Controls.Add(btns, 1, 7);

        Controls.Add(table);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (DialogResult != DialogResult.OK) return;

        if (string.IsNullOrWhiteSpace(_accountLoginBox.Text))
        {
            MessageBox.Show("Nhập thông tin tài khoản Shopee.", "Thiếu thông tin",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            e.Cancel = true;
            return;
        }

        Result.Label = _labelBox.Text.Trim();
        Result.ShopeeAccountLogin = _accountLoginBox.Text.Trim();
        Result.KiotProxyKey = _kiotKeyBox.Text.Trim();
        Result.ManualProxy = _manualProxyBox.Text.Trim();
        Result.ProxyType = _proxyTypeCombo.SelectedItem?.ToString() ?? "http";
        Result.OpenWithShopeeAccount = _needsLoginBox.Checked;
    }

    private static void AddRow(TableLayoutPanel table, int row, string label, Control ctrl)
    {
        if (!string.IsNullOrEmpty(label))
            table.Controls.Add(new Label
            {
                Text = label,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight,
                AutoSize = false,
            }, 0, row);
        table.Controls.Add(ctrl, 1, row);
    }
}
