using ClosedXML.Excel;

namespace OpenMultiBraveLauncherV3;

/// <summary>
/// Panel cài đặt cho một account cụ thể — nhúng vào tab "Cài đặt" trong workspace.
/// </summary>
internal sealed class AccountSettingsPanel : UserControl
{
    private readonly LauncherSettingsFile _settings;
    private readonly string _accountId;
    private readonly ListBox _shopList = new();
    private readonly TextBox _emailTextBox = new();
    private readonly TextBox _workbookTextBox = new();
    private readonly TextBox _bigSellerCookieFileTextBox = new();
    private readonly TextBox _shopNameTextBox = new();
    private readonly ComboBox _sheetComboBox = new() { DropDownStyle = ComboBoxStyle.DropDown };
    private readonly Label _bigSellerStatusLabel = new();
    private RichTextBox _loginLogBox = null!;
    private Panel _loginLogPanel = null!;
    private Button _stopLoginBtn = null!;
    private TableLayoutPanel? _rootTable;
    private BigSellerLoginRunner? _loginRunner;
    private CancellationTokenSource? _loginCts;
    private bool _suppressChanges;

    public AccountSettingsPanel(LauncherSettingsFile settings, string accountId)
    {
        _settings = settings;
        _accountId = accountId;
        Dock = DockStyle.Fill;
        Controls.Add(CreateLayout());
        Load += (_, _) => RefreshShopList();
        HandleDestroyed += (_, _) => CleanupLogin();
    }

    private AccountConfig? ActiveAccount =>
        _settings.Accounts.FirstOrDefault(a => a.Id == _accountId);

    private ShopConfig? CurrentShop =>
        ActiveAccount is { } account && _shopList.SelectedItem is SelectorItem item
            ? account.Shops.FirstOrDefault(s => s.Id == item.Id)
            : null;

    private Control CreateLayout()
    {
        _rootTable = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
            Padding = new Padding(10),
        };
        _rootTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
        _rootTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _rootTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _rootTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));  // log panel, ẩn ban đầu
        _rootTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _rootTable.Controls.Add(CreateShopPanel(), 0, 0);
        _rootTable.Controls.Add(CreateDetailPanel(), 1, 0);

        _loginLogPanel = CreateLoginLogPanel();
        _rootTable.Controls.Add(_loginLogPanel, 0, 1);
        _rootTable.SetColumnSpan(_loginLogPanel, 2);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(0, 4, 0, 0),
        };
        var saveBtn = new Button { Text = "Lưu cài đặt", AutoSize = true, Padding = new Padding(16, 0, 16, 0) };
        saveBtn.Click += (_, _) => SaveAndPersist();
        actions.Controls.Add(saveBtn);
        _rootTable.SetColumnSpan(actions, 2);
        _rootTable.Controls.Add(actions, 0, 2);

        return _rootTable;
    }

    private Control CreateShopPanel()
    {
        var panel = new GroupBox { Text = "Shop", Dock = DockStyle.Fill, Padding = new Padding(8) };
        _shopList.Dock = DockStyle.Fill;
        _shopList.SelectedIndexChanged += (_, _) => SelectShop();

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Bottom };
        var add = new Button { Text = "Thêm", AutoSize = true, Padding = new Padding(10, 0, 10, 0) };
        var remove = new Button { Text = "Xóa", AutoSize = true, Padding = new Padding(10, 0, 10, 0) };
        add.Click += (_, _) => AddShop();
        remove.Click += (_, _) => RemoveShop();
        buttons.Controls.Add(add);
        buttons.Controls.Add(remove);

        panel.Controls.Add(_shopList);
        panel.Controls.Add(buttons);
        return panel;
    }

    private Control CreateDetailPanel()
    {
        var group = new GroupBox { Text = "Chi tiết", Dock = DockStyle.Fill, Padding = new Padding(8) };
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 3,
            Padding = new Padding(8),
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        AddTextRow(table, 0, "Email", _emailTextBox, null);
        var browse = new Button { Text = "...", Width = 34 };
        browse.Click += (_, _) => BrowseWorkbook();
        AddTextRow(table, 1, "Workbook", _workbookTextBox, browse);

        var bigSellerBrowse = new Button { Text = "...", Width = 34 };
        bigSellerBrowse.Click += (_, _) => BrowseBigSellerCookieFile();
        AddTextRow(table, 2, "BS Cookie", _bigSellerCookieFileTextBox, bigSellerBrowse);

        var loginBtn = new Button
        {
            Text = "Đăng nhập BigSeller",
            AutoSize = true,
            Margin = new Padding(0, 2, 0, 6),
        };
        loginBtn.Click += (_, _) => OpenBigSellerLogin();
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(loginBtn, 1, 3);
        table.SetColumnSpan(loginBtn, 2);

        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _bigSellerStatusLabel.Dock = DockStyle.Fill;
        _bigSellerStatusLabel.AutoSize = false;
        _bigSellerStatusLabel.Height = 22;
        _bigSellerStatusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _bigSellerStatusLabel.Padding = new Padding(2, 0, 0, 4);
        table.Controls.Add(_bigSellerStatusLabel, 1, 4);
        table.SetColumnSpan(_bigSellerStatusLabel, 2);

        AddTextRow(table, 5, "Tên shop", _shopNameTextBox, null);
        AddTextRow(table, 6, "Sheet Shopee", _sheetComboBox, null);

        var saveShopBtn = new Button
        {
            Text = "Lưu shop",
            AutoSize = true,
            Margin = new Padding(0, 6, 0, 2),
        };
        saveShopBtn.Click += (_, _) => SaveAndPersist();
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(saveShopBtn, 1, 7);
        table.SetColumnSpan(saveShopBtn, 2);

        _bigSellerCookieFileTextBox.TextChanged += (_, _) => UpdateBigSellerStatus();
        _workbookTextBox.Leave += (_, _) => RefreshSheetDropdown(_workbookTextBox.Text);
        _sheetComboBox.DropDown += (_, _) => RefreshSheetDropdown(_workbookTextBox.Text);

        group.Controls.Add(table);
        return group;
    }

    private static void AddTextRow(TableLayoutPanel table, int row, string label, Control box, Control? button)
    {
        box.Dock = DockStyle.Fill;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 0, row);
        table.Controls.Add(box, 1, row);
        if (button is null)
            table.SetColumnSpan(box, 2);
        else
            table.Controls.Add(button, 2, row);
    }

    private void RefreshShopList()
    {
        _suppressChanges = true;
        try
        {
            var account = ActiveAccount;
            _shopList.Items.Clear();
            if (account is null) return;

            foreach (var shop in account.Shops)
                _shopList.Items.Add(new SelectorItem(shop.Id, shop.DisplayName));

            var shopIndex = Math.Max(0, account.Shops.FindIndex(s => s.Id == _settings.ActiveShopId));
            if (_shopList.Items.Count > 0)
                _shopList.SelectedIndex = shopIndex;

            LoadCurrent();
        }
        finally
        {
            _suppressChanges = false;
        }
    }

    private void SelectShop()
    {
        if (_suppressChanges) return;
        if (CurrentShop is { } shop)
            _settings.ActiveShopId = shop.Id;
        LoadCurrent();
    }

    private void LoadCurrent()
    {
        _suppressChanges = true;
        try
        {
            var account = ActiveAccount;
            var shop = CurrentShop;
            _emailTextBox.Text = account?.Email ?? "";
            _workbookTextBox.Text = account?.WorkbookPath ?? "";
            _bigSellerCookieFileTextBox.Text = account?.BigSellerCookieFile ?? "";
            _shopNameTextBox.Text = shop?.Name ?? "";
            _sheetComboBox.Text = shop?.ShopeeDataSheet ?? "";
            UpdateBigSellerStatus();
        }
        finally
        {
            _suppressChanges = false;
        }
    }

    private void RefreshSheetDropdown(string? workbookPath, string? currentSheet = null)
    {
        var previousText = currentSheet ?? _sheetComboBox.Text;
        _sheetComboBox.Items.Clear();

        if (!string.IsNullOrWhiteSpace(workbookPath) && File.Exists(workbookPath))
        {
            var prev = Cursor.Current;
            Cursor.Current = Cursors.WaitCursor;
            try
            {
                using var wb = new XLWorkbook(workbookPath);
                foreach (var ws in wb.Worksheets)
                    _sheetComboBox.Items.Add(ws.Name);
            }
            catch { }
            finally { Cursor.Current = prev; }
        }

        if (!string.IsNullOrWhiteSpace(previousText))
            _sheetComboBox.Text = previousText;
        else if (_sheetComboBox.Items.Count > 0)
            _sheetComboBox.SelectedIndex = 0;
    }

    private void SaveCurrent()
    {
        if (_suppressChanges) return;
        var account = ActiveAccount;
        if (account is not null)
        {
            account.Email = _emailTextBox.Text.Trim();
            account.WorkbookPath = _workbookTextBox.Text.Trim();
            account.BigSellerCookieFile = _bigSellerCookieFileTextBox.Text.Trim();
        }
        if (CurrentShop is { } shop)
        {
            shop.Name = _shopNameTextBox.Text.Trim();
            shop.ShopeeDataSheet = _sheetComboBox.Text.Trim();
            _settings.ActiveShopId = shop.Id;

            if (_shopList.SelectedIndex >= 0)
                _shopList.Items[_shopList.SelectedIndex] = new SelectorItem(shop.Id, shop.DisplayName);
        }
    }

    private void SaveAndPersist()
    {
        SaveCurrent();
        LauncherSettings.Save(_settings);
    }

    private void AddShop()
    {
        SaveCurrent();
        var account = ActiveAccount;
        if (account is null) return;
        var shop = ShopConfig.CreateDefault();
        shop.Name = $"Shop {account.Shops.Count + 1}";
        account.Shops.Add(shop);
        _settings.ActiveShopId = shop.Id;
        LauncherSettings.Save(_settings);
        RefreshShopList();
    }

    private void RemoveShop()
    {
        var account = ActiveAccount;
        if (account is null || CurrentShop is not { } shop || account.Shops.Count <= 1) return;
        account.Shops.Remove(shop);
        _settings.ActiveShopId = account.Shops[0].Id;
        LauncherSettings.Save(_settings);
        RefreshShopList();
    }

    private void BrowseWorkbook()
    {
        using var ofd = new OpenFileDialog
        {
            Filter = "Excel files (*.xlsx;*.xlsm)|*.xlsx;*.xlsm|All files (*.*)|*.*",
            Title = "Chọn workbook",
        };
        if (ofd.ShowDialog(FindForm()) != DialogResult.OK) return;
        _workbookTextBox.Text = ofd.FileName;
        RefreshSheetDropdown(ofd.FileName);
    }

    private void BrowseBigSellerCookieFile()
    {
        using var sfd = new SaveFileDialog
        {
            Filter = "JSON (*.json)|*.json|All files (*.*)|*.*",
            Title = "Chọn nơi lưu cookie BigSeller",
            FileName = "bigseller-cookies.json",
            InitialDirectory = AppSession.ProjectSourceDirectory,
        };
        if (sfd.ShowDialog(FindForm()) == DialogResult.OK)
            _bigSellerCookieFileTextBox.Text = sfd.FileName;
    }

    private async void OpenBigSellerLogin()
    {
        SaveCurrent();
        var account = ActiveAccount;
        if (account is null) return;

        var braveExe = _settings.BraveExe;
        if (string.IsNullOrWhiteSpace(braveExe) || !File.Exists(braveExe))
        {
            MessageBox.Show("Chưa cấu hình đường dẫn Brave.exe trong Cài đặt chung.", "BigSeller",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var cookieFile = account.BigSellerCookieFile.Trim();
        if (string.IsNullOrWhiteSpace(cookieFile))
        {
            cookieFile = AppSession.ResolvePersistentDataPath("bigseller-cookies", $"{account.Id}.json");
            account.BigSellerCookieFile = cookieFile;
            _bigSellerCookieFileTextBox.Text = cookieFile;
        }

        CleanupLogin();
        ShowLoginLogPanel();
        _loginLogBox.Clear();
        _stopLoginBtn.Text = "Dừng";
        _stopLoginBtn.Enabled = true;

        _loginCts = new CancellationTokenSource();
        bool loginSucceeded = false;
        try
        {
            AppendLoginLog($"File cookie sẽ lưu tại: {cookieFile}");
            _loginRunner = await BigSellerLoginRunner.LaunchAsync(
                braveExe, _settings.SourceUserData, AppendLoginLog, _loginCts.Token).ConfigureAwait(true);
            AppendLoginLog("Đăng nhập trong cửa sổ Brave. Cookie tự lưu khi phát hiện đăng nhập thành công.");

            await _loginRunner.PollForLoginAndSaveCookiesAsync(
                cookieFile, AppendLoginLog,
                onLoginDetected: _ =>
                {
                    loginSucceeded = true;
                    BeginInvoke(() =>
                    {
                        UpdateBigSellerStatus();
                        _stopLoginBtn.Text = "Đóng log";
                    });
                },
                _loginCts.Token).ConfigureAwait(true);

            if (!loginSucceeded)
                AppendLoginLog("Không phát hiện đăng nhập (hết thời gian hoặc bị hủy).");
        }
        catch (OperationCanceledException)
        {
            AppendLoginLog("Đã hủy phiên đăng nhập.");
        }
        catch (Exception ex)
        {
            AppendLoginLog($"Lỗi: {ex.Message}");
        }
        finally
        {
            if (!loginSucceeded)
                _stopLoginBtn.Text = "Đóng log";
        }
    }

    private Panel CreateLoginLogPanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 4, 0, 0), Visible = false };
        var header = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 0, 0, 2),
        };
        var headerLabel = new Label
        {
            Text = "Log đăng nhập BigSeller",
            AutoSize = true,
            Font = new Font(Font.FontFamily, Font.Size, FontStyle.Bold),
            Padding = new Padding(0, 4, 8, 0),
        };
        _stopLoginBtn = new Button { Text = "Dừng", AutoSize = true, Padding = new Padding(8, 0, 8, 0) };
        _stopLoginBtn.Click += (_, _) =>
        {
            if (_loginRunner is not null)
            {
                CleanupLogin();
                AppendLoginLog("Đã dừng.");
                _stopLoginBtn.Text = "Đóng log";
            }
            else
            {
                _loginLogPanel.Visible = false;
                _rootTable!.RowStyles[1] = new RowStyle(SizeType.Absolute, 0);
                _rootTable.PerformLayout();
            }
        };
        header.Controls.Add(headerLabel);
        header.Controls.Add(_stopLoginBtn);

        _loginLogBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = Color.FromArgb(28, 28, 28),
            ForeColor = Color.FromArgb(210, 210, 210),
            Font = new Font("Consolas", 8.5f),
            ScrollBars = RichTextBoxScrollBars.Vertical,
            BorderStyle = BorderStyle.None,
        };

        panel.Controls.Add(_loginLogBox);
        panel.Controls.Add(header);
        return panel;
    }

    private void ShowLoginLogPanel()
    {
        if (_rootTable is null) return;
        _loginLogPanel.Visible = true;
        _rootTable.RowStyles[1] = new RowStyle(SizeType.Absolute, 150);
        _rootTable.PerformLayout();
    }

    private void AppendLoginLog(string msg)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => AppendLoginLog(msg)); return; }
        _loginLogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\n");
        _loginLogBox.ScrollToCaret();
    }

    private void UpdateBigSellerStatus()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(UpdateBigSellerStatus); return; }
        var cookieFile = _bigSellerCookieFileTextBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(cookieFile) && File.Exists(cookieFile))
        {
            _bigSellerStatusLabel.Text = "✓ Đã có cookie BigSeller";
            _bigSellerStatusLabel.ForeColor = Color.FromArgb(30, 160, 60);
        }
        else
        {
            _bigSellerStatusLabel.Text = "⚠ Chưa đăng nhập BigSeller";
            _bigSellerStatusLabel.ForeColor = Color.FromArgb(200, 130, 0);
        }
    }

    private void CleanupLogin()
    {
        try { _loginCts?.Cancel(); } catch { }
        _loginRunner?.Dispose();
        _loginRunner = null;
        _loginCts?.Dispose();
        _loginCts = null;
    }

    private sealed record SelectorItem(string Id, string Text)
    {
        public override string ToString() => Text;
    }
}
