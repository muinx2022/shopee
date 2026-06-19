using System.Windows.Forms;
using ShopeeStatApp.Models;

namespace ShopeeStatApp.Forms;

internal sealed class ExportAccountForm : Form
{
    private readonly DataGridView _grid;
    private readonly TextBox _pathBox;
    private readonly Button _browseBtn;
    private readonly Button _exportBtn;
    private readonly Button _cancelBtn;
    private readonly Label _countLabel;

    public ExportAccountForm(IReadOnlyList<InstanceConfig> normalAccounts)
    {
        Text = "Export tài khoản bình thường";
        Size = new Size(720, 540);
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(520, 400);

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 4,
            ColumnCount = 1,
            Padding = new Padding(10),
        };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // header label
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // grid
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // path row
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // buttons
        Controls.Add(panel);

        _countLabel = new Label
        {
            Text = $"Tổng cộng {normalAccounts.Count} tài khoản bình thường:",
            AutoSize = true,
            Padding = new Padding(0, 0, 0, 6),
            Font = new Font("Segoe UI", 9f),
        };
        panel.Controls.Add(_countLabel, 0, 0);

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            RowHeadersVisible = false,
        };
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Username", HeaderText = "Username", FillWeight = 25 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Login", HeaderText = "Dữ liệu tài khoản", FillWeight = 55 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Proxy", HeaderText = "Proxy", FillWeight = 20 });
        foreach (var acc in normalAccounts)
        {
            var idx = _grid.Rows.Add(acc.Username, acc.ShopeeAccountLogin, acc.ProxySummary);
            _grid.Rows[idx].Tag = acc;
        }
        panel.Controls.Add(_grid, 0, 1);

        var pathRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 8, 0, 4),
        };
        pathRow.Controls.Add(new Label
        {
            Text = "Lưu tại:",
            Width = 60,
            Height = 28,
            TextAlign = ContentAlignment.MiddleLeft,
        });
        _pathBox = new TextBox { Width = 460, Height = 28 };
        _browseBtn = new Button { Text = "Chọn...", Width = 80, Height = 28 };
        _browseBtn.Click += OnBrowseClick;
        pathRow.Controls.Add(_pathBox);
        pathRow.Controls.Add(_browseBtn);
        panel.Controls.Add(pathRow, 0, 2);

        var btnRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 4, 0, 0),
        };
        _cancelBtn = new Button { Text = "Hủy", Width = 90, Height = 28, DialogResult = DialogResult.Cancel };
        _exportBtn = new Button { Text = "Export", Width = 100, Height = 28 };
        _exportBtn.Click += OnExportClick;
        btnRow.Controls.Add(_cancelBtn);
        btnRow.Controls.Add(_exportBtn);
        panel.Controls.Add(btnRow, 0, 3);

        AcceptButton = _exportBtn;
        CancelButton = _cancelBtn;
    }

    private void OnBrowseClick(object? sender, EventArgs e)
    {
        using var dlg = new SaveFileDialog
        {
            Title = "Lưu danh sách tài khoản",
            Filter = "Text files (*.txt)|*.txt|CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = "txt",
            FileName = "accounts_export.txt",
        };
        var current = _pathBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(current))
            dlg.InitialDirectory = Path.GetDirectoryName(current) ?? "";
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _pathBox.Text = dlg.FileName;
    }

    private void OnExportClick(object? sender, EventArgs e)
    {
        var path = _pathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            MessageBox.Show("Vui lòng chọn đường dẫn lưu file.", "Export tài khoản",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            var lines = new List<string>(_grid.Rows.Count);
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.Tag is InstanceConfig cfg)
                    lines.Add(cfg.ShopeeAccountLogin);
            }
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllLines(path, lines, Encoding.UTF8);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Lỗi khi lưu file:\n{ex.Message}", "Export tài khoản",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
