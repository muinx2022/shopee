using System.IO.Compression;
using System.Xml.Linq;

namespace OpenMultiBraveLauncherV3;

internal sealed class AccountShopManagerForm : Form
{
    private readonly List<BigSellerAccountConfig> _accounts;
    private readonly string _braveExe;
    private readonly string _sourceUserData;
    private readonly PortAllocator _portAllocator = PortAllocator.Shared;
    private bool _suppress;
    private bool _closing;
    private BraveInstanceSession? _accountCookieSession;
    private int _accountCookiePort;

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
        var browseAccountWorkbook = new Button { Text = "Chọn...", Dock = DockStyle.Fill };
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
        var browseAccountCookie = new Button { Text = "Chọn...", Dock = DockStyle.Fill };
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
        _openBigSellerButton.Click += async (_, _) => await RunUiAsync(OpenBigSellerForAccountAsync, "Open BigSeller");
        _saveCookieButton = new Button { Text = "Save && close", AutoSize = true, Enabled = false };
        _saveCookieButton.Click += async (_, _) => await RunUiAsync(SaveAndCloseBigSellerCookieAsync, "Save BigSeller cookie");
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
            Visible = false,
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
        FormClosing += (_, _) =>
        {
            _closing = true;
            StopAccountCookieSession();
        };
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
                UseSharedProfiles = true,
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
        if (shop is not null)
            shop.UseSharedProfiles = true;
        _sharedProfilesCheckBox.Checked = true;
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
            MessageBox.Show(this, "TÃ i khoáº£n khÃ´ng Ä‘Æ°á»£c Ä‘á»ƒ trá»‘ng.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        account.Email = _accountTextBox.Text.Trim();
        account.WorkbookPath = _workbookTextBox.Text.Trim();
        account.BigSellerCookieFile = _cookieTextBox.Text.Trim();
        UpdateCookieStatus();
        RefreshAccountList();
    }

    private async Task RunUiAsync(Func<Task> action, string context)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogException($"AccountShopManagerForm.{context}", ex);
            SafeShowMessage(ex.Message, MessageBoxIcon.Error);
        }
    }

    private bool IsClosingOrDisposed => _closing || IsDisposed || Disposing;

    private void SafeShowMessage(string message, MessageBoxIcon icon)
    {
        if (IsClosingOrDisposed)
        {
            AppDiagnostics.Log($"AccountShopManagerForm skipped MessageBox: {message}");
            return;
        }

        try
        {
            MessageBox.Show(this, message, Text, MessageBoxButtons.OK, icon);
        }
        catch (ObjectDisposedException)
        {
            AppDiagnostics.Log($"AccountShopManagerForm MessageBox owner disposed: {message}");
        }
    }

    private async Task OpenBigSellerForAccountAsync()
    {
        var account = CurrentAccount;
        if (account is null) return;

        if (string.IsNullOrWhiteSpace(_braveExe) || !File.Exists(_braveExe))
        {
            SafeShowMessage("Chưa cấu hình brave.exe trong Cài đặt chung.", MessageBoxIcon.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(_sourceUserData) || !Directory.Exists(_sourceUserData))
        {
            SafeShowMessage("Chưa cấu hình User Data mẫu trong Cài đặt chung.", MessageBoxIcon.Warning);
            return;
        }

        StopAccountCookieSession();

        // Profile BỀN dùng chung per-account + port account: mọi thao tác BigSeller của account đi qua
        // đúng một profile/port → login persist, không đẻ phiên trùng.
        BigSellerProfileManager.EnsureAccountProfile(_accounts, account);
        var accountPort = account.Shops.Count > 0 ? account.Shops[0].BigSellerDebugPort : 0;

        // Brave account đã chạy sẵn (đang chạy update/import)? Tái dùng cửa sổ đó, KHÔNG mở Brave thứ hai
        // trên cùng thư mục — Brave sẽ gộp vào instance cũ nên port mới không mở ("connection refused").
        if (accountPort > 0 && await IsCdpAliveAsync(accountPort).ConfigureAwait(true))
        {
            SafeShowMessage(
                "BigSeller của account này đang mở sẵn (đang chạy workflow trên cùng profile). "
                + "Dùng cửa sổ Brave đang mở để kiểm tra/đăng nhập; muốn thao tác riêng thì dừng workflow trước.",
                MessageBoxIcon.Information);
            return;
        }

        var config = new InstanceConfig
        {
            Id = $"bigseller-{account.Id}",
            Label = $"BigSeller {account.DisplayName}",
            // Dùng CHUNG profile bền per-account với workflow (update/import/scrape) → login tay
            // persist qua các phiên + cùng một cookie jar, hết cảnh file snapshot chết vì rotation.
            ProfileRelativePath = Path.Combine("bigseller-profiles", account.Id),
            UsePersistentSharedProfile = true,
            // AutoImport lúc Start: jar chưa đăng nhập -> seed từ file account; đã đăng nhập (phiên sống)
            // -> KHÔNG đè, mà GHI token sống NGƯỢC về file account (master). Nhờ vậy master luôn tươi để
            // các Shopee instance import ra login được — đây là lý do BẮT BUỘC ExportBigSeller = true.
            BigSellerCookieFile = GetAccountCookieFile(account),
            ExportBigSeller = true,
            ExportShopee = false,
            // Login BigSeller bằng IP máy là chấp nhận được — không liên quan Shopee.
            RequireProxy = false,
        };

        _accountCookiePort = accountPort > 0 ? accountPort : _portAllocator.AllocateCookiePort();
        _accountCookieSession = new BraveInstanceSession(_accountCookiePort, _ => { });
        _accountCookieSession.ApplyConfig(config);

        try
        {
            _openBigSellerButton.Enabled = false;
            _saveCookieButton.Enabled = false;
            // StartAsync tự AutoImport cookie từ file account NẾU jar chưa đăng nhập (đã gate live-session);
            // profile đã có phiên sống thì giữ nguyên, không nạp đè → không văng login.
            await _accountCookieSession.StartAsync(_braveExe, _sourceUserData).ConfigureAwait(true);
            if (IsClosingOrDisposed) return;

            await _accountCookieSession.OpenBigSellerPageAsync().ConfigureAwait(true);
            if (IsClosingOrDisposed) return;
            _saveCookieButton.Enabled = true;
            _cookieStatusLabel.Text =
                "Profile BigSeller bền (dùng chung per-account). Nếu đã đăng nhập sẵn thì dùng luôn; "
                + "chưa thì đăng nhập rồi bấm Save & close.";
        }
        catch (Exception ex)
        {
            StopAccountCookieSession();
            SafeShowMessage(ex.Message, MessageBoxIcon.Error);
        }
        finally
        {
            if (!IsClosingOrDisposed)
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
            if (IsClosingOrDisposed) return;
            account.BigSellerCookieFile = fileName;
            _cookieTextBox.Text = fileName;
            await _accountCookieSession.StopAsync(CancellationToken.None).ConfigureAwait(true);
            if (IsClosingOrDisposed) return;
            _accountCookieSession.Dispose();
            _accountCookieSession = null;
            // Port account thuộc pool BigSeller (dùng lại xuyên suốt cho workflow) — không trả về pool cookie.
            _accountCookiePort = 0;
            UpdateCookieStatus();
        }
        catch (Exception ex)
        {
            if (!IsClosingOrDisposed)
                _saveCookieButton.Enabled = _accountCookieSession is not null;
            SafeShowMessage(ex.Message, MessageBoxIcon.Error);
        }
    }

    private void StopAccountCookieSession()
    {
        if (_accountCookieSession is null)
            return;

        try { _accountCookieSession.Stop(); } catch { }
        _accountCookieSession.Dispose();
        _accountCookieSession = null;
        // Port account thuộc pool BigSeller (dùng lại xuyên suốt cho workflow) — không trả về pool cookie.
        _accountCookiePort = 0;
        if (_saveCookieButton is not null)
            _saveCookieButton.Enabled = false;
    }

    /// <summary>Có Brave nào đang lắng nghe CDP ở port này không (account đang mở sẵn).</summary>
    private static async Task<bool> IsCdpAliveAsync(int port)
    {
        try
        {
            using var resp = await AppServices.DirectHttp
                .GetAsync($"http://127.0.0.1:{port}/json/version").ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private void UpdateCookieStatus()
    {
        if (_cookieStatusLabel is null)
            return;

        var path = _cookieTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            _cookieStatusLabel.Text = "Chưa có cookie BigSeller.";
            _cookieStatusLabel.ForeColor = Color.Firebrick;
            return;
        }

        if (File.Exists(path))
        {
            _cookieStatusLabel.Text = $"Đã lưu cookie: {Path.GetFileName(path)}";
            _cookieStatusLabel.ForeColor = Color.ForestGreen;
            return;
        }

        _cookieStatusLabel.Text = "Đã cấu hình đường dẫn cookie nhưng file không tồn tại.";
        _cookieStatusLabel.ForeColor = Color.DarkOrange;
    }

    private static string GetAccountCookieFile(BigSellerAccountConfig account) =>
        AppSession.ResolvePersistentDataPath("account-cookies", $"{account.Id}-bigseller.json");

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
        shop.UseSharedProfiles = true;
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
            MessageBox.Show(this, $"Không đọc được danh sách sheet: {ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

