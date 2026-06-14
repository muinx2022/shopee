namespace OpenMultiBraveLauncherV3;

internal sealed class AccountWorkspaceControl : UserControl
{
    private readonly LauncherSettingsFile _settings;
    private readonly string _accountId;
    private string _activeShopId = "";
    private readonly ShopeeWorkspaceControl _shopeeWorkspace;
    private readonly TabControl _tabs;
    private BigSellerWorkflowPanel? _bigSellerPanel;

    public AccountWorkspaceControl(LauncherSettingsFile settings, string accountId)
    {
        _settings = settings;
        _accountId = accountId;
        Dock = DockStyle.Fill;

        var account = ActiveAccount;
        _activeShopId = account.Shops.Any(s => s.Id == _settings.ActiveShopId)
            ? _settings.ActiveShopId
            : account.Shops.First().Id;

        _tabs = new TabControl { Dock = DockStyle.Fill };
        // Chừa khoảng trống trên để phần chi tiết tách khỏi dải tab, dễ nhìn hơn.
        var shopeePage = new TabPage("Shopee") { Padding = new Padding(0, 10, 0, 0) };
        var bigSellerPage = new TabPage("BigSeller") { Padding = new Padding(0, 10, 0, 0) };
        _tabs.TabPages.Add(shopeePage);
        _tabs.TabPages.Add(bigSellerPage);
        // 2 tab chính: to, xanh đậm, nổi bật.
        TabStyler.ApplyMain(_tabs);
        Controls.Add(_tabs);

        _shopeeWorkspace = new ShopeeWorkspaceControl(_settings, _accountId)
        {
            Dock = DockStyle.Fill,
        };
        _shopeeWorkspace.RunningStateChanged += NotifyRunningStateChanged;
        shopeePage.Controls.Add(_shopeeWorkspace);

        bigSellerPage.Controls.Add(CreateBigSellerHost());
    }

    public string AccountId => _accountId;

    public string AccountTitle => ActiveAccount.DisplayName;

    public bool HasRunningWork => _shopeeWorkspace.HasRunningWork || _bigSellerPanel?.HasRunningWork == true;

    public event Action? RunningStateChanged;

    public async Task ShutdownAsync()
    {
        await _shopeeWorkspace.ShutdownAsync().ConfigureAwait(true);
        if (_bigSellerPanel is not null)
            await _bigSellerPanel.ShutdownAsync().ConfigureAwait(true);
    }

    private BigSellerAccountConfig ActiveAccount =>
        _settings.Accounts.FirstOrDefault(a => a.Id == _accountId)
        ?? _settings.Accounts.First();

    private ShopConfig ActiveShop =>
        ActiveAccount.Shops.FirstOrDefault(s => s.Id == _activeShopId)
        ?? ActiveAccount.Shops.First();

    private Control CreateBigSellerHost()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(8),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var toolbar = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };
        var shopCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 260,
        };
        foreach (var shop in ActiveAccount.Shops)
            shopCombo.Items.Add(new SelectorItem(shop.Id, shop.DisplayName));
        var shopIndex = ActiveAccount.Shops.FindIndex(s => s.Id == _activeShopId);
        shopCombo.SelectedIndex = Math.Max(0, shopIndex);
        shopCombo.SelectedIndexChanged += (_, _) =>
        {
            if (shopCombo.SelectedItem is not SelectorItem item)
                return;

            _activeShopId = item.Id;
            RebuildBigSellerPanel(root);
        };

        var reloadButton = new Button { Text = "Tải lại", Width = 90, Height = 28 };
        reloadButton.Click += (_, _) => RebuildBigSellerPanel(root);
        toolbar.Controls.Add(new Label { Text = "Shop", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
        toolbar.Controls.Add(shopCombo);
        toolbar.Controls.Add(reloadButton);
        root.Controls.Add(toolbar, 0, 0);

        RebuildBigSellerPanel(root);
        return root;
    }

    private void RebuildBigSellerPanel(TableLayoutPanel root)
    {
        if (root.Controls.Count > 1)
        {
            var old = root.Controls[1];
            root.Controls.Remove(old);
            old.Dispose();
        }

        _bigSellerPanel = null;
        var account = ActiveAccount;
        var shop = ActiveShop;
        if (string.IsNullOrWhiteSpace(account.WorkbookPath) ||
            string.IsNullOrWhiteSpace(shop.ShopeeDataSheet))
        {
            root.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Text = "Cần cấu hình workbook và sheet Shopee cho shop trước khi dùng BigSeller.",
            }, 0, 1);
            return;
        }

        BigSellerProfileManager.EnsureWorkflowProfile(_settings.Accounts, account, shop);
        _bigSellerPanel = new BigSellerWorkflowPanel(BuildBigSellerSettings(account, shop), shop, _ =>
        {
            PersistSettings();
        })
        {
            Dock = DockStyle.Fill,
        };
        _bigSellerPanel.RunningStateChanged += NotifyRunningStateChanged;
        root.Controls.Add(_bigSellerPanel, 0, 1);
    }

    private void NotifyRunningStateChanged() => RunningStateChanged?.Invoke();

    private BigSellerWorkflowSettings BuildBigSellerSettings(BigSellerAccountConfig account, ShopConfig shop)
    {
        var pythonDir = Path.Combine(FindRepoRoot(), "update-product-python");
        var pythonExe = Path.Combine(pythonDir, ".venv", "Scripts", "python.exe");
        if (!File.Exists(pythonExe))
            pythonExe = "python";

        var defaultKeyFile = Path.Combine(FindRepoRoot(), "bigseller-tools", "openai.key");
        return new BigSellerWorkflowSettings
        {
            BravePath = _settings.BraveExe.Trim(),
            ProfileDir = Path.GetFullPath(AppSession.ResolvePersistentDataPath(shop.BigSellerProfileRelativePath)),
            DebugPort = shop.BigSellerDebugPort,
            ImportProfileDir = Path.GetFullPath(AppSession.ResolvePersistentDataPath(shop.BigSellerImportProfileRelativePath)),
            ImportDebugPort = shop.BigSellerImportDebugPort,
            ShopName = shop.DisplayName,
            WorkbookPath = account.WorkbookPath,
            DataSheet = shop.ShopeeDataSheet,
            BigSellerCookieFile = account.BigSellerCookieFile,
            StartRow = shop.BigSellerStartRow > 0 ? shop.BigSellerStartRow : 2,
            EndRow = shop.BigSellerEndRow,
            PythonDir = pythonDir,
            PythonExe = pythonExe,
            ImagePath = string.IsNullOrWhiteSpace(shop.BigSellerImagePath) ? @"D:\images\1.jpeg" : shop.BigSellerImagePath,
            VideoFolder = string.IsNullOrWhiteSpace(shop.BigSellerVideoFolder) ? @"D:\videos" : shop.BigSellerVideoFolder,
            CrawlUrl = string.IsNullOrWhiteSpace(shop.BigSellerCrawlUrl) ? BigSellerCrawlHelper.CrawlUrl : shop.BigSellerCrawlUrl,
            ImportFromClaimedTab = shop.BigSellerImportFromClaimedTab,
            ListingReloadSeconds = Math.Clamp(shop.BigSellerListingReloadSeconds, 3, 600),
            OpenAiModel = string.IsNullOrWhiteSpace(shop.OpenAiModel) ? "gpt-4.1-mini" : shop.OpenAiModel,
            OpenAiApiKeyFile = string.IsNullOrWhiteSpace(shop.OpenAiApiKeyFile) ? defaultKeyFile : shop.OpenAiApiKeyFile,
            OpenAiBatchSize = Math.Clamp(shop.OpenAiBatchSize, 1, 500),
        };
    }

    private void PersistSettings()
    {
        try { LauncherSettings.Save(_settings); } catch { }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "update-product-python")))
                return dir.FullName;
            dir = dir.Parent;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    }

    private sealed record SelectorItem(string Id, string Text)
    {
        public override string ToString() => Text;
    }
}
