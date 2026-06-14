using System.IO.Compression;
using System.Xml.Linq;

namespace OpenMultiBraveLauncherV3;

internal sealed class AccountShopManagerForm : Form
{
    private readonly List<BigSellerAccountConfig> _accounts;
    private readonly string _braveExe;
    private readonly string _sourceUserData;
    private bool _suppress;
    private BraveInstanceSession? _accountCookieSession;

    private readonly ListBox _accountList;
    private readonly TextBox _accountTextBox;
    private readonly TextBox _cookieTextBox;
    private readonly Label _cookieStatusLabel;
    private readonly Button _openBigSellerButton;
    private readonly Button _saveCookieButton;
    private readonly ListBox _shopList;
    private readonly TextBox _shopNameTextBox;
    private readonly TextBox _workbookTextBox;
    private readonly ComboBox _shopeeSheetCombo;
    private readonly CheckBox _sharedProfilesCheckBox;

    public IReadOnlyList<BigSellerAccountConfig> Accounts => _accounts;
    public string ActiveAccountId { get; private set; }
    public string ActiveShopId { get; private set; }

    public AccountShopManagerForm(
        IReadOnlyList<BigSellerAccountConfig> accounts,
        string activeAccountId,
        string activeShopId,
        string braveExe,
        string sourceUserData)
    {
        _accounts = CloneAccounts(accounts);
        _braveExe = braveExe;
        _sourceUserData = sourceUserData;
        if (_accounts.Count == 0)
            _accounts.Add(BigSellerAccountConfig.CreateDefault());

        ActiveAccountId = _accounts.Any(a => a.Id == activeAccountId) ? activeAccountId : _accounts[0].Id;
        var activeAccount = _accounts.First(a => a.Id == ActiveAccountId);
        ActiveShopId = activeAccount.Shops.Any(s => s.Id == activeShopId) ? activeShopId : activeAccount.Shops[0].Id;

        Text = "Quản lý tài khoản / shop";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(1180, 640);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(10),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 460));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var accountGroup = new GroupBox { Text = "Tài khoản BigSeller", Dock = DockStyle.Fill };
        var accountLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 10,
            Padding = new Padding(8),
        };
        accountLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        accountLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        accountLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        accountLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        accountLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        accountLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        accountLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        accountLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        accountLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        accountLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _accountList = new ListBox { Dock = DockStyle.Fill };
        _accountList.SelectedIndexChanged += (_, _) => OnAccountSelected();
        _accountTextBox = new TextBox { Dock = DockStyle.Top, PlaceholderText = "abc@test.com" };
        _workbookTextBox = new TextBox { Dock = DockStyle.Fill };
        _cookieTextBox = new TextBox { Dock = DockStyle.Fill };

        var accountWorkbookRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            Height = 28,
        };
        accountWorkbookRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        accountWorkbookRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        var browseAccountWorkbook = new Button { Text = "Chọn…", Dock = DockStyle.Fill };
        browseAccountWorkbook.Click += (_, _) => BrowseWorkbook();
        accountWorkbookRow.Controls.Add(_workbookTextBox, 0, 0);
        accountWorkbookRow.Controls.Add(browseAccountWorkbook, 1, 0);

        var accountCookieRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            Height = 28,
        };
        accountCookieRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        accountCookieRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        var browseAccountCookie = new Button { Text = "Chọn…", Dock = DockStyle.Fill };
        browseAccountCookie.Click += (_, _) => BrowseFile(_cookieTextBox, "JSON/Text files (*.json;*.txt)|*.json;*.txt|All files (*.*)|*.*", "Chọn cookie BigSeller của account");
        accountCookieRow.Controls.Add(_cookieTextBox, 0, 0);
        accountCookieRow.Controls.Add(browseAccountCookie, 1, 0);
        _cookieStatusLabel = new Label
        {
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(0, 4, 0, 0),
        };

        var accountCookieActions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
        _openBigSellerButton = new Button { Text = "Open BigSeller", AutoSize = true };
        _openBigSellerButton.Click += async (_, _) => await OpenBigSellerForAccountAsync();
        _saveCookieButton = new Button { Text = "Save && close", AutoSize = true, Enabled = false };
        _saveCookieButton.Click += async (_, _) => await SaveAndCloseBigSellerCookieAsync();
        accountCookieActions.Controls.AddRange([_openBigSellerButton, _saveCookieButton]);

        var accountButtons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
        var addAccount = new Button { Text = "Thêm", Width = 78 };
        addAccount.Click += (_, _) => AddAccount();
        var saveAccount = new Button { Text = "Lưu", Width = 78 };
        saveAccount.Click += (_, _) => SaveAccount();
        var deleteAccount = new Button { Text = "Xóa", Width = 78 };
        deleteAccount.Click += (_, _) => DeleteAccount();
        accountButtons.Controls.AddRange([addAccount, saveAccount, deleteAccount]);

        accountLayout.Controls.Add(_accountList, 0, 0);
        accountLayout.Controls.Add(new Label { Text = "Email / tên tài khoản", AutoSize = true, Margin = new Padding(0, 8, 0, 3) }, 0, 1);
        accountLayout.Controls.Add(_accountTextBox, 0, 2);
        accountLayout.Controls.Add(new Label { Text = "File xlsx", AutoSize = true, Margin = new Padding(0, 8, 0, 3) }, 0, 3);
        accountLayout.Controls.Add(accountWorkbookRow, 0, 4);
        accountLayout.Controls.Add(new Label { Text = "Cookie BigSeller", AutoSize = true, Margin = new Padding(0, 8, 0, 3) }, 0, 5);
        accountLayout.Controls.Add(accountCookieRow, 0, 6);
        accountLayout.Controls.Add(_cookieStatusLabel, 0, 7);
        accountLayout.Controls.Add(accountCookieActions, 0, 8);
        accountLayout.Controls.Add(accountButtons, 0, 9);
        accountGroup.Controls.Add(accountLayout);

        var shopGroup = new GroupBox { Text = "Shop", Dock = DockStyle.Fill };
        var shopLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(8),
        };
        shopLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
        shopLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shopLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shopLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var shopListPanel = new Panel { Dock = DockStyle.Fill };
        _shopList = new ListBox { Dock = DockStyle.Fill };
        _shopList.SelectedIndexChanged += (_, _) => OnShopSelected();
        var shopButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 40,
            WrapContents = false,
        };
        var addShop = new Button { Text = "Thêm", Width = 78 };
        addShop.Click += (_, _) => AddShop();
        var deleteShop = new Button { Text = "Xóa", Width = 78 };
        deleteShop.Click += (_, _) => DeleteShop();
        shopButtons.Controls.AddRange([addShop, deleteShop]);
        shopListPanel.Controls.Add(_shopList);
        shopListPanel.Controls.Add(shopButtons);

        var detail = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 3,
        };
        detail.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        detail.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        detail.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));

        _shopNameTextBox = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "Minoa Store" };
        _shopeeSheetCombo = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _sharedProfilesCheckBox = new CheckBox
        {
            Text = "Dùng chung profile trong tài khoản này",
            AutoSize = true,
        };

        AddDetailRow(detail, 0, "Tên shop", _shopNameTextBox, null);
        AddDetailRow(detail, 1, "Sheet Shopee", _shopeeSheetCombo, null);
        detail.Controls.Add(_sharedProfilesCheckBox, 1, 2);
        detail.SetColumnSpan(_sharedProfilesCheckBox, 2);

        var saveShop = new Button
        {
            Text = "Lưu shop",
            Width = 96,
            Height = 32,
            Margin = new Padding(110, 12, 0, 0),
        };
        saveShop.Click += (_, _) => SaveShop();
        detail.Controls.Add(saveShop, 1, 3);

        shopLayout.Controls.Add(shopListPanel, 0, 0);
        shopLayout.Controls.Add(detail, 1, 0);
        shopGroup.Controls.Add(shopLayout);

        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
        };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 88, Height = 32 };
        var cancel = new Button { Text = "Hủy", DialogResult = DialogResult.Cancel, Width = 88, Height = 32 };
        bottom.Controls.Add(cancel);
        bottom.Controls.Add(ok);

        root.Controls.Add(accountGroup, 0, 0);
        root.Controls.Add(shopGroup, 1, 0);
        root.Controls.Add(bottom, 0, 1);
        root.SetColumnSpan(bottom, 2);
        Controls.Add(root);

        AcceptButton = ok;
        CancelButton = cancel;
        FormClosing += (_, _) => StopAccountCookieSession();
        RefreshAccountList();
    }

    private static List<BigSellerAccountConfig> CloneAccounts(IReadOnlyList<BigSellerAccountConfig> accounts) =>
        accounts.Select(a => new BigSellerAccountConfig
        {
            Id = string.IsNullOrWhiteSpace(a.Id) ? Guid.NewGuid().ToString("N") : a.Id,
            Email = a.Email,
            WorkbookPath = a.WorkbookPath,
            BigSellerCookieFile = a.BigSellerCookieFile,
            Shops = a.Shops.Select(s => new ShopConfig
            {
                Id = string.IsNullOrWhiteSpace(s.Id) ? Guid.NewGuid().ToString("N") : s.Id,
                Name = s.Name,
                ShopeeDataSheet = s.ShopeeDataSheet,
                UseSharedProfiles = s.UseSharedProfiles,
                BigSellerImagePath = s.BigSellerImagePath,
                BigSellerVideoFolder = s.BigSellerVideoFolder,
                BigSellerCrawlUrl = s.BigSellerCrawlUrl,
                BigSellerImportFromClaimedTab = s.BigSellerImportFromClaimedTab,
                BigSellerListingReloadSeconds = s.BigSellerListingReloadSeconds,
                BigSellerProfileRelativePath = s.BigSellerProfileRelativePath,
                BigSellerDebugPort = s.BigSellerDebugPort,
                BigSellerImportProfileRelativePath = s.BigSellerImportProfileRelativePath,
                BigSellerImportDebugPort = s.BigSellerImportDebugPort,
                OpenAiModel = s.OpenAiModel,
                OpenAiApiKeyFile = s.OpenAiApiKeyFile,
            }).ToList(),
        }).ToList();

    private static void AddDetailRow(TableLayoutPanel table, int row, string label, Control input, Control? button)
    {
        table.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 0, row);
        table.Controls.Add(input, 1, row);
        if (button is not null)
            table.Controls.Add(button, 2, row);
        else
            table.SetColumnSpan(input, 2);
    }

    private void RefreshAccountList()
    {
        _suppress = true;
        try
        {
            _accountList.Items.Clear();
            foreach (var account in _accounts)
                _accountList.Items.Add(new SelectorItem(account.Id, account.DisplayName));

            var index = _accounts.FindIndex(a => a.Id == ActiveAccountId);
            _accountList.SelectedIndex = Math.Max(0, index);
            LoadAccountFields();
            RefreshShopList();
        }
        finally
        {
            _suppress = false;
        }
    }

    private void RefreshShopList()
    {
        var account = CurrentAccount;
        if (account is null) return;

        _shopList.Items.Clear();
        foreach (var shop in account.Shops)
            _shopList.Items.Add(new SelectorItem(shop.Id, shop.DisplayName));

        var index = account.Shops.FindIndex(s => s.Id == ActiveShopId);
        _shopList.SelectedIndex = Math.Max(0, index);
        LoadShopFields();
    }

    private BigSellerAccountConfig? CurrentAccount =>
        _accountList.SelectedItem is SelectorItem item
            ? _accounts.FirstOrDefault(a => a.Id == item.Id)
            : null;

    private ShopConfig? CurrentShop =>
        CurrentAccount is { } account && _shopList.SelectedItem is SelectorItem item
            ? account.Shops.FirstOrDefault(s => s.Id == item.Id)
            : null;

    private void OnAccountSelected()
    {
        if (_suppress || _accountList.SelectedItem is not SelectorItem item)
            return;

        ActiveAccountId = item.Id;
        var account = CurrentAccount;
        if (account is not null && !account.Shops.Any(s => s.Id == ActiveShopId))
            ActiveShopId = account.Shops[0].Id;

        LoadAccountFields();
        RefreshShopList();
    }

    private void OnShopSelected()
    {
        if (_suppress || _shopList.SelectedItem is not SelectorItem item)
            return;

        ActiveShopId = item.Id;
        LoadShopFields();
    }

    private void LoadAccountFields()
    {
        _accountTextBox.Text = CurrentAccount?.Email ?? "";
        _workbookTextBox.Text = CurrentAccount?.WorkbookPath ?? "";
        _cookieTextBox.Text = CurrentAccount?.BigSellerCookieFile ?? "";
        UpdateCookieStatus();
    }

    private void LoadShopFields()
    {
        var shop = CurrentShop;
        _shopNameTextBox.Text = shop?.Name ?? "";
        LoadSheetOptions(CurrentAccount?.WorkbookPath, shop?.ShopeeDataSheet);
        _sharedProfilesCheckBox.Checked = shop?.UseSharedProfiles ?? true;
    }

    private void AddAccount()
    {
        var account = new BigSellerAccountConfig { Email = "abc@test.com" };
        account.Shops.Add(ShopConfig.CreateDefault());
        _accounts.Add(account);
        ActiveAccountId = account.Id;
        ActiveShopId = account.Shops[0].Id;
        RefreshAccountList();
    }

    private void SaveAccount()
    {
        var account = CurrentAccount;
        if (account is null) return;

        if (string.IsNullOrWhiteSpace(_accountTextBox.Text))
        {
            MessageBox.Show(this, "Tài khoản không được để trống.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        account.Email = _accountTextBox.Text.Trim();
        account.WorkbookPath = _workbookTextBox.Text.Trim();
        account.BigSellerCookieFile = _cookieTextBox.Text.Trim();
        UpdateCookieStatus();
        RefreshAccountList();
    }

    private async Task OpenBigSellerForAccountAsync()
    {
        var account = CurrentAccount;
        if (account is null) return;

        if (string.IsNullOrWhiteSpace(_braveExe) || !File.Exists(_braveExe))
        {
            MessageBox.Show(this, "Chưa cấu hình brave.exe trong Cài đặt chung.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(_sourceUserData) || !Directory.Exists(_sourceUserData))
        {
            MessageBox.Show(this, "Chưa cấu hình User Data mẫu trong Cài đặt chung.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        StopAccountCookieSession();

        var config = new InstanceConfig
        {
            Id = $"bigseller-{account.Id}",
            Label = $"BigSeller {account.DisplayName}",
            ProfileRelativePath = Path.Combine("bigseller-account-profiles", account.Id),
            ExportBigSeller = false,
            ExportShopee = false,
        };

        _accountCookieSession = new BraveInstanceSession(AllocateAccountCookiePort(account), _ => { });
        _accountCookieSession.ApplyConfig(config);

        try
        {
            _openBigSellerButton.Enabled = false;
            _saveCookieButton.Enabled = false;
            await _accountCookieSession.StartAsync(_braveExe, _sourceUserData).ConfigureAwait(true);

            var cookieFile = GetAccountCookieFile(account);
            if (File.Exists(cookieFile))
            {
                await _accountCookieSession.ImportCookiesFromFileAsync(
                    cookieFile, includeShopee: false, includeBigSeller: true,
                    prepareBigSellerPage: true).ConfigureAwait(true);
                _cookieStatusLabel.Text = "Da nap cookie. Mo BigSeller...";
            }

            await _accountCookieSession.OpenBigSellerPageAsync().ConfigureAwait(true);
            _saveCookieButton.Enabled = true;
            _cookieStatusLabel.Text = File.Exists(cookieFile)
                ? "Da nap cookie tu file. Kiem tra dang nhap, neu can thi dang nhap lai roi bam Save & close."
                : "Da mo BigSeller. Dang nhap xong bam Save & close.";
        }
        catch (Exception ex)
        {
            StopAccountCookieSession();
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _openBigSellerButton.Enabled = true;
        }
    }

    private async Task SaveAndCloseBigSellerCookieAsync()
    {
        var account = CurrentAccount;
        if (account is null || _accountCookieSession is null)
            return;

        try
        {
            _saveCookieButton.Enabled = false;
            var fileName = GetAccountCookieFile(account);
            await _accountCookieSession.ExportCookiesToFileAsync(fileName).ConfigureAwait(true);
            account.BigSellerCookieFile = fileName;
            _cookieTextBox.Text = fileName;
            await _accountCookieSession.StopAsync(CancellationToken.None).ConfigureAwait(true);
            _accountCookieSession.Dispose();
            _accountCookieSession = null;
            UpdateCookieStatus();
        }
        catch (Exception ex)
        {
            _saveCookieButton.Enabled = _accountCookieSession is not null;
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void StopAccountCookieSession()
    {
        if (_accountCookieSession is null)
            return;

        try { _accountCookieSession.Stop(); } catch { }
        _accountCookieSession.Dispose();
        _accountCookieSession = null;
        if (_saveCookieButton is not null)
            _saveCookieButton.Enabled = false;
    }

    private void UpdateCookieStatus()
    {
        if (_cookieStatusLabel is null)
            return;

        var path = _cookieTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            _cookieStatusLabel.Text = "Chua co cookie BigSeller.";
            _cookieStatusLabel.ForeColor = Color.Firebrick;
            return;
        }

        if (File.Exists(path))
        {
            _cookieStatusLabel.Text = $"Da luu cookie: {Path.GetFileName(path)}";
            _cookieStatusLabel.ForeColor = Color.ForestGreen;
            return;
        }

        _cookieStatusLabel.Text = "Da cau hinh duong dan cookie nhung file khong ton tai.";
        _cookieStatusLabel.ForeColor = Color.DarkOrange;
    }

    private static string GetAccountCookieFile(BigSellerAccountConfig account) =>
        AppSession.ResolvePersistentDataPath("account-cookies", $"{account.Id}-bigseller.json");

    private int AllocateAccountCookiePort(BigSellerAccountConfig account)
    {
        var index = Math.Max(0, _accounts.FindIndex(a => a.Id == account.Id));
        return 9700 + AppSession.PortOffset + index;
    }

    private void DeleteAccount()
    {
        var account = CurrentAccount;
        if (account is null) return;
        if (_accounts.Count <= 1)
        {
            MessageBox.Show(this, "Cần giữ lại ít nhất một tài khoản.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (MessageBox.Show(this, $"Xóa tài khoản \"{account.DisplayName}\"?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        _accounts.Remove(account);
        ActiveAccountId = _accounts[0].Id;
        ActiveShopId = _accounts[0].Shops[0].Id;
        RefreshAccountList();
    }

    private void AddShop()
    {
        var account = CurrentAccount;
        if (account is null) return;

        var shop = new ShopConfig
        {
            Name = account.Shops.Count == 0 ? "Minoa Store" : $"Shop {account.Shops.Count + 1}",
            UseSharedProfiles = true,
        };
        account.Shops.Add(shop);
        ActiveShopId = shop.Id;
        RefreshShopList();
    }

    private void SaveShop()
    {
        var shop = CurrentShop;
        if (shop is null) return;

        if (string.IsNullOrWhiteSpace(_shopNameTextBox.Text))
        {
            MessageBox.Show(this, "Tên shop không được để trống.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        shop.Name = _shopNameTextBox.Text.Trim();
        shop.ShopeeDataSheet = (_shopeeSheetCombo.SelectedItem as string ?? "").Trim();
        shop.UseSharedProfiles = _sharedProfilesCheckBox.Checked;
        RefreshShopList();
    }

    private void DeleteShop()
    {
        var account = CurrentAccount;
        var shop = CurrentShop;
        if (account is null || shop is null) return;
        if (account.Shops.Count <= 1)
        {
            MessageBox.Show(this, "Cần giữ lại ít nhất một shop trong tài khoản.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (MessageBox.Show(this, $"Xóa shop \"{shop.DisplayName}\"?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        account.Shops.Remove(shop);
        ActiveShopId = account.Shops[0].Id;
        RefreshShopList();
    }

    private void BrowseFile(TextBox target, string filter, string title)
    {
        using var d = new OpenFileDialog
        {
            Filter = filter,
            Title = title,
        };
        if (d.ShowDialog(this) == DialogResult.OK)
            target.Text = d.FileName;
    }

    private void BrowseWorkbook()
    {
        using var d = new OpenFileDialog
        {
            Filter = "Excel files (*.xlsx)|*.xlsx|All files (*.*)|*.*",
            Title = "Chọn file xlsx của account",
        };

        if (d.ShowDialog(this) != DialogResult.OK)
            return;

        _workbookTextBox.Text = d.FileName;
        if (CurrentAccount is { } account)
            account.WorkbookPath = d.FileName;
        LoadSheetOptions(d.FileName, CurrentShop?.ShopeeDataSheet);
    }

    private void LoadSheetOptions(string? workbookPath, string? selectedSheet)
    {
        var keep = selectedSheet?.Trim() ?? "";
        _shopeeSheetCombo.BeginUpdate();
        try
        {
            _shopeeSheetCombo.Items.Clear();
            _shopeeSheetCombo.Enabled = false;

            if (string.IsNullOrWhiteSpace(workbookPath) || !File.Exists(workbookPath))
                return;

            var sheets = ReadXlsxSheetNames(workbookPath);
            foreach (var sheet in sheets)
                _shopeeSheetCombo.Items.Add(sheet);

            if (_shopeeSheetCombo.Items.Count == 0)
                return;

            _shopeeSheetCombo.Enabled = true;
            var selectedIndex = keep.Length > 0 ? _shopeeSheetCombo.Items.IndexOf(keep) : -1;
            _shopeeSheetCombo.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Kh�ng d?c du?c danh s�ch sheet: {ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _shopeeSheetCombo.EndUpdate();
        }
    }

    private static List<string> ReadXlsxSheetNames(string workbookPath)
    {
        using var archive = ZipFile.OpenRead(workbookPath);
        var workbookEntry = archive.GetEntry("xl/workbook.xml")
            ?? throw new InvalidOperationException("File xlsx không có xl/workbook.xml.");

        using var stream = workbookEntry.Open();
        var doc = XDocument.Load(stream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

        return doc
            .Descendants(ns + "sheet")
            .Select(e => (string?)e.Attribute("name"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private sealed record SelectorItem(string Id, string Text)
    {
        public override string ToString() => Text;
    }
}
