using ClosedXML.Excel;

namespace UpdateProduct;

internal sealed class SettingsView : UserControl, IAsyncShutdown
{
    private readonly UpdateProductSettingsFile _settings;
    private readonly Action _onChanged;
    private readonly ListBox _accountList = new() { Dock = DockStyle.Fill };
    private readonly ListBox _shopList = new() { Dock = DockStyle.Fill };
    private readonly TextBox _braveBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _sourceUserDataBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _openAiKeyFileBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _accountNameBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _workbookBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _cookieBox = new() { Dock = DockStyle.Fill };
    private readonly Label _cookieStatus = new() { AutoSize = true };
    private readonly TextBox _shopNameBox = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _sheetCombo = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly RichTextBox _logBox = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        Font = new Font("Consolas", 9f),
        BackColor = Color.FromArgb(18, 18, 18),
        ForeColor = Color.Gainsboro,
    };
    private Button? _saveCookieButton;
    private bool _suppress;
    private BigSellerLoginSession? _loginSession;

    public SettingsView(UpdateProductSettingsFile settings, Action onChanged)
    {
        _settings = settings;
        _onChanged = onChanged;
        Dock = DockStyle.Fill;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 62));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
        Controls.Add(root);

        root.Controls.Add(BuildAccountPanel(), 0, 0);
        root.Controls.Add(BuildShopPanel(), 1, 0);
        root.Controls.Add(BuildGeneralPanel(), 0, 1);
        root.Controls.Add(BuildLogPanel(), 1, 1);

        _accountList.SelectedIndexChanged += (_, _) => SelectAccountFromList();
        _shopList.SelectedIndexChanged += (_, _) => SelectShopFromList();
        RefreshLists();
    }

    public Task ShutdownAsync()
    {
        _loginSession?.Stop();
        _loginSession = null;
        return Task.CompletedTask;
    }

    private Control BuildAccountPanel()
    {
        var group = new GroupBox { Text = "Tai khoan BigSeller", Dock = DockStyle.Fill, Padding = new Padding(8) };
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var accountButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        AddSmallButton(accountButtons, "Them", AddAccount);
        AddSmallButton(accountButtons, "Xoa", DeleteAccount);
        left.Controls.Add(_accountList, 0, 0);
        left.Controls.Add(accountButtons, 0, 1);

        var detail = CreateFieldGrid(8);
        AddRow(detail, 0, "Ten/email", _accountNameBox);
        AddRow(detail, 1, "Workbook", WrapBrowse(_workbookBox, BrowseWorkbook));
        AddRow(detail, 2, "Cookie", WrapBrowse(_cookieBox, () => BrowseFile(_cookieBox, "JSON/Text|*.json;*.txt|All|*.*")));
        detail.Controls.Add(_cookieStatus, 1, 3);

        var cookieActions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        AddSmallButton(cookieActions, "Open BigSeller", async () => await OpenBigSellerAsync());
        _saveCookieButton = AddSmallButton(cookieActions, "Save && close", async () => await SaveCookieAsync());
        _saveCookieButton.Enabled = false;
        detail.Controls.Add(cookieActions, 1, 4);

        AddSmallButton(detail, "Luu account", SaveAccount, row: 5, col: 1);
        root.Controls.Add(left, 0, 0);
        root.Controls.Add(detail, 1, 0);
        group.Controls.Add(root);
        return group;
    }

    private Control BuildShopPanel()
    {
        var group = new GroupBox { Text = "Shop", Dock = DockStyle.Fill, Padding = new Padding(8) };
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var shopButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        AddSmallButton(shopButtons, "Them", AddShop);
        AddSmallButton(shopButtons, "Xoa", DeleteShop);
        left.Controls.Add(_shopList, 0, 0);
        left.Controls.Add(shopButtons, 0, 1);

        var detail = CreateFieldGrid(5);
        AddRow(detail, 0, "Ten shop", _shopNameBox);
        AddRow(detail, 1, "Sheet", _sheetCombo);
        AddSmallButton(detail, "Luu shop", SaveShop, row: 2, col: 1);

        root.Controls.Add(left, 0, 0);
        root.Controls.Add(detail, 1, 0);
        group.Controls.Add(root);
        return group;
    }

    private Control BuildGeneralPanel()
    {
        var group = new GroupBox { Text = "Cau hinh chung", Dock = DockStyle.Fill, Padding = new Padding(8) };
        var grid = CreateFieldGrid(4);
        AddRow(grid, 0, "Brave exe", WrapBrowse(_braveBox, () => BrowseFile(_braveBox, "Brave|brave.exe|Executable|*.exe|All|*.*")));
        AddRow(grid, 1, "User Data mau", WrapBrowse(_sourceUserDataBox, () => BrowseFolder(_sourceUserDataBox)));
        AddRow(grid, 2, "OpenAI key", WrapBrowse(_openAiKeyFileBox, () => BrowseFile(_openAiKeyFileBox, "Key/Text|*.key;*.txt|All|*.*")));
        AddSmallButton(grid, "Luu cau hinh", SaveGeneral, row: 3, col: 1);
        group.Controls.Add(grid);
        return group;
    }

    private Control BuildLogPanel()
    {
        var group = new GroupBox { Text = "Log", Dock = DockStyle.Fill, Padding = new Padding(8) };
        group.Controls.Add(_logBox);
        return group;
    }

    private void RefreshLists()
    {
        _suppress = true;
        try
        {
            UpdateProductSettings.Normalize(_settings);
            _braveBox.Text = _settings.BraveExe;
            _sourceUserDataBox.Text = _settings.SourceUserData;
            _openAiKeyFileBox.Text = _settings.OpenAiApiKeyFile;

            _accountList.Items.Clear();
            foreach (var account in _settings.Accounts)
                _accountList.Items.Add(new SelectorItem(account.Id, account.DisplayName));
            _accountList.SelectedIndex = Math.Max(0, _settings.Accounts.FindIndex(a => a.Id == _settings.ActiveAccountId));

            RefreshShopList();
            LoadAccountFields();
            LoadShopFields();
        }
        finally
        {
            _suppress = false;
        }
    }

    private void RefreshShopList()
    {
        var account = CurrentAccount;
        if (account is null)
            return;

        _shopList.Items.Clear();
        foreach (var shop in account.Shops)
            _shopList.Items.Add(new SelectorItem(shop.Id, shop.DisplayName));
        _shopList.SelectedIndex = Math.Max(0, account.Shops.FindIndex(s => s.Id == _settings.ActiveShopId));
    }

    private BigSellerAccountConfig? CurrentAccount =>
        _accountList.SelectedItem is SelectorItem item
            ? _settings.Accounts.FirstOrDefault(a => a.Id == item.Id)
            : null;

    private ShopConfig? CurrentShop =>
        CurrentAccount is { } account && _shopList.SelectedItem is SelectorItem item
            ? account.Shops.FirstOrDefault(s => s.Id == item.Id)
            : null;

    private void SelectAccountFromList()
    {
        if (_suppress || _accountList.SelectedItem is not SelectorItem item)
            return;

        _settings.ActiveAccountId = item.Id;
        var account = CurrentAccount;
        if (account is not null && !account.Shops.Any(s => s.Id == _settings.ActiveShopId))
            _settings.ActiveShopId = account.Shops[0].Id;

        RefreshShopList();
        LoadAccountFields();
        LoadShopFields();
        Persist();
    }

    private void SelectShopFromList()
    {
        if (_suppress || _shopList.SelectedItem is not SelectorItem item)
            return;

        _settings.ActiveShopId = item.Id;
        LoadShopFields();
        Persist();
    }

    private void LoadAccountFields()
    {
        var account = CurrentAccount;
        _accountNameBox.Text = account?.Email ?? "";
        _workbookBox.Text = account?.WorkbookPath ?? "";
        _cookieBox.Text = account?.BigSellerCookieFile ?? "";
        UpdateCookieStatus();
    }

    private void LoadShopFields()
    {
        var shop = CurrentShop;
        _shopNameBox.Text = shop?.Name ?? "";
        LoadSheetOptions(CurrentAccount?.WorkbookPath, shop?.ShopeeDataSheet);
    }

    private void AddAccount()
    {
        var account = BigSellerAccountConfig.CreateDefault();
        account.BigSellerCookieFile = UpdateProductSettings.ResolveAccountCookieFile(account);
        _settings.Accounts.Add(account);
        _settings.ActiveAccountId = account.Id;
        _settings.ActiveShopId = account.Shops[0].Id;
        RefreshLists();
        Persist();
    }

    private void DeleteAccount()
    {
        var account = CurrentAccount;
        if (account is null || _settings.Accounts.Count <= 1)
            return;

        if (MessageBox.Show(this, $"Xoa account \"{account.DisplayName}\"?", "Update Product",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        _settings.Accounts.Remove(account);
        _settings.ActiveAccountId = _settings.Accounts[0].Id;
        _settings.ActiveShopId = _settings.Accounts[0].Shops[0].Id;
        RefreshLists();
        Persist();
    }

    private void SaveAccount()
    {
        var account = CurrentAccount;
        if (account is null)
            return;

        account.Email = _accountNameBox.Text.Trim();
        account.WorkbookPath = _workbookBox.Text.Trim();
        account.BigSellerCookieFile = string.IsNullOrWhiteSpace(_cookieBox.Text)
            ? UpdateProductSettings.ResolveAccountCookieFile(account)
            : _cookieBox.Text.Trim();
        RefreshLists();
        Persist();
    }

    private void AddShop()
    {
        var account = CurrentAccount;
        if (account is null)
            return;

        var shop = new ShopConfig { Name = $"Shop {account.Shops.Count + 1}" };
        account.Shops.Add(shop);
        _settings.ActiveShopId = shop.Id;
        RefreshLists();
        Persist();
    }

    private void DeleteShop()
    {
        var account = CurrentAccount;
        var shop = CurrentShop;
        if (account is null || shop is null || account.Shops.Count <= 1)
            return;

        if (MessageBox.Show(this, $"Xoa shop \"{shop.DisplayName}\"?", "Update Product",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        account.Shops.Remove(shop);
        _settings.ActiveShopId = account.Shops[0].Id;
        RefreshLists();
        Persist();
    }

    private void SaveShop()
    {
        var shop = CurrentShop;
        if (shop is null)
            return;

        shop.Name = _shopNameBox.Text.Trim();
        shop.ShopeeDataSheet = (_sheetCombo.SelectedItem as string ?? _sheetCombo.Text).Trim();
        RefreshLists();
        Persist();
    }

    private void SaveGeneral()
    {
        _settings.BraveExe = _braveBox.Text.Trim();
        _settings.SourceUserData = _sourceUserDataBox.Text.Trim();
        _settings.OpenAiApiKeyFile = _openAiKeyFileBox.Text.Trim();
        Persist();
    }

    private async Task OpenBigSellerAsync()
    {
        try
        {
            SaveGeneral();
            SaveAccount();
            var settings = BigSellerContextFactory.Build(_settings);
            UpdateProductSettings.Save(_settings);
            _loginSession?.Stop();
            _loginSession = new BigSellerLoginSession(settings, Log);
            await _loginSession.OpenAsync().ConfigureAwait(true);
            if (_saveCookieButton is not null)
                _saveCookieButton.Enabled = true;
            UpdateCookieStatus();
        }
        catch (Exception ex)
        {
            Log($"Open BigSeller loi: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Open BigSeller", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task SaveCookieAsync()
    {
        if (_loginSession is null)
            return;

        try
        {
            await _loginSession.SaveAndCloseAsync().ConfigureAwait(true);
            _loginSession = null;
            if (_saveCookieButton is not null)
                _saveCookieButton.Enabled = false;
            UpdateCookieStatus();
            Persist();
        }
        catch (Exception ex)
        {
            Log($"Save cookie loi: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Save BigSeller cookie", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void LoadSheetOptions(string? workbook, string? selected)
    {
        _sheetCombo.Items.Clear();
        if (!string.IsNullOrWhiteSpace(workbook) && File.Exists(workbook))
        {
            try
            {
                using var wb = new XLWorkbook(workbook);
                foreach (var ws in wb.Worksheets)
                    _sheetCombo.Items.Add(ws.Name);
            }
            catch (Exception ex)
            {
                Log($"Khong doc duoc workbook: {ex.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(selected) && !_sheetCombo.Items.Contains(selected))
            _sheetCombo.Items.Add(selected);
        if (!string.IsNullOrWhiteSpace(selected))
            _sheetCombo.SelectedItem = selected;
        else if (_sheetCombo.Items.Count > 0)
            _sheetCombo.SelectedIndex = 0;
    }

    private void BrowseWorkbook()
    {
        BrowseFile(_workbookBox, "Excel workbook|*.xlsx;*.xlsm|All|*.*");
        LoadSheetOptions(_workbookBox.Text, CurrentShop?.ShopeeDataSheet);
    }

    private static TableLayoutPanel CreateFieldGrid(int rows)
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = rows, Padding = new Padding(6) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return grid;
    }

    private static void AddRow(TableLayoutPanel grid, int row, string label, Control control)
    {
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 0, row);
        grid.Controls.Add(control, 1, row);
    }

    private Button AddSmallButton(Control parent, string text, Action action, int? row = null, int col = 0)
    {
        var button = new Button { Text = text, AutoSize = true, Margin = new Padding(0, 4, 6, 4) };
        button.Click += (_, _) => action();
        AddButtonToParent(parent, button, row, col);
        return button;
    }

    private Button AddSmallButton(Control parent, string text, Func<Task> action, int? row = null, int col = 0)
    {
        var button = new Button { Text = text, AutoSize = true, Margin = new Padding(0, 4, 6, 4) };
        button.Click += async (_, _) => await action().ConfigureAwait(true);
        AddButtonToParent(parent, button, row, col);
        return button;
    }

    private static void AddButtonToParent(Control parent, Button button, int? row, int col)
    {
        if (parent is TableLayoutPanel table && row is not null)
            table.Controls.Add(button, col, row.Value);
        else
            parent.Controls.Add(button);
    }

    private static Control WrapBrowse(TextBox box, Action browse)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Height = 28 };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38));
        box.Margin = Padding.Empty;
        var button = new Button { Text = "...", Dock = DockStyle.Fill, Margin = new Padding(4, 0, 0, 0) };
        button.Click += (_, _) => browse();
        row.Controls.Add(box, 0, 0);
        row.Controls.Add(button, 1, 0);
        return row;
    }

    private void BrowseFile(TextBox target, string filter)
    {
        using var dialog = new OpenFileDialog { Filter = filter };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            target.Text = dialog.FileName;
    }

    private void BrowseFolder(TextBox target)
    {
        using var dialog = new FolderBrowserDialog();
        if (dialog.ShowDialog(this) == DialogResult.OK)
            target.Text = dialog.SelectedPath;
    }

    private void UpdateCookieStatus()
    {
        var path = _cookieBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            _cookieStatus.Text = "Chua co cookie BigSeller.";
            _cookieStatus.ForeColor = Color.Firebrick;
        }
        else if (File.Exists(path))
        {
            _cookieStatus.Text = $"Da luu cookie: {Path.GetFileName(path)}";
            _cookieStatus.ForeColor = Color.ForestGreen;
        }
        else
        {
            _cookieStatus.Text = "Duong dan cookie chua co file.";
            _cookieStatus.ForeColor = Color.DarkOrange;
        }
    }

    private void Persist()
    {
        UpdateProductSettings.Save(_settings);
        _onChanged();
    }

    private void Log(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => Log(message));
            return;
        }

        _logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.ScrollToCaret();
    }

    private sealed record SelectorItem(string Id, string Text)
    {
        public override string ToString() => Text;
    }
}
