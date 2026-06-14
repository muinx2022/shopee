using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace OpenMultiBraveLauncherV3;

public sealed class MainForm : Form
{
    private const int BaseCdpPort = 9330;

    private readonly LauncherSettingsFile _settings;
    private readonly List<InstanceEntry> _entries = [];
    private InstanceEntry? _selected;

    private readonly SplitContainer _actionSplit;
    private readonly SplitContainer _split;
    private readonly ListView _instanceList;
    private readonly InstanceDetailPanel _detailPanel;
    private readonly TabControl _logTabs;
    private readonly RichTextBox _logTextBox;
    private readonly ToolStripStatusLabel _statusStripLabel;
    private readonly System.Windows.Forms.Timer _allProgressTimer;
    private readonly BatchRunnerScheduler _batchScheduler = new();
    private readonly Dictionary<string, RichTextBox> _instanceLogTextBoxes = new(StringComparer.Ordinal);
    private Button? _startAutoButton;
    private Button? _stopAutoButton;
    private Button? _runSelectedButton;
    private Button? _stopAllButton;
    private Button? _autoSetRowButton;
    private Label? _autoShopSheetLabel;
    private CheckBox? _autoRunCheckBox;
    private NumericUpDown? _maxConcurrentInput;
    private NumericUpDown? _autoFromInput;
    private NumericUpDown? _autoToInput;
    private NumericUpDown? _rangeStartRowInput;
    private NumericUpDown? _rangeRowsPerProfileInput;
    private ComboBox? _accountComboBox;
    private ComboBox? _shopComboBox;
    private CheckBox? _sharedProfilesCheckBox;
    private bool _suppressShopSelection;
    private Button? _importButton;
    private Button? _stopImportButton;
    private CancellationTokenSource? _importCts;

    public MainForm()
    {
        _settings = LauncherSettings.LoadOrCreate();

        Text = "Multi Brave Manager v3";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 1220;
        Height = 1040;
        MinimumSize = new Size(980, 820);

        var actionColumn = CreateActionColumn();

        _instanceList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = true,
        };
        _instanceList.Columns.Add("T\u00ean", 100);
        _instanceList.Columns.Add("Tr\u1ea1ng th\u00e1i", 80);
        _instanceList.Columns.Add("Ti\u1ebfn \u0111\u1ed9", 200);
        _instanceList.Columns.Add("Proxy", 100);
        _instanceList.SelectedIndexChanged += (_, _) => OnInstanceSelected();

        _detailPanel = new InstanceDetailPanel();
        _detailPanel.ConfigChanged += OnDetailConfigChanged;
        _detailPanel.RunRequested += OnRunRequested;
        _detailPanel.ResumeContinueRequested += OnResumeContinueRequested;
        _detailPanel.StopRunnerRequested += OnStopRunnerRequested;

        _split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            Panel1MinSize = 25,
            Panel2MinSize = 25,
            SplitterWidth = 6,
        };
        var instancePanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            Padding = new Padding(0),
        };
        instancePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        instancePanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        instancePanel.Controls.Add(_instanceList, 0, 0);

        var instanceButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(0, 6, 0, 0),
        };
        var addInstanceButton = CreateTopActionButton("Th\u00eam instance", UiButtonHelper.Add());
        addInstanceButton.Width = 138;
        addInstanceButton.Click += (_, _) => AddInstance();
        var removeInstanceButton = CreateTopActionButton("X\u00f3a instance", UiButtonHelper.Remove());
        removeInstanceButton.Width = 138;
        removeInstanceButton.Click += (_, _) => RemoveSelected();
        instanceButtons.Controls.Add(addInstanceButton);
        instanceButtons.Controls.Add(removeInstanceButton);
        instancePanel.Controls.Add(instanceButtons, 0, 1);

        _split.Panel1.Controls.Add(instancePanel);
        _split.Panel2.Controls.Add(_detailPanel);

        _actionSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            Panel1MinSize = 80,
            Panel2MinSize = 80,
            SplitterWidth = 6,
        };
        var rightPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(8, 8, 8, 0),
        };
        rightPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rightPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        rightPanel.Controls.Add(CreateShopActionPanel(), 0, 0);
        rightPanel.Controls.Add(_split, 0, 1);

        _actionSplit.Panel1.Controls.Add(actionColumn);
        _actionSplit.Panel2.Controls.Add(rightPanel);

        var logGroup = new GroupBox { Text = "Nh\u1eadt k\u00fd", Dock = DockStyle.Bottom, Height = 220 };
        _logTabs = new TabControl { Dock = DockStyle.Fill };
        _logTextBox = CreateLogTextBox();
        var totalLogPage = new TabPage("T\u1ed5ng");
        totalLogPage.Controls.Add(_logTextBox);
        _logTabs.TabPages.Add(totalLogPage);
        logGroup.Controls.Add(_logTabs);

        var main = new Panel { Dock = DockStyle.Fill };
        main.Controls.Add(_actionSplit);
        main.Controls.Add(logGroup);

        _statusStripLabel = new ToolStripStatusLabel("S\u1eb5n s\u00e0ng");
        var statusStrip = new StatusStrip { Dock = DockStyle.Bottom };
        statusStrip.Items.Add(_statusStripLabel);

        Controls.Add(main);
        Controls.Add(statusStrip);

        _allProgressTimer = new System.Windows.Forms.Timer { Interval = 20_000 };
        _allProgressTimer.Tick += (_, _) => SyncAllExtensionProgress(silent: true);

        _batchScheduler.Configure(
            getMaxConcurrent: () => Math.Clamp(_settings.MaxConcurrentProfiles, 1, 50),
            canDispatchMore: () => CountOpenAutoProfiles() < Math.Clamp(_settings.MaxConcurrentProfiles, 1, 50),
            findNextUndispatched: FindNextBatchInstanceId,
            dispatchRunner: DispatchBatchRunnerAsync,
            onBatchComplete: dispatched =>
            {
                BeginInvoke(() =>
                {
                    AppendLog($"L\u01b0\u1ee3t auto ho\u00e0n t\u1ea5t - \u0111\u00e3 ch\u1ea1y {dispatched} profile.");
                    _statusStripLabel.Text = $"Auto xong ({dispatched} profile)";
                    UpdateAutoToolbarButtons();
                });
            },
            onDispatchError: (id, msg) =>
            {
                BeginInvoke(() =>
                {
                    var name = _entries.FirstOrDefault(e => e.Config.Id == id)?.Config.DisplayName ?? id;
                    AppendLog($"[{name}] L\u01b0\u1ee3t - l\u1ed7i kh\u1edfi ch\u1ea1y: {msg}");
                    UpdateBatchStatusLabel();
                });
            });

        Load += (_, _) => OnFormLoad();
        Shown += (_, _) =>
        {
            ApplySplitLayout();
            WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        };
        FormClosing += (_, _) =>
        {
            _allProgressTimer.Stop();
            _allProgressTimer.Dispose();
            _batchScheduler.Stop();
            _detailPanel.FlushToConfig();
            StopAll();
            PersistSettings();
        };
    }

    private static Button CreateSidebarActionButton(string text, Bitmap icon)
    {
        var button = new Button
        {
            Width = 176,
            Height = 34,
            Margin = new Padding(0, 0, 0, 6),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        UiButtonHelper.Style(button, icon, text);
        return button;
    }

    private static Button CreateTopActionButton(string text, Bitmap icon)
    {
        var button = new Button
        {
            Width = 132,
            Height = 34,
            Margin = new Padding(0, 0, 8, 0),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        UiButtonHelper.Style(button, icon, text);
        return button;
    }

    private Control CreateShopActionPanel()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 3,
            Margin = new Padding(0, 0, 0, 8),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var infoGroup = new GroupBox
        {
            Text = "Th\u00f4ng tin chung",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8),
            Margin = new Padding(0, 0, 0, 8),
        };
        var infoRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = true,
            Margin = new Padding(0),
        };
        infoRow.Controls.Add(new Label
        {
            Text = "Sheet \u0111ang ch\u1ecdn:",
            AutoSize = true,
            Padding = new Padding(0, 4, 6, 0),
        });
        _autoShopSheetLabel = new Label
        {
            AutoSize = true,
            ForeColor = Color.DimGray,
            Padding = new Padding(0, 4, 12, 0),
            Text = GetActiveShopSheetLabelText(),
        };
        infoRow.Controls.Add(_autoShopSheetLabel);
        infoGroup.Controls.Add(infoRow);

        var actionGroup = new GroupBox
        {
            Text = "Action",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8),
            Margin = new Padding(0, 0, 0, 8),
        };
        var actionRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = true,
            Margin = new Padding(0),
        };

        _runSelectedButton = CreateTopActionButton("Ch\u1ea1y \u0111\u00e3 ch\u1ecdn", UiButtonHelper.Run());
        _runSelectedButton.Click += async (_, _) => await StartSelectedAsync();
        _startAutoButton = CreateTopActionButton("Ch\u1ea1y t\u1ef1 \u0111\u1ed9ng", UiButtonHelper.Run());
        _startAutoButton.Click += async (_, _) => await StartAutoRunAsync();
        _stopAutoButton = CreateTopActionButton("D\u1eebng t\u1ef1 \u0111\u1ed9ng", UiButtonHelper.Stop());
        _stopAutoButton.Click += (_, _) => StopAutoRun();
        _stopAllButton = CreateTopActionButton("D\u1eebng t\u1ea5t c\u1ea3", UiButtonHelper.Stop());
        _stopAllButton.Click += (_, _) => StopAll();

        actionRow.Controls.Add(_runSelectedButton);
        actionRow.Controls.Add(_stopAllButton);
        actionGroup.Controls.Add(actionRow);

        var autoGroup = new GroupBox
        {
            Text = "Auto",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8),
            Margin = new Padding(0),
        };
        var autoTable = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 8,
            RowCount = 3,
            Margin = new Padding(0),
        };
        autoTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        autoTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
        autoTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        autoTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
        autoTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        autoTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
        autoTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        autoTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        autoTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        autoTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        autoTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _autoRunCheckBox = new CheckBox
        {
            Text = "Ch\u1ea1y t\u1ef1 \u0111\u1ed9ng",
            AutoSize = true,
            Checked = _settings.AutoRunEnabled,
            Margin = new Padding(0, 6, 12, 0),
        };
        _autoRunCheckBox.CheckedChanged += (_, _) =>
        {
            _settings.AutoRunEnabled = _autoRunCheckBox.Checked;
            PersistSettings();
            ApplyAutoRunUi();
        };

        _maxConcurrentInput = CreateAutoNumeric(1, 50, Math.Clamp(_settings.MaxConcurrentProfiles, 1, 50));
        _maxConcurrentInput.ValueChanged += (_, _) =>
        {
            _settings.MaxConcurrentProfiles = (int)_maxConcurrentInput.Value;
            PersistSettings();
            UpdateBatchStatusLabel();
        };

        _autoFromInput = CreateAutoNumeric(1, 999, Math.Max(1, _settings.AutoRunFromInstance));
        _autoFromInput.ValueChanged += (_, _) =>
        {
            _settings.AutoRunFromInstance = (int)_autoFromInput.Value;
            PersistSettings();
            UpdateBatchStatusLabel();
        };

        _autoToInput = CreateAutoNumeric(0, 999, Math.Max(0, _settings.AutoRunToInstance));
        _autoToInput.ValueChanged += (_, _) =>
        {
            _settings.AutoRunToInstance = (int)_autoToInput.Value;
            PersistSettings();
            UpdateBatchStatusLabel();
        };

        _rangeStartRowInput = CreateAutoNumeric(2, 1_000_000, Math.Max(2, _settings.RangeStartRow), width: 96);
        _rangeStartRowInput.ValueChanged += (_, _) => SaveAutoRangeInputs();
        _rangeRowsPerProfileInput = CreateAutoNumeric(1, 100_000, Math.Max(1, _settings.RangeRowsPerProfile), width: 96);
        _rangeRowsPerProfileInput.ValueChanged += (_, _) => SaveAutoRangeInputs();

        _autoSetRowButton = new Button { Text = "Set row", AutoSize = true, MinimumSize = new Size(88, 28), Margin = new Padding(0, 2, 0, 2) };
        _autoSetRowButton.Click += (_, _) => ApplyAutoRowRangesFromPanel();

        autoTable.Controls.Add(_autoRunCheckBox, 0, 0);
        autoTable.SetColumnSpan(_autoRunCheckBox, 2);
        autoTable.Controls.Add(_startAutoButton, 2, 0);
        autoTable.SetColumnSpan(_startAutoButton, 2);
        autoTable.Controls.Add(_stopAutoButton, 4, 0);
        autoTable.SetColumnSpan(_stopAutoButton, 2);

        autoTable.Controls.Add(new Label { Text = "Instance t\u1eeb", AutoSize = true, Padding = new Padding(0, 7, 6, 0) }, 0, 1);
        autoTable.Controls.Add(_autoFromInput, 1, 1);
        autoTable.Controls.Add(new Label { Text = "\u0111\u1ebfn", AutoSize = true, Padding = new Padding(10, 7, 6, 0) }, 2, 1);
        autoTable.Controls.Add(_autoToInput, 3, 1);
        autoTable.Controls.Add(new Label { Text = "Max", AutoSize = true, Padding = new Padding(10, 7, 6, 0) }, 4, 1);
        autoTable.Controls.Add(_maxConcurrentInput, 5, 1);

        autoTable.Controls.Add(new Label { Text = "T\u1eeb d\u00f2ng", AutoSize = true, Padding = new Padding(0, 7, 6, 0) }, 0, 2);
        autoTable.Controls.Add(_rangeStartRowInput, 1, 2);
        autoTable.Controls.Add(new Label { Text = "S\u1ed1 d\u00f2ng", AutoSize = true, Padding = new Padding(10, 7, 6, 0) }, 2, 2);
        var rowPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        rowPanel.Controls.Add(_rangeRowsPerProfileInput);
        rowPanel.Controls.Add(_autoSetRowButton);
        autoTable.Controls.Add(rowPanel, 3, 2);
        autoTable.SetColumnSpan(rowPanel, 5);

        autoGroup.Controls.Add(autoTable);
        root.Controls.Add(infoGroup, 0, 0);
        root.Controls.Add(actionGroup, 0, 1);
        root.Controls.Add(autoGroup, 0, 2);
        return root;
    }

    private static NumericUpDown CreateAutoNumeric(int min, int max, int value, int width = 76)
    {
        return new NumericUpDown
        {
            Minimum = min,
            Maximum = max,
            Width = width,
            Value = Math.Clamp(value, min, max),
            Margin = new Padding(0, 2, 8, 2),
        };
    }

    private Control CreateActionColumn()
    {
        var root = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
        };

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
        };

        _accountComboBox = new ComboBox
        {
            Width = 176,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Margin = new Padding(0, 0, 0, 6),
        };
        _accountComboBox.SelectedIndexChanged += (_, _) => OnAccountSelectionChanged();

        _shopComboBox = new ComboBox
        {
            Width = 176,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Margin = new Padding(0, 0, 0, 6),
        };
        _shopComboBox.SelectedIndexChanged += (_, _) => OnShopSelectionChanged();

        _sharedProfilesCheckBox = new CheckBox
        {
            AutoSize = true,
            Text = "D\u00f9ng chung profile",
            Margin = new Padding(0, 0, 0, 10),
        };
        _sharedProfilesCheckBox.CheckedChanged += (_, _) => OnSharedProfilesChanged();

        var manageAccountShopButton = CreateSidebarActionButton("Qu\u1ea3n l\u00fd account/shop...", UiButtonHelper.Settings());
        manageAccountShopButton.Click += (_, _) => ShowAccountShopManager();
        _importButton = CreateSidebarActionButton("Import...", UiButtonHelper.Import());
        _importButton.Click += async (_, _) => await ShowImportManagerAsync();
        _stopImportButton = CreateSidebarActionButton("Dừng Import", UiButtonHelper.Stop());
        _stopImportButton.Visible = false;
        _stopImportButton.Click += (_, _) => _importCts?.Cancel();
        var bigSellerToolsButton = CreateSidebarActionButton("BigSeller Tools...", UiButtonHelper.Settings());
        bigSellerToolsButton.Click += (_, _) => ShowBigSellerWorkflow();
        var settingsButton = CreateSidebarActionButton("C\u00e0i \u0111\u1eb7t chung...", UiButtonHelper.Settings());
        settingsButton.Click += (_, _) => ShowGlobalSettings();

        actions.Controls.Add(new Label { Text = "T\u00e0i kho\u1ea3n", AutoSize = true, Margin = new Padding(0, 0, 0, 3) });
        actions.Controls.Add(_accountComboBox);
        actions.Controls.Add(new Label { Text = "Shop", AutoSize = true, Margin = new Padding(0, 8, 0, 3) });
        actions.Controls.Add(_shopComboBox);
        actions.Controls.Add(_sharedProfilesCheckBox);
        actions.Controls.Add(manageAccountShopButton);
        actions.Controls.Add(_importButton);
        actions.Controls.Add(_stopImportButton);
        actions.Controls.Add(bigSellerToolsButton);
        actions.Controls.Add(settingsButton);

        root.Controls.Add(actions);
        return root;
    }

    private BigSellerAccountConfig ActiveAccount =>
        _settings.Accounts.FirstOrDefault(a => a.Id == _settings.ActiveAccountId)
        ?? _settings.Accounts.First();

    private ShopConfig ActiveShop =>
        ActiveAccount.Shops.FirstOrDefault(s => s.Id == _settings.ActiveShopId)
        ?? ActiveAccount.Shops.First();

    private void RefreshAccountShopSelectors()
    {
        if (_accountComboBox is null || _shopComboBox is null || _sharedProfilesCheckBox is null)
            return;

        _suppressShopSelection = true;
        try
        {
            _accountComboBox.Items.Clear();
            foreach (var account in _settings.Accounts)
                _accountComboBox.Items.Add(new SelectorItem(account.Id, account.DisplayName));

            var accountIndex = _settings.Accounts.FindIndex(a => a.Id == _settings.ActiveAccountId);
            _accountComboBox.SelectedIndex = Math.Max(0, accountIndex);

            RefreshShopSelectorCore();
        }
        finally
        {
            _suppressShopSelection = false;
        }
    }

    private void RefreshShopSelectorCore()
    {
        if (_shopComboBox is null || _sharedProfilesCheckBox is null)
            return;

        _shopComboBox.Items.Clear();
        foreach (var shop in ActiveAccount.Shops)
            _shopComboBox.Items.Add(new SelectorItem(shop.Id, shop.DisplayName));

        var shopIndex = ActiveAccount.Shops.FindIndex(s => s.Id == _settings.ActiveShopId);
        _shopComboBox.SelectedIndex = Math.Max(0, shopIndex);
        _sharedProfilesCheckBox.Checked = ActiveShop.UseSharedProfiles;
    }

    private void OnAccountSelectionChanged()
    {
        if (_suppressShopSelection || _accountComboBox?.SelectedItem is not SelectorItem item)
            return;

        _settings.ActiveAccountId = item.Id;
        var account = ActiveAccount;
        ApiServerHelper.ConfigureWorkbookPath(account.WorkbookPath);
        if (!account.Shops.Any(s => s.Id == _settings.ActiveShopId))
            _settings.ActiveShopId = account.Shops[0].Id;

        _suppressShopSelection = true;
        try
        {
            RefreshShopSelectorCore();
        }
        finally
        {
            _suppressShopSelection = false;
        }

        SyncActiveShopSheetToVisibleInstances();
        RefreshAutoShopSheetLabel();
        RefreshInstanceList();
        PersistSettings();
    }

    private void OnShopSelectionChanged()
    {
        if (_suppressShopSelection || _shopComboBox?.SelectedItem is not SelectorItem item)
            return;

        _settings.ActiveShopId = item.Id;
        if (_sharedProfilesCheckBox is not null)
            _sharedProfilesCheckBox.Checked = ActiveShop.UseSharedProfiles;
        SyncActiveShopSheetToVisibleInstances();
        RefreshAutoShopSheetLabel();
        RefreshInstanceList();
        PersistSettings();
    }

    private void OnSharedProfilesChanged()
    {
        if (_suppressShopSelection || _sharedProfilesCheckBox is null)
            return;

        ActiveShop.UseSharedProfiles = _sharedProfilesCheckBox.Checked;
        RefreshInstanceList();
        PersistSettings();
    }

    private void ShowAccountShopManager()
    {
        using var form = new AccountShopManagerForm(
            _settings.Accounts,
            _settings.ActiveAccountId,
            _settings.ActiveShopId,
            GetBraveExe(),
            GetSourceUserData());

        if (form.ShowDialog(this) != DialogResult.OK)
            return;

        _settings.Accounts = form.Accounts
            .Select(a => new BigSellerAccountConfig
            {
                Id = a.Id,
                Email = a.Email,
                WorkbookPath = a.WorkbookPath,
                BigSellerCookieFile = a.BigSellerCookieFile,
                Shops = a.Shops.Select(s => new ShopConfig
                {
                    Id = s.Id,
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
            })
            .ToList();
        _settings.ActiveAccountId = form.ActiveAccountId;
        _settings.ActiveShopId = form.ActiveShopId;

        RemoveEntries(GetOrphanedEntries());
        RefreshAccountShopSelectors();
        ApiServerHelper.ConfigureWorkbookPath(ActiveAccount.WorkbookPath);
        SyncActiveShopSheetToVisibleInstances();
        RefreshAutoShopSheetLabel();
        RefreshInstanceList();
        PersistSettings();
    }

    private async Task ShowImportManagerAsync()
    {
        using var form = new ImportManagerForm();
        if (form.ShowDialog(this) != DialogResult.OK)
            return;

        var shopeeLines = SplitShopeeImportLines(form.ShopeeAccountsText);
        var proxyKeys = SplitImportLines(form.ProxyKeysText);
        if (shopeeLines.Count == 0 && proxyKeys.Count == 0)
        {
            MessageBox.Show(this, "Không có dữ liệu import.", "Import", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var cts = new CancellationTokenSource();
        _importCts = cts;
        _importButton!.Enabled = false;
        _stopImportButton!.Visible = true;
        Cursor = Cursors.WaitCursor;
        try
        {
            if (proxyKeys.Count > 0 && shopeeLines.Count == 0)
                ApplyProxyImport(proxyKeys);

            if (shopeeLines.Count > 0)
                await ImportShopeeAccountsAsync(shopeeLines, proxyKeys, cts.Token).ConfigureAwait(true);

            RefreshInstanceList();
            PersistSettings();
            _statusStripLabel.Text = cts.IsCancellationRequested ? "Import đã dừng" : "Import xong";
        }
        finally
        {
            _importCts = null;
            _importButton!.Enabled = true;
            _stopImportButton!.Visible = false;
            Cursor = Cursors.Default;
        }
    }

    private void ShowBigSellerWorkflow()
    {
        var shop = ActiveShop;
        var account = ActiveAccount;
        if (string.IsNullOrWhiteSpace(account.WorkbookPath))
        {
            MessageBox.Show(this, "Account chưa cấu hình file xlsx. Vào Quản lý account/shop để chọn file xlsx.", "BigSeller Tools",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SyncActiveShopSheetToVisibleInstances();
        if (string.IsNullOrWhiteSpace(shop.ShopeeDataSheet))
        {
            MessageBox.Show(this, "Shop chưa cấu hình Sheet Shopee. Vào Quản lý account/shop để nhập sheet của shop.", "BigSeller Tools",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var pythonDir = Path.Combine(FindRepoRoot(), "update-product-python");
        var pythonExe = Path.Combine(pythonDir, ".venv", "Scripts", "python.exe");
        if (!File.Exists(pythonExe))
            pythonExe = "python";

        var defaultKeyFile = Path.Combine(FindRepoRoot(), "bigseller-tools", "openai.key");
        EnsureBigSellerWorkflowProfile(account, shop);
        var profileDir = Path.GetFullPath(AppSession.ResolvePersistentDataPath(shop.BigSellerProfileRelativePath));
        var importProfileDir = Path.GetFullPath(
            AppSession.ResolvePersistentDataPath(shop.BigSellerImportProfileRelativePath));
        var shopSettings = new BigSellerWorkflowSettings
        {
            BravePath = GetBraveExe(),
            ProfileDir = profileDir,
            DebugPort = shop.BigSellerDebugPort,
            ImportProfileDir = importProfileDir,
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

        using var form = new BigSellerWorkflowForm(shopSettings, shop, _ =>
        {
            RefreshAccountShopSelectors();
            PersistSettings();
        });
        form.ShowDialog(this);
    }

    private void EnsureBigSellerWorkflowProfile(BigSellerAccountConfig account, ShopConfig shop)
    {
        var expectedProfile = Path.Combine("bigseller-profiles", account.Id, shop.Id);
        if (string.IsNullOrWhiteSpace(shop.BigSellerProfileRelativePath) ||
            !shop.BigSellerProfileRelativePath.Contains(account.Id, StringComparison.OrdinalIgnoreCase))
            shop.BigSellerProfileRelativePath = expectedProfile;

        var expectedImportProfile = Path.Combine("bigseller-profiles", account.Id, $"{shop.Id}-import");
        if (string.IsNullOrWhiteSpace(shop.BigSellerImportProfileRelativePath) ||
            !shop.BigSellerImportProfileRelativePath.Contains(account.Id, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                shop.BigSellerImportProfileRelativePath,
                shop.BigSellerProfileRelativePath,
                StringComparison.OrdinalIgnoreCase))
            shop.BigSellerImportProfileRelativePath = expectedImportProfile;

        if (shop.BigSellerDebugPort <= 0)
            shop.BigSellerDebugPort = AllocateBigSellerDebugPort(shop.Id, shop.BigSellerImportDebugPort);

        if (shop.BigSellerImportDebugPort <= 0 ||
            shop.BigSellerImportDebugPort == shop.BigSellerDebugPort)
            shop.BigSellerImportDebugPort = AllocateBigSellerDebugPort(shop.Id, shop.BigSellerDebugPort);

        Directory.CreateDirectory(Path.GetFullPath(AppSession.ResolvePersistentDataPath(shop.BigSellerProfileRelativePath)));
        Directory.CreateDirectory(Path.GetFullPath(AppSession.ResolvePersistentDataPath(shop.BigSellerImportProfileRelativePath)));
        PersistSettings();
    }

    private int AllocateBigSellerDebugPort(string shopId, params int[] excludePorts)
    {
        var basePort = 9430 + AppSession.PortOffset;
        var excluded = excludePorts.Where(p => p > 0).ToHashSet();
        var used = _settings.Accounts
            .SelectMany(a => a.Shops)
            .Where(s => s.Id != shopId)
            .SelectMany(s => new[] { s.BigSellerDebugPort, s.BigSellerImportDebugPort })
            .Where(p => p > 0)
            .ToHashSet();
        foreach (var port in excluded)
            used.Add(port);

        for (var port = basePort; port < basePort + 500; port++)
        {
            if (!used.Contains(port))
                return port;
        }

        return basePort + 500;
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

    private static List<string> SplitImportLines(string text) =>
        (text ?? "")
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(line => !string.IsNullOrWhiteSpace(line))
        .ToList();

    private static List<string> SplitShopeeImportLines(string text)
    {
        var lines = SplitImportLines(text);
        if (lines.Count > 1)
            return lines;

        var matches = Regex.Matches(
            text ?? "",
            @"[^\s|]+\|[^\s|]+\|\.shopee\.vn=SPC_F=[^\s|]+",
            RegexOptions.IgnoreCase);
        return matches.Count > 0
            ? matches.Select(m => m.Value.Trim()).ToList()
            : lines;
    }

    private List<InstanceEntry> GetActiveShopEntries() =>
        _entries.Where(IsVisibleInActiveShop).ToList();

    private void SyncActiveShopSheetToVisibleInstances()
    {
        var shop = ActiveShop;
        if (string.IsNullOrWhiteSpace(shop.ShopeeDataSheet))
        {
            var migrated = GetActiveShopEntries()
                .Select(e => e.Config.DataSheet)
                .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
            if (!string.IsNullOrWhiteSpace(migrated))
                shop.ShopeeDataSheet = migrated.Trim();
        }

        if (string.IsNullOrWhiteSpace(shop.ShopeeDataSheet))
            return;

        foreach (var entry in GetActiveShopEntries())
        {
            entry.Config.DataSheet = shop.ShopeeDataSheet.Trim();
            entry.Config.BigSellerCookieFile = ActiveAccount.BigSellerCookieFile.Trim();
            entry.Session.ApplyConfig(entry.Config);
            RefreshListItem(entry.Config.Id);
        }
    }

    private void ApplyShopSheetToEntry(InstanceEntry entry)
    {
        var shop = ResolveShop(entry.Config);
        if (shop is null)
            return;

        if (string.IsNullOrWhiteSpace(shop.ShopeeDataSheet) && !string.IsNullOrWhiteSpace(entry.Config.DataSheet))
            shop.ShopeeDataSheet = entry.Config.DataSheet.Trim();

        if (!string.IsNullOrWhiteSpace(shop.ShopeeDataSheet))
        {
            entry.Config.DataSheet = shop.ShopeeDataSheet.Trim();
        }

        var account = ResolveAccount(entry.Config);
        if (account is not null)
            entry.Config.BigSellerCookieFile = account.BigSellerCookieFile.Trim();

        entry.Session.ApplyConfig(entry.Config);
    }

    private BigSellerAccountConfig? ResolveAccount(InstanceConfig config) =>
        _settings.Accounts.FirstOrDefault(a => a.Id == config.AccountId);

    private ShopConfig? ResolveShop(InstanceConfig config)
    {
        var account = ResolveAccount(config);
        return account?.Shops.FirstOrDefault(s => s.Id == config.ShopId);
    }

    private void ApplyProxyImport(List<string> proxyKeys)
    {
        var targets = GetActiveShopEntries();
        if (targets.Count == 0)
        {
            AppendLog("Import proxy: shop hiện tại chưa có profile.");
            return;
        }

        for (var i = 0; i < targets.Count; i++)
        {
            targets[i].Config.KiotProxyKey = proxyKeys[i % proxyKeys.Count].Trim();
            targets[i].Config.ManualProxy = "";
            targets[i].Session.ApplyConfig(targets[i].Config);
            RefreshListItem(targets[i].Config.Id);
        }

        AppendLog($"Import proxy: đã gán {proxyKeys.Count} key cho {targets.Count} profile trong shop.");
    }

    private async Task ApplyBigSellerImportForActiveShopAsync(string cookieFile)
    {
        if (!File.Exists(cookieFile))
        {
            MessageBox.Show(this, "Không tìm thấy file cookie BigSeller.", "Import BigSeller",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        ActiveAccount.BigSellerCookieFile = cookieFile.Trim();
        var targets = GetActiveShopEntries();
        foreach (var entry in targets)
        {
            entry.Config.BigSellerCookieFile = cookieFile.Trim();
            entry.Config.ExportBigSeller = true;
            entry.Session.ApplyConfig(entry.Config);
        }

        var running = targets.Where(e => e.Session.IsRunning).ToList();
        AppendLog($"Import BigSeller: áp dụng cookie cho {targets.Count} profile trong shop; {running.Count} profile đang mở sẽ import ngay.");
        foreach (var entry in running)
            await ImportBigSellerCookieIntoRunningProfileAsync(entry).ConfigureAwait(true);
    }

    private async Task ImportShopeeAccountsAsync(
        List<string> accountLines,
        List<string>? proxyKeys = null,
        CancellationToken cancellationToken = default)
    {
        var brave = GetBraveExe();
        var userData = GetSourceUserData();
        if (string.IsNullOrWhiteSpace(brave) || string.IsNullOrWhiteSpace(userData))
        {
            MessageBox.Show(this, "Cấu hình Brave exe và User Data mẫu trong Cài đặt chung.", "Import Shopee",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        AppendLog(proxyKeys is { Count: > 0 }
            ? $"Import Shopee: {accountLines.Count} profile, sẽ gán proxy trước khi đăng nhập."
            : $"Import Shopee: {accountLines.Count} profile.");

        var completed = 0;
        for (var i = 0; i < accountLines.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            var line = accountLines[i].Trim();
            var username = line.Split('|', 2)[0].Trim();
            var config = InstanceConfig.CreateNew(_entries.Count + 1);
            config.AccountId = ActiveAccount.Id;
            config.ShopId = ActiveShop.Id;
            config.Label = string.IsNullOrWhiteSpace(username) ? $"Shopee {i + 1}" : username;
            config.ShopeeAccountLogin = line;
            config.OpenWithShopeeAccount = true;
            config.CreateNewProfileOnNextStart = true;
            if (proxyKeys is { Count: > 0 })
            {
                config.KiotProxyKey = proxyKeys[i % proxyKeys.Count].Trim();
                config.ManualProxy = "";
            }

            AddInstanceCore(config, select: false);
            var entry = _entries.First(e => e.Config.Id == config.Id);
            entry.Session.ApplyConfig(config);
            RefreshListItem(config.Id);

            _statusStripLabel.Text = $"Import Shopee {i + 1}/{accountLines.Count}: {config.DisplayName}";
            var proxyNote = proxyKeys is { Count: > 0 } ? $" | proxy #{(i % proxyKeys.Count) + 1}" : "";
            AppendLog($"[{config.DisplayName}] Import Shopee: mở profile và đăng nhập ({i + 1}/{accountLines.Count}){proxyNote}.");

            try
            {
                await entry.Session.StartAsync(brave, userData).ConfigureAwait(true);
                await entry.Session.OpenShopeeAccountLoginAsync().ConfigureAwait(true);
                AppendLog($"[{config.DisplayName}] Import Shopee: chờ 30s trước khi đóng profile.");
                await Task.Delay(30_000, cancellationToken).ConfigureAwait(true);
                completed++;
            }
            catch (OperationCanceledException)
            {
                AppendLog($"[{config.DisplayName}] Import Shopee: dừng sớm theo lệnh người dùng.");
            }
            catch (Exception ex)
            {
                AppendLog($"[{config.DisplayName}] Import Shopee lỗi: {ex.Message}");
                completed++;
            }
            finally
            {
                try
                {
                    if (entry.Session.IsRunning)
                        await entry.Session.StopAsync(CancellationToken.None).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    AppendLog($"[{config.DisplayName}] Đóng profile lỗi: {ex.Message}");
                }

                config.OpenWithShopeeAccount = false;
                config.CreateNewProfileOnNextStart = false;
                entry.Session.ApplyConfig(config);
                RefreshListItem(config.Id);
                PersistSettings();
            }
        }

        AppendLog(cancellationToken.IsCancellationRequested
            ? $"Import Shopee: đã dừng sau {completed}/{accountLines.Count} profile."
            : $"Import Shopee: hoàn tất {accountLines.Count} profile.");
    }

    private List<InstanceEntry> GetOrphanedEntries()
    {
        var accountIds = _settings.Accounts.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var shopIdsByAccount = _settings.Accounts.ToDictionary(
            a => a.Id,
            a => a.Shops.Select(s => s.Id).ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);

        return _entries
            .Where(e =>
                !accountIds.Contains(e.Config.AccountId) ||
                !shopIdsByAccount.TryGetValue(e.Config.AccountId, out var shopIds) ||
                !shopIds.Contains(e.Config.ShopId))
            .ToList();
    }

    private void AddAccount()
    {
        var email = PromptText("Thêm tài khoản BigSeller", "Email / tên tài khoản", "abc@test.com");
        if (string.IsNullOrWhiteSpace(email))
            return;

        var account = new BigSellerAccountConfig { Email = email.Trim() };
        account.Shops.Add(ShopConfig.CreateDefault());
        _settings.Accounts.Add(account);
        _settings.ActiveAccountId = account.Id;
        _settings.ActiveShopId = account.Shops[0].Id;
        RefreshAccountShopSelectors();
        RefreshInstanceList();
        PersistSettings();
    }

    private void EditAccount()
    {
        var account = ActiveAccount;
        if (!ShowAccountSettingsDialog(account))
            return;

        RefreshAccountShopSelectors();
        PersistSettings();
    }

    private void DeleteAccount()
    {
        if (_settings.Accounts.Count <= 1)
        {
            MessageBox.Show(this, "Cần giữ lại ít nhất một tài khoản.", "Xóa tài khoản",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var account = ActiveAccount;
        var ownedInstances = _entries.Where(e => e.Config.AccountId == account.Id).ToList();
        var message =
            $"Xóa tài khoản \"{account.DisplayName}\"?\n\n" +
            $"Tài khoản này có {account.Shops.Count} shop và {ownedInstances.Count} instance.";
        if (MessageBox.Show(this, message, "Xóa tài khoản", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        RemoveEntries(ownedInstances);
        _settings.Accounts.Remove(account);
        var next = _settings.Accounts[0];
        _settings.ActiveAccountId = next.Id;
        _settings.ActiveShopId = next.Shops[0].Id;
        RefreshAccountShopSelectors();
        RefreshInstanceList();
        PersistSettings();
    }

    private void AddShop()
    {
        var defaultName = ActiveAccount.Shops.Count == 0 ? "Minoa Store" : $"Shop {ActiveAccount.Shops.Count + 1}";
        var name = PromptText("Thêm shop", "Tên shop", defaultName);
        if (string.IsNullOrWhiteSpace(name))
            return;

        var shop = new ShopConfig { Name = name.Trim(), UseSharedProfiles = true };
        ActiveAccount.Shops.Add(shop);
        _settings.ActiveShopId = shop.Id;
        RefreshAccountShopSelectors();
        RefreshInstanceList();
        PersistSettings();
    }

    private void EditShop()
    {
        var shop = ActiveShop;
        if (!ShowShopSettingsDialog(shop))
            return;

        RefreshAccountShopSelectors();
        RefreshInstanceList();
        PersistSettings();
    }

    private void DeleteShop()
    {
        var account = ActiveAccount;
        if (account.Shops.Count <= 1)
        {
            MessageBox.Show(this, "Cần giữ lại ít nhất một shop trong tài khoản.", "Xóa shop",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var shop = ActiveShop;
        var ownedInstances = _entries.Where(e => e.Config.AccountId == account.Id && e.Config.ShopId == shop.Id).ToList();
        var message =
            $"Xóa shop \"{shop.DisplayName}\"?\n\n" +
            $"Shop này có {ownedInstances.Count} instance gắn riêng.";
        if (MessageBox.Show(this, message, "Xóa shop", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        RemoveEntries(ownedInstances);
        account.Shops.Remove(shop);
        _settings.ActiveShopId = account.Shops[0].Id;
        RefreshAccountShopSelectors();
        RefreshInstanceList();
        PersistSettings();
    }

    private void RemoveEntries(List<InstanceEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.Session.IsRunning)
                entry.Session.Stop();
            entry.Session.Dispose();
            _entries.Remove(entry);
            CloseInstanceLogTab(entry.Config);
        }
    }

    private static string? PromptText(string title, string label, string defaultValue)
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(380, 120),
        };

        var labelControl = new Label { Text = label, AutoSize = true, Location = new Point(12, 14) };
        var input = new TextBox { Text = defaultValue, Location = new Point(12, 38), Width = 356 };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(194, 78), Width = 80 };
        var cancel = new Button { Text = "Hủy", DialogResult = DialogResult.Cancel, Location = new Point(288, 78), Width = 80 };
        form.Controls.AddRange([labelControl, input, ok, cancel]);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog() == DialogResult.OK ? input.Text.Trim() : null;
    }

    private bool ShowShopSettingsDialog(ShopConfig shop)
    {
        using var form = new Form
        {
            Text = "Cài đặt shop",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(560, 210),
        };

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(10),
            ColumnCount = 3,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));

        var nameText = new TextBox { Text = shop.Name, Dock = DockStyle.Fill };
        var sharedCheck = new CheckBox
        {
            Text = "Dùng chung profile trong tài khoản này",
            Checked = shop.UseSharedProfiles,
            AutoSize = true,
        };


        table.Controls.Add(new Label { Text = "Tên shop", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 0, 0);
        table.Controls.Add(nameText, 1, 0);
        table.SetColumnSpan(nameText, 2);
        table.Controls.Add(sharedCheck, 1, 1);
        table.SetColumnSpan(sharedCheck, 2);

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 88 };
        var cancel = new Button { Text = "Hủy", DialogResult = DialogResult.Cancel, Width = 88 };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 46,
            Padding = new Padding(8),
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        form.Controls.Add(table);
        form.Controls.Add(buttons);
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        if (form.ShowDialog(this) != DialogResult.OK)
            return false;

        if (string.IsNullOrWhiteSpace(nameText.Text))
        {
            MessageBox.Show(this, "Tên shop không được để trống.", "Cài đặt shop",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        shop.Name = nameText.Text.Trim();
        shop.UseSharedProfiles = sharedCheck.Checked;
        return true;
    }

    private bool ShowAccountSettingsDialog(BigSellerAccountConfig account)
    {
        using var form = new Form
        {
            Text = "Cài đặt tài khoản",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(460, 126),
        };

        var label = new Label
        {
            Text = "Email / tên tài khoản",
            AutoSize = true,
            Location = new Point(12, 16),
        };
        var input = new TextBox
        {
            Text = account.Email,
            Location = new Point(12, 42),
            Width = 436,
        };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(266, 84), Width = 84 };
        var cancel = new Button { Text = "Hủy", DialogResult = DialogResult.Cancel, Location = new Point(364, 84), Width = 84 };
        form.Controls.AddRange([label, input, ok, cancel]);
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        if (form.ShowDialog(this) != DialogResult.OK)
            return false;

        if (string.IsNullOrWhiteSpace(input.Text))
        {
            MessageBox.Show(this, "Tài khoản không được để trống.", "Cài đặt tài khoản",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        account.Email = input.Text.Trim();
        return true;
    }

    private bool IsVisibleInActiveShop(InstanceEntry entry)
    {
        if (entry.Config.AccountId != ActiveAccount.Id)
            return false;

        return ActiveShop.UseSharedProfiles || entry.Config.ShopId == ActiveShop.Id;
    }

    private void RefreshInstanceList()
    {
        _detailPanel.FlushToConfig();
        _instanceList.BeginUpdate();
        try
        {
            _instanceList.Items.Clear();
            foreach (var entry in _entries.Where(IsVisibleInActiveShop))
                _instanceList.Items.Add(CreateListItem(entry));
        }
        finally
        {
            _instanceList.EndUpdate();
        }

        _selected = null;
        if (_instanceList.Items.Count > 0)
        {
            _instanceList.Items[0].Selected = true;
            _instanceList.Items[0].Focused = true;
            OnInstanceSelected();
        }
        else
        {
            _detailPanel.Bind(null, null, GetBraveExe, GetSourceUserData);
        }
    }

    private static ListViewItem CreateListItem(InstanceEntry entry)
    {
        var item = new ListViewItem(entry.Config.DisplayName);
        item.SubItems.Add(entry.Session.StatusText);
        item.SubItems.Add(TruncateProgress(entry.Config.ProgressSummary));
        item.SubItems.Add(entry.Session.ProxySummary);
        item.Tag = entry.Config.Id;
        return item;
    }

    private sealed record SelectorItem(string Id, string Text)
    {
        public override string ToString() => Text;
    }

    private void ApplySplitLayout()
    {
        var outerWidth = _actionSplit.ClientSize.Width;
        if (outerWidth >= 720)
        {
            var actionWidth = Math.Clamp(210, _actionSplit.Panel1MinSize, outerWidth - _actionSplit.Panel2MinSize - _actionSplit.SplitterWidth);
            try { _actionSplit.SplitterDistance = actionWidth; }
            catch { /* bỏ qua */ }
        }

        var w = _split.ClientSize.Width;
        if (w < 120) return;
        const int minLeft = 300;
        const int minRight = 360;
        var maxLeft = w - minRight - _split.SplitterWidth;
        if (maxLeft < minLeft)
        {
            // Cửa sổ hẹp: không ép min size SplitContainer (tránh crash lúc khởi động)
            try { _split.SplitterDistance = Math.Max(80, w / 3); }
            catch { /* bỏ qua */ }
            return;
        }

        var left = (int)(w * 0.34);
        left = Math.Clamp(left, minLeft, maxLeft);
        try
        {
            if (Math.Abs(_split.SplitterDistance - left) > 2)
                _split.SplitterDistance = left;
        }
        catch
        {
            try { _split.SplitterDistance = minLeft; }
            catch { /* bỏ qua */ }
        }
    }

    private void OnFormLoad()
    {
        if (string.IsNullOrWhiteSpace(_settings.BraveExe))
        {
            var brave = DetectBraveExe();
            if (brave is not null) _settings.BraveExe = brave.FullName;
        }

        if (string.IsNullOrWhiteSpace(_settings.SourceUserData))
        {
            var ud = DetectBraveUserData();
            if (ud is not null) _settings.SourceUserData = ud.FullName;
        }

        RefreshAccountShopSelectors();
        ApiServerHelper.ConfigureWorkbookPath(ActiveAccount.WorkbookPath);

        foreach (var config in _settings.Instances)
            AddInstanceCore(config, select: false);

        if (_entries.Count == 0)
            AddInstance();

        RefreshInstanceList();

        if (_entries.Count > 0)
        {
            if (_instanceList.Items.Count > 0)
            {
                _instanceList.Items[0].Selected = true;
                _instanceList.Select();
            }
        }

        AppendLog($"Runtime session: {AppSession.SessionId}");
        AppendLog($"Session data: {AppSession.RootDirectory}");
        AppendLog($"CDP port offset: +{AppSession.PortOffset}");
        AppendLog($"API server: {ApiServerHelper.DefaultApiBase}");
        AppendLog($"API workbook: {ActiveAccount.WorkbookPath}");

        if (File.Exists(LauncherSettings.SettingsPath))
            AppendLog($"Đã tải cấu hình: {LauncherSettings.SettingsPath}");
        else
            AppendLog("Chưa có launcher-settings.json — đã tạo cấu hình mặc định (hoặc import key từ v1).");

        _allProgressTimer.Start();
        SyncAllExtensionProgress(silent: true);
        ApplyAutoRunUi();
    }

    private void ApplyAutoRunUi()
    {
        if (!_settings.AutoRunEnabled && _batchScheduler.IsActive)
            _batchScheduler.Stop();

        _detailPanel.SetAutoRunMode(_settings.AutoRunEnabled);
        SyncAutoPanelFromSettings();
        UpdateAutoToolbarButtons();
    }

    private void UpdateAutoToolbarButtons()
    {
        if (_startAutoButton is null || _stopAutoButton is null) return;

        var auto = _settings.AutoRunEnabled;
        _startAutoButton.Visible = true;
        _stopAutoButton.Visible = true;
        _startAutoButton.Enabled = auto && !_batchScheduler.IsActive;
        _stopAutoButton.Enabled = auto && _batchScheduler.IsActive;
        if (_runSelectedButton is not null)
            _runSelectedButton.Enabled = !auto;
        if (_stopAllButton is not null)
            _stopAllButton.Enabled = !auto;
        UpdateAutoConfigControlsEnabled(auto);
    }

    private void UpdateAutoConfigControlsEnabled(bool enabled)
    {
        if (_maxConcurrentInput is not null)
            _maxConcurrentInput.Enabled = enabled;
        if (_autoFromInput is not null)
            _autoFromInput.Enabled = enabled;
        if (_autoToInput is not null)
            _autoToInput.Enabled = enabled;
        if (_rangeStartRowInput is not null)
            _rangeStartRowInput.Enabled = enabled;
        if (_rangeRowsPerProfileInput is not null)
            _rangeRowsPerProfileInput.Enabled = enabled;
        if (_autoSetRowButton is not null)
            _autoSetRowButton.Enabled = enabled;
    }

    private string GetActiveShopSheetLabelText()
    {
        var sheet = ActiveShop.ShopeeDataSheet?.Trim();
        return string.IsNullOrWhiteSpace(sheet) ? "(shop ch\u01b0a ch\u1ecdn sheet)" : sheet;
    }

    private void RefreshAutoShopSheetLabel()
    {
        if (_autoShopSheetLabel is not null)
            _autoShopSheetLabel.Text = GetActiveShopSheetLabelText();
    }

    private void SyncAutoPanelFromSettings()
    {
        if (_autoRunCheckBox is not null && _autoRunCheckBox.Checked != _settings.AutoRunEnabled)
            _autoRunCheckBox.Checked = _settings.AutoRunEnabled;
        if (_maxConcurrentInput is not null)
            _maxConcurrentInput.Value = Math.Clamp(_settings.MaxConcurrentProfiles, 1, 50);
        if (_autoFromInput is not null)
            _autoFromInput.Value = Math.Max(1, _settings.AutoRunFromInstance);
        if (_autoToInput is not null)
            _autoToInput.Value = Math.Max(0, _settings.AutoRunToInstance);
        if (_rangeStartRowInput is not null)
            _rangeStartRowInput.Value = Math.Max(2, _settings.RangeStartRow);
        if (_rangeRowsPerProfileInput is not null)
            _rangeRowsPerProfileInput.Value = Math.Max(1, _settings.RangeRowsPerProfile);
        RefreshAutoShopSheetLabel();
    }

    private void SaveAutoRangeInputs()
    {
        if (_rangeStartRowInput is null || _rangeRowsPerProfileInput is null)
            return;

        _settings.RangeStartRow = (int)_rangeStartRowInput.Value;
        _settings.RangeRowsPerProfile = (int)_rangeRowsPerProfileInput.Value;
        _settings.AutoStartRow = _settings.RangeStartRow;
        _settings.AutoRowsPerProfile = _settings.RangeRowsPerProfile;
        PersistSettings();
    }

    private void ApplyAutoRowRangesFromPanel()
    {
        if (_rangeStartRowInput is null || _rangeRowsPerProfileInput is null)
            return;

        var sheetName = ActiveShop.ShopeeDataSheet?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(sheetName))
        {
            MessageBox.Show(this, "Shop ch\u01b0a ch\u1ecdn sheet. V\u00e0o Qu\u1ea3n l\u00fd account/shop \u0111\u1ec3 ch\u1ecdn sheet tr\u01b0\u1edbc.", "Set row",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _settings.RangeSheetName = sheetName;
        _settings.AutoSheetName = sheetName;
        _settings.RangeStartRow = (int)_rangeStartRowInput.Value;
        _settings.RangeRowsPerProfile = (int)_rangeRowsPerProfileInput.Value;
        _settings.AutoStartRow = _settings.RangeStartRow;
        _settings.AutoRowsPerProfile = _settings.RangeRowsPerProfile;
        ApplyAutoRowRangesToInstances(
            _settings.RangeSheetName,
            _settings.RangeStartRow,
            _settings.RangeRowsPerProfile);
        PersistSettings();
    }

    private async Task RefreshSheetsWhenApiReadyAsync()
    {
        if (await ApiServerHelper.EnsureReachableAsync(msg => AppendLog(msg)))
            await _detailPanel.RefreshSheetsFromWorkbookAsync();
    }

    private void SyncAllExtensionProgress(bool silent)
    {
        _ = SyncAllExtensionProgressAsync(silent);
    }

    private async Task SyncAllExtensionProgressAsync(bool silent)
    {
        var any = false;
        foreach (var entry in _entries)
        {
            if (!await entry.Session.SyncExtensionProgressAsync(silent: true))
                continue;
            any = true;
            RefreshListItem(entry.Config.Id);
            if (_selected?.Config.Id == entry.Config.Id)
                _detailPanel.RefreshProgressFromConfig();
        }

        if (any)
            PersistSettings();
    }

    private void AddInstance()
    {
        _detailPanel.FlushToConfig();
        var config = InstanceConfig.CreateNew(_entries.Count + 1);
        config.AccountId = ActiveAccount.Id;
        config.ShopId = ActiveShop.Id;
        AddInstanceCore(config, select: true);
        PersistSettings();
    }

    private void AddInstanceCore(InstanceConfig config, bool select)
    {
        config.EnsureProfileRelativePath();
        var cdpPort = BaseCdpPort + AppSession.PortOffset + _entries.Count;
        var session = new BraveInstanceSession(cdpPort, msg => AppendLog($"[{config.DisplayName}] {msg}"));
        session.ApplyConfig(config);
        session.LogLine += msg => BeginInvoke(() => AppendInstanceLog(config, msg));
        session.StatusChanged += () => BeginInvoke(() =>
        {
            RefreshListItem(config.Id);
            if (session.IsRunning)
            {
                EnsureInstanceLogTab(config, selectTab: _selected?.Config.Id == config.Id);
            }
            else
            {
                CloseInstanceLogTab(config);
                if (_batchScheduler.IsActive)
                {
                    _batchScheduler.TryFillSlots();
                    UpdateBatchStatusLabel();
                }
            }
        });
        session.ExtensionProgressSynced += () =>
        {
            BeginInvoke(() =>
            {
                if (_selected?.Config.Id == config.Id)
                    _detailPanel.RefreshProgressFromConfig();
                RefreshListItem(config.Id);
                PersistSettings();
            });
        };
        session.ExtensionInterrupted += interruptedConfig =>
        {
            BeginInvoke(() =>
            {
                if (_selected?.Config.Id == config.Id)
                    _detailPanel.RefreshProgressFromConfig();
                RefreshListItem(config.Id);
                var resume = interruptedConfig.SuggestedResumeRow;
                _statusStripLabel.Text =
                    $"{interruptedConfig.DisplayName}: dừng tại dòng {interruptedConfig.StoppedAtRow} → chạy tiếp {resume}";
            });
        };
        session.RunnerLoopEnded += instanceId =>
        {
            BeginInvoke(async () =>
            {
                var endedEntry = _entries.FirstOrDefault(e => e.Config.Id == instanceId);
                if (endedEntry is not null && IsCaptchaPaused(endedEntry.Config))
                {
                    await HandleCaptchaHandoffAsync(endedEntry).ConfigureAwait(true);
                    return;
                }

                RefreshListItem(instanceId);
                if (_selected?.Config.Id == instanceId)
                    _detailPanel.RefreshProgressFromConfig();
                PersistSettings();
                _batchScheduler.OnRunnerLoopEnded(instanceId);
                UpdateBatchStatusLabel();
            });
        };

        var entry = new InstanceEntry(config, session, cdpPort);
        _entries.Add(entry);

        if (IsVisibleInActiveShop(entry))
        {
            var item = CreateListItem(entry);
            _instanceList.Items.Add(item);
            if (select)
            {
                item.Selected = true;
                item.Focused = true;
            }
        }
        else if (select)
        {
            RefreshInstanceList();
        }
    }

    private void RemoveSelected()
    {
        _detailPanel.FlushToConfig();

        if (_instanceList.SelectedItems.Count == 0)
        {
            MessageBox.Show(this, "Chọn một hoặc nhiều instance cần xóa (Ctrl/Shift + click).",
                "Xóa", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var ids = _instanceList.SelectedItems
            .Cast<ListViewItem>()
            .Select(i => i.Tag as string)
            .Where(id => !string.IsNullOrEmpty(id))
            .Cast<string>()
            .Distinct()
            .ToList();

        var toRemove = _entries.Where(e => ids.Contains(e.Config.Id)).ToList();
        if (toRemove.Count == 0) return;

        var runningCount = toRemove.Count(e => e.Session.IsRunning);
        var msg = runningCount > 0
            ? $"Xóa {toRemove.Count} instance ({runningCount} đang chạy sẽ bị dừng)?"
            : $"Xóa {toRemove.Count} instance đã chọn?";
        if (MessageBox.Show(this, msg, "Xóa", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        foreach (var entry in toRemove)
        {
            if (entry.Session.IsRunning)
                entry.Session.Stop();
            entry.Session.Dispose();
            _entries.Remove(entry);
        }

        foreach (ListViewItem item in _instanceList.Items.Cast<ListViewItem>()
                     .Where(i => i.Tag is string id && ids.Contains(id)).ToList())
            _instanceList.Items.Remove(item);

        _selected = null;
        if (_instanceList.Items.Count > 0)
        {
            _instanceList.Items[0].Selected = true;
            OnInstanceSelected();
        }
        else
        {
            _detailPanel.Bind(null, null, GetBraveExe, GetSourceUserData);
        }

        PersistSettings();
        _statusStripLabel.Text = $"Đã xóa {toRemove.Count} instance";
    }

    private List<InstanceEntry> GetSelectedEntries()
    {
        var ids = _instanceList.SelectedItems
            .Cast<ListViewItem>()
            .Select(i => i.Tag as string)
            .Where(id => !string.IsNullOrEmpty(id))
            .Cast<string>()
            .Distinct()
            .ToHashSet(StringComparer.Ordinal);

        return _entries.Where(e => ids.Contains(e.Config.Id)).ToList();
    }

    private async Task StartSelectedAsync()
    {
        if (_settings.AutoRunEnabled)
        {
            MessageBox.Show(this,
                "Đang bật chế độ auto — dùng \"Chạy tự động\" ở phần Auto.",
                "Chạy đã chọn", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _detailPanel.FlushToConfig();

        var selected = GetSelectedEntries();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "Chọn một hoặc nhiều instance cần chạy (Ctrl/Shift + click).",
                "Chạy đã chọn", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var brave = GetBraveExe();
        var userData = GetSourceUserData();
        if (string.IsNullOrWhiteSpace(brave) || string.IsNullOrWhiteSpace(userData))
        {
            MessageBox.Show(this, "Cấu hình Brave exe và User Data mẫu trong Cài đặt chung.",
                "Chạy đã chọn", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var ready = new List<(InstanceEntry Entry, int RunRow)>();
        var skipped = new List<string>();
        foreach (var entry in selected)
        {
            var c = entry.Config;
            if (entry.Session.IsRunnerLoopPending || entry.Session.IsBusy)
            {
                skipped.Add($"{c.DisplayName}: đang chạy/bận");
                continue;
            }

            if (string.IsNullOrWhiteSpace(c.DataSheet) || c.StartRow is not > 0)
            {
                skipped.Add($"{c.DisplayName}: thiếu sheet hoặc từ dòng");
                continue;
            }

            var runRow = c.GetEffectiveRunRow();
            if (runRow is not > 0)
            {
                skipped.Add($"{c.DisplayName}: dòng tiếp theo không hợp lệ");
                continue;
            }

            if (!c.TryValidateRunRow(runRow.Value, out var rangeErr))
            {
                skipped.Add($"{c.DisplayName}: {rangeErr ?? "dòng tiếp theo ngoài phạm vi"}");
                continue;
            }

            c.NextRunRow = runRow;
            ready.Add((entry, runRow.Value));
        }

        if (ready.Count == 0)
        {
            MessageBox.Show(this,
                "Không có instance nào sẵn sàng để chạy.\n\n" + string.Join("\n", skipped),
                "Chạy đã chọn", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _runSelectedButton!.Enabled = false;
        try
        {
            var index = 0;
            foreach (var (entry, runRow) in ready)
            {
                index++;
                var c = entry.Config;
                _statusStripLabel.Text = $"Chạy đã chọn {index}/{ready.Count}: {c.DisplayName}";
                AppendLog($"[{c.DisplayName}] Chạy đã chọn — mở profile & chạy sheet \"{c.DataSheet}\" từ dòng {runRow} (phạm vi {c.StartRow}–{c.EndRow?.ToString() ?? "hết"})…");
                AppendInstanceLog(c, $"Chạy đã chọn — từ dòng {runRow} (sheet \"{c.DataSheet}\")…", selectTab: index == 1);

                entry.Session.ApplyConfig(c);
                await RunManualWithExtensionRetryAsync(entry, brave, userData, preferSuggestedResume: true)
                    .ConfigureAwait(true);

                for (var wait = 0; wait < 90 && !entry.Session.IsRunnerLoopPending; wait++)
                    await Task.Delay(500).ConfigureAwait(true);

                RefreshListItem(c.Id);
                PersistSettings();

                await Task.Delay(3000).ConfigureAwait(true);
            }

            if (skipped.Count > 0)
                AppendLog("Chạy đã chọn — bỏ qua: " + string.Join("; ", skipped));

            _statusStripLabel.Text = $"Đã kích hoạt {ready.Count}/{selected.Count} instance đã chọn";
        }
        finally
        {
            _runSelectedButton!.Enabled = !_settings.AutoRunEnabled;
        }
    }

    private async Task StartAllAsync()
    {
        if (_settings.AutoRunEnabled)
        {
            MessageBox.Show(this,
                "Đang bật chế độ auto — profile được mở bởi \"Chạy tự động\", không mở thủ công.",
                "Mở profile tất cả", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        SyncDetailToConfig();
        var brave = GetBraveExe();
        var userData = GetSourceUserData();
        if (string.IsNullOrWhiteSpace(brave) || string.IsNullOrWhiteSpace(userData))
        {
            MessageBox.Show(this, "Cấu hình Brave exe và User Data mẫu trong Cài đặt chung.", "Mở profile tất cả",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var total = _entries.Count;
        var ok = 0;
        for (var i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            if (entry.Session.IsRunning) continue;

            _statusStripLabel.Text = $"Mở profile {i + 1}/{total}: {entry.Config.DisplayName}";
            entry.Session.ApplyConfig(entry.Config);
            try
            {
                await entry.Session.StartAsync(brave, userData);
                ok++;
            }
            catch (Exception ex)
            {
                AppendLog($"[{entry.Config.DisplayName}] Lỗi: {ex.Message}");
            }

            RefreshListItem(entry.Config.Id);
        }

        _statusStripLabel.Text = $"Mở profile tất cả xong ({ok}/{total})";
        PersistSettings();
    }

    private void StopAll()
    {
        _batchScheduler.Stop();
        foreach (var entry in _entries)
        {
            if (entry.Session.IsRunning)
                entry.Session.Stop();
            RefreshListItem(entry.Config.Id);
        }
        _statusStripLabel.Text = "Đã dừng tất cả";
    }

    private async Task StartAutoRunAsync()
    {
        if (!_settings.AutoRunEnabled)
        {
            MessageBox.Show(this,
                "Bật \"Chạy tự động (lượt)\" trong phần Auto trước.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        await StartBatchRunAsync().ConfigureAwait(true);
        UpdateAutoToolbarButtons();
    }

    private void StopAutoRun()
    {
        StopBatchRun();
        UpdateAutoToolbarButtons();
    }

    private async Task StartBatchRunAsync()
    {
        _detailPanel.FlushToConfig();

        if (_batchScheduler.IsActive)
        {
            MessageBox.Show(this, "Lượt auto đang chạy.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var brave = GetBraveExe();
        var userData = GetSourceUserData();
        if (string.IsNullOrWhiteSpace(brave) || string.IsNullOrWhiteSpace(userData))
        {
            MessageBox.Show(this, "Cấu hình Brave exe và User Data mẫu trong Cài đặt chung.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var (rangeFrom, rangeTo) = GetAutoRunRange();
        var rangeLabel = rangeTo >= _entries.Count
            ? $"instance {rangeFrom} → hết (tổng {_entries.Count})"
            : $"instance {rangeFrom}–{rangeTo}";

        var max = Math.Clamp(_settings.MaxConcurrentProfiles, 1, 50);
        var msg =
            $"Chạy tự động trong phạm vi {rangeLabel},\n" +
            $"tối đa {max} profile đồng thời.\n\n" +
            "Runner/profile cũ (lượt trước hoặc ngoài phạm vi) sẽ được dừng trước.\n\n" +
            "Tiếp tục?";
        if (MessageBox.Show(this, msg, "Chạy tự động", MessageBoxButtons.YesNo, MessageBoxIcon.Question) !=
            DialogResult.Yes)
            return;

        await PrepareForNewAutoBatchAsync(rangeFrom, rangeTo).ConfigureAwait(true);

        var eligible = _entries.Where(e => IsEligibleForBatch(e)).ToList();
        if (eligible.Count == 0)
        {
            MessageBox.Show(this,
                "Không có profile nào sẵn sàng trong phạm vi auto (cần sheet, \"Từ dòng\", và nằm trong khoảng instance đã chọn).",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _batchScheduler.Start();
        AppendLog($"Chạy tự động: {eligible.Count} profile ({rangeLabel}), tối đa {max} đồng thời.");
        UpdateBatchStatusLabel();
        UpdateAutoToolbarButtons();
        _batchScheduler.TryFillSlots();
    }

    /// <summary>Dừng runner cũ và đóng profile ngoài phạm vi trước khi bắt lượt auto mới.</summary>
    private async Task PrepareForNewAutoBatchAsync(int rangeFrom, int rangeTo)
    {
        var anyRunner = _entries.Any(e => e.Session.IsRunnerLoopActive);
        var anyOutsideRunning = _entries.Any(e =>
        {
            var idx = _entries.IndexOf(e) + 1;
            return (idx < rangeFrom || idx > rangeTo) && e.Session.IsRunning;
        });

        if (!anyRunner && !anyOutsideRunning)
            return;

        AppendLog("Chuẩn bị lượt auto mới — dừng runner/profile cũ…");
        _statusStripLabel.Text = "Đang dừng runner cũ…";

        foreach (var entry in _entries)
        {
            var idx = _entries.IndexOf(entry) + 1;
            var inRange = idx >= rangeFrom && idx <= rangeTo;

            if (entry.Session.IsRunnerLoopActive)
            {
                try
                {
                    entry.Session.ApplyConfig(entry.Config);
                    await entry.Session.StopRunnerAsync().ConfigureAwait(true);
                    AppendLog($"[{entry.Config.DisplayName}] Đã dừng runner (lượt cũ).");
                }
                catch (Exception ex)
                {
                    AppendLog($"[{entry.Config.DisplayName}] Dừng runner: {ex.Message}");
                }
            }

            if (!inRange && entry.Session.IsRunning)
            {
                try
                {
                    await entry.Session.StopAsync(CancellationToken.None).ConfigureAwait(true);
                    AppendLog($"[{entry.Config.DisplayName}] Đóng profile (ngoài phạm vi {rangeFrom}–{(rangeTo >= _entries.Count ? "hết" : rangeTo)}).");
                }
                catch (Exception ex)
                {
                    AppendLog($"[{entry.Config.DisplayName}] Đóng profile: {ex.Message}");
                }
            }

            RefreshListItem(entry.Config.Id);
        }

        for (var i = 0; i < 40; i++)
        {
            if (!_entries.Any(e => e.Session.IsRunnerLoopActive))
                break;
            await Task.Delay(250).ConfigureAwait(true);
        }

        PersistSettings();
    }

    private void StopBatchRun()
    {
        if (!_batchScheduler.IsActive)
        {
            MessageBox.Show(this, "Auto chưa chạy.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _batchScheduler.Stop();
        AppendLog("Đã dừng auto — profile đang chạy vẫn tiếp tục, không tự bật profile mới.");
        _statusStripLabel.Text = "Đã dừng auto";
        UpdateAutoToolbarButtons();
    }

    private (int from, int to) GetAutoRunRange()
    {
        var total = _entries.Count;
        if (total == 0) return (1, 0);

        var from = Math.Max(1, _settings.AutoRunFromInstance);
        if (from > total) from = total;

        var to = _settings.AutoRunToInstance <= 0
            ? total
            : Math.Clamp(_settings.AutoRunToInstance, from, total);

        return (from, to);
    }

    private bool IsInAutoRunRange(InstanceEntry entry)
    {
        var idx = _entries.IndexOf(entry);
        if (idx < 0) return false;
        var (from, to) = GetAutoRunRange();
        var oneBased = idx + 1;
        return oneBased >= from && oneBased <= to;
    }

    private bool IsEligibleForBatch(InstanceEntry entry) =>
        IsVisibleInActiveShop(entry) &&
        IsInAutoRunRange(entry) &&
        IsBatchConfigured(entry) &&
        !entry.Session.IsRunnerLoopActive;

    private static bool IsCaptchaPaused(InstanceConfig config) =>
        string.Equals(config.RunnerPhase, "paused", StringComparison.OrdinalIgnoreCase) &&
        (config.LastRunnerMessage?.Contains("captcha", StringComparison.OrdinalIgnoreCase) == true);

    private async Task HandleCaptchaHandoffAsync(InstanceEntry source)
    {
        var autoActive = _batchScheduler.IsActive;
        if (autoActive)
            _batchScheduler.ReleaseSlotWithoutFill(source.Config.Id);

        var replacement = FindCaptchaReplacement(source, autoActive);
        var sourceRow = source.Config.CurrentRow
                        ?? source.Config.NextRunRow
                        ?? source.Config.GetEffectiveRunRow()
                        ?? source.Config.StartRow;

        AppendLog(
            $"[{source.Config.DisplayName}] Gap captcha tai dong {sourceRow?.ToString() ?? "?"} - dong profile va chuyen sang instance ke tiep.");
        AppendInstanceLog(source.Config,
            $"Gap captcha tai dong {sourceRow?.ToString() ?? "?"} - dong profile.", selectTab: true);

        try
        {
            if (source.Session.IsRunning)
                await source.Session.StopAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppendLog($"[{source.Config.DisplayName}] Dong profile sau captcha loi: {ex.Message}");
        }

        RefreshListItem(source.Config.Id);

        if (replacement is null)
        {
            if (autoActive)
                _batchScheduler.Stop();
            _statusStripLabel.Text = "Captcha: khong con instance ke tiep de chuyen.";
            AppendLog("Captcha: khong con instance ke tiep chua chay - dung.");
            PersistSettings();
            UpdateAutoToolbarButtons();
            return;
        }

        var shiftedEntries = ShiftRunnerConfigsDownForCaptchaHandoff(replacement, autoActive);
        if (shiftedEntries is null)
        {
            if (autoActive)
                _batchScheduler.Stop();
            _statusStripLabel.Text = "Captcha: khong dich duoc config sang profile sau.";
            AppendLog(
                $"[{source.Config.DisplayName}] Captcha: khong dich duoc config tu [{replacement.Config.DisplayName}] sang profile sau - dung de tranh mat config.");
            PersistSettings();
            UpdateAutoToolbarButtons();
            return;
        }

        CopyRunnerConfigForCaptchaHandoff(source.Config, replacement.Config);
        replacement.Session.ApplyConfig(replacement.Config);
        foreach (var entry in shiftedEntries)
            entry.Session.ApplyConfig(entry.Config);
        foreach (var entry in shiftedEntries.Prepend(replacement).DistinctBy(e => e.Config.Id))
            RefreshListItem(entry.Config.Id);
        if (_selected?.Config.Id == replacement.Config.Id)
            _detailPanel.RefreshProgressFromConfig();
        PersistSettings();

        if (shiftedEntries.Count > 0)
        {
            AppendLog(
                $"[{source.Config.DisplayName}] Da dich config tu [{replacement.Config.DisplayName}] xuong {shiftedEntries.Count} profile phia sau truoc khi handoff.");
        }
        AppendLog(
            $"[{source.Config.DisplayName}] Copy runner config sang [{replacement.Config.DisplayName}] va chay tiep tu dong {replacement.Config.NextRunRow?.ToString() ?? "?"}.");
        AppendInstanceLog(replacement.Config,
            $"Nhan config tu {source.Config.DisplayName}, chay tiep tu dong {replacement.Config.NextRunRow?.ToString() ?? "?"}.",
            selectTab: true);

        if (autoActive)
        {
            if (!_batchScheduler.ReserveSlot(replacement.Config.Id))
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await DispatchBatchRunnerAsync(replacement.Config.Id).ConfigureAwait(false);
                }
                finally
                {
                    _batchScheduler.TryFillSlots();
                }
            });
        }
        else
        {
            await StartManualCaptchaReplacementAsync(replacement).ConfigureAwait(true);
        }

        UpdateBatchStatusLabel();
    }

    private InstanceEntry? FindCaptchaReplacement(InstanceEntry source, bool autoActive)
    {
        var dispatched = autoActive
            ? _batchScheduler.GetDispatchedSnapshot()
            : new HashSet<string>(StringComparer.Ordinal);

        var sourceIndex = _entries.IndexOf(source);
        if (sourceIndex < 0)
            return null;

        var startIndex = sourceIndex + 1;
        if (autoActive && dispatched.Count > 0)
        {
            var lastDispatchedIndex = _entries
                .Select((entry, index) => dispatched.Contains(entry.Config.Id) ? index : -1)
                .DefaultIfEmpty(-1)
                .Max();
            if (lastDispatchedIndex >= 0)
                startIndex = lastDispatchedIndex + 1;
        }

        for (var i = startIndex; i < _entries.Count; i++)
        {
            var candidate = _entries[i];
            if (autoActive && (!IsInAutoRunRange(candidate) || dispatched.Contains(candidate.Config.Id)))
                continue;
            if (candidate.Session.IsRunning ||
                candidate.Session.IsBusy ||
                candidate.Session.IsRunnerLoopActive ||
                candidate.Session.IsRunnerLoopPending)
                continue;
            return candidate;
        }

        return null;
    }

    private List<InstanceEntry>? ShiftRunnerConfigsDownForCaptchaHandoff(InstanceEntry replacement, bool autoActive)
    {
        var replacementIndex = _entries.IndexOf(replacement);
        if (replacementIndex < 0)
            return null;

        var displaced = CaptureRunnerConfig(replacement.Config);
        if (!displaced.HasMeaningfulConfig)
            return [];

        var dispatched = autoActive
            ? _batchScheduler.GetDispatchedSnapshot()
            : new HashSet<string>(StringComparer.Ordinal);
        var affected = new List<InstanceEntry>();

        for (var i = replacementIndex + 1; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            if (autoActive && (!IsInAutoRunRange(entry) || dispatched.Contains(entry.Config.Id)))
                continue;
            if (entry.Session.IsRunning ||
                entry.Session.IsBusy ||
                entry.Session.IsRunnerLoopActive ||
                entry.Session.IsRunnerLoopPending)
                continue;

            var current = CaptureRunnerConfig(entry.Config);
            ApplyRunnerConfigSnapshot(displaced, entry.Config);
            affected.Add(entry);

            if (!current.HasMeaningfulConfig)
                return affected;

            displaced = current;
        }

        return null;
    }

    private static void CopyRunnerConfigForCaptchaHandoff(InstanceConfig source, InstanceConfig target)
    {
        var resumeRow = source.CurrentRow
                        ?? source.NextRunRow
                        ?? source.GetEffectiveRunRow()
                        ?? source.StartRow;

        target.DataSheet = source.DataSheet;
        target.StartRow = source.StartRow;
        target.EndRow = source.EndRow;
        target.NextRunRow = resumeRow;
        target.CurrentRow = source.CurrentRow;
        target.LastCompletedRow = source.LastCompletedRow;
        target.LastSku = source.LastSku;
        target.RunnerPhase = null;
        target.RunnerRunning = false;
        target.LastRunnerMessage = "";
        target.RunLog.Clear();
        target.ProgressSyncedAt = null;
    }

    private static RunnerConfigSnapshot CaptureRunnerConfig(InstanceConfig config) =>
        new(
            config.DataSheet,
            config.StartRow,
            config.EndRow,
            config.NextRunRow,
            config.CurrentRow,
            config.LastCompletedRow,
            config.LastSku,
            config.RunnerPhase,
            config.RunnerRunning,
            config.LastRunnerMessage,
            config.RunLog
                .Select(entry => new RunnerLogEntry
                {
                    RowNumber = entry.RowNumber,
                    Sku = entry.Sku,
                    ScrapeOk = entry.ScrapeOk,
                    VideoOk = entry.VideoOk,
                    VideoPath = entry.VideoPath,
                })
                .ToList(),
            config.ProgressSyncedAt);

    private static void ApplyRunnerConfigSnapshot(RunnerConfigSnapshot snapshot, InstanceConfig target)
    {
        target.DataSheet = snapshot.DataSheet;
        target.StartRow = snapshot.StartRow;
        target.EndRow = snapshot.EndRow;
        target.NextRunRow = snapshot.NextRunRow;
        target.CurrentRow = snapshot.CurrentRow;
        target.LastCompletedRow = snapshot.LastCompletedRow;
        target.LastSku = snapshot.LastSku;
        target.RunnerPhase = snapshot.RunnerPhase;
        target.RunnerRunning = snapshot.RunnerRunning;
        target.LastRunnerMessage = snapshot.LastRunnerMessage;
        target.RunLog = snapshot.RunLog
            .Select(entry => new RunnerLogEntry
            {
                RowNumber = entry.RowNumber,
                Sku = entry.Sku,
                ScrapeOk = entry.ScrapeOk,
                VideoOk = entry.VideoOk,
                VideoPath = entry.VideoPath,
            })
            .ToList();
        target.ProgressSyncedAt = snapshot.ProgressSyncedAt;
    }

    private async Task StartManualCaptchaReplacementAsync(InstanceEntry replacement)
    {
        var brave = GetBraveExe();
        var userData = GetSourceUserData();
        if (string.IsNullOrWhiteSpace(brave) || string.IsNullOrWhiteSpace(userData))
        {
            AppendLog($"[{replacement.Config.DisplayName}] Khong chay duoc instance thay the: thieu Brave/User Data.");
            return;
        }

        try
        {
            _statusStripLabel.Text =
                $"{replacement.Config.DisplayName}: chay thay profile captcha tu dong {replacement.Config.NextRunRow}";
            await RunManualWithExtensionRetryAsync(replacement, brave, userData, preferSuggestedResume: true)
                .ConfigureAwait(true);
            RefreshListItem(replacement.Config.Id);
            PersistSettings();
        }
        catch (Exception ex)
        {
            AppendLog($"[{replacement.Config.DisplayName}] Chay thay profile captcha loi: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Chay thay profile captcha", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private string? FindNextBatchInstanceId(HashSet<string> dispatched)
    {
        foreach (var entry in _entries)
        {
            if (dispatched.Contains(entry.Config.Id)) continue;
            if (!IsEligibleForBatch(entry)) continue;
            if (entry.Session.IsRunnerLoopActive) continue;
            return entry.Config.Id;
        }
        return null;
    }

    private int CountOpenAutoProfiles() =>
        _entries.Count(e =>
            IsInAutoRunRange(e) &&
            (e.Session.IsRunning || e.Session.IsBusy || e.Session.IsRunnerLoopPending));

    private async Task DispatchBatchRunnerAsync(string instanceId)
    {
        if (!_batchScheduler.IsActive)
            return;

        var entry = _entries.FirstOrDefault(e => e.Config.Id == instanceId);
        if (entry is null) return;

        var brave = GetBraveExe();
        var userData = GetSourceUserData();
        if (string.IsNullOrWhiteSpace(brave) || string.IsNullOrWhiteSpace(userData))
            throw new InvalidOperationException("Thiếu cấu hình Brave.");

        var c = entry.Config;
        var runRow = c.GetEffectiveRunRow();
        if (runRow is not > 0)
            throw new InvalidOperationException("Dòng tiếp theo không hợp lệ.");
        if (!c.TryValidateRunRow(runRow.Value, out var rangeErr))
            throw new InvalidOperationException(rangeErr ?? "Dòng tiếp theo ngoài phạm vi.");
        c.NextRunRow = runRow;

        // Chờ profile khác mở xong trước khi bật thêm (tránh 2 StartAsync song song).
        for (var w = 0; w < 120; w++)
        {
            var otherBusy = _entries.Any(e =>
                e.Config.Id != instanceId &&
                (e.Session.IsBusy || e.Session.IsRunnerLoopPending));
            if (!otherBusy) break;
            await Task.Delay(500).ConfigureAwait(false);
        }

        if (!_batchScheduler.IsActive)
            return;

        BeginInvoke(() =>
        {
            AppendLog($"[{c.DisplayName}] Auto — mở profile & chạy sheet \"{c.DataSheet}\" từ dòng {runRow} (phạm vi {c.StartRow}–{c.EndRow?.ToString() ?? "hết"})…");
            UpdateBatchStatusLabel();
        });

        entry.Session.ApplyConfig(c);
        await entry.Session.ResumeContinueAsync(
                brave,
                userData,
                preferSuggestedResume: true,
                retryExtensionStart: true)
            .ConfigureAwait(false);

        // StartAsync + 3s chờ CDP — cần thời gian dài hơn 6s cũ.
        for (var i = 0; i < 90 && !entry.Session.IsRunnerLoopPending; i++)
            await Task.Delay(500).ConfigureAwait(false);

        if (!entry.Session.IsRunnerLoopPending)
        {
            BeginInvoke(() =>
                AppendLog($"[{c.DisplayName}] Auto — runner không khởi động được, đóng profile rồi mới mở slot kế."));

            if (entry.Session.IsRunning)
            {
                try
                {
                    await entry.Session.StopAsync(CancellationToken.None).ConfigureAwait(false);
                    BeginInvoke(() =>
                        AppendLog($"[{c.DisplayName}] Auto — đóng profile vì runner không chạy."));
                }
                catch
                {
                    // ignore
                }
            }

            BeginInvoke(() =>
            {
                _batchScheduler.ReleaseSlot(instanceId);
                UpdateBatchStatusLabel();
            });
            return;
        }

        // Cách vài giây trước profile kế để CDP/extension ổn định.
        await Task.Delay(3000).ConfigureAwait(false);

        BeginInvoke(() =>
        {
            AppendLog($"[{c.DisplayName}] Auto — runner đã chạy scrape.");
            RefreshListItem(c.Id);
            if (_selected?.Config.Id == c.Id)
                _detailPanel.RefreshProgressFromConfig();
            PersistSettings();
            UpdateBatchStatusLabel();
        });
    }

    private void UpdateBatchStatusLabel()
    {
        if (!_batchScheduler.IsActive)
            return;

        var max = Math.Clamp(_settings.MaxConcurrentProfiles, 1, 50);
        var (from, to) = GetAutoRunRange();
        var rangeTotal = _entries.Count(e => IsInAutoRunRange(e) && IsBatchConfigured(e));
        _statusStripLabel.Text =
            $"Auto ({from}–{(to >= _entries.Count ? "hết" : to.ToString())}): " +
            $"{_batchScheduler.ActiveSlotCount}/{max} đang chạy, " +
            $"{_batchScheduler.DispatchedCount} dispatch / {rangeTotal} trong phạm vi";
        UpdateAutoToolbarButtons();
    }

    private static bool IsBatchConfigured(InstanceEntry entry)
    {
        var c = entry.Config;
        if (string.IsNullOrWhiteSpace(c.DataSheet) || c.StartRow is not > 0)
            return false;
        var runRow = c.GetEffectiveRunRow();
        return runRow is > 0 && c.TryValidateRunRow(runRow.Value, out _);
    }

    private void ShowGlobalSettings()
    {
        using var form = new Form
        {
            Text = "C\u00e0i \u0111\u1eb7t chung",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(760, 160),
        };

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(8),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 2,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var bravePathTextBox = new TextBox { Dock = DockStyle.Fill, Text = _settings.BraveExe };
        var userDataTextBox = new TextBox { Dock = DockStyle.Fill, Text = _settings.SourceUserData };

        table.Controls.Add(new Label { Text = "Brave exe", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 0, 0);
        table.Controls.Add(bravePathTextBox, 1, 0);
        var browseExe = new Button { Text = "Ch\u1ecdn...", AutoSize = true, MinimumSize = new Size(72, 28) };
        browseExe.Click += (_, _) => BrowseExe(bravePathTextBox);
        table.Controls.Add(browseExe, 2, 0);

        table.Controls.Add(new Label { Text = "User Data m\u1eabu", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 0, 1);
        table.Controls.Add(userDataTextBox, 1, 1);
        var browseUd = new Button { Text = "Ch\u1ecdn...", AutoSize = true, MinimumSize = new Size(72, 28) };
        browseUd.Click += (_, _) => BrowseUserData(userDataTextBox);
        table.Controls.Add(browseUd, 2, 1);

        var btnRow = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 8, 0, 0),
        };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(88, 32) };
        var cancel = new Button { Text = "H\u1ee7y", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(88, 32) };
        btnRow.Controls.Add(ok);
        btnRow.Controls.Add(cancel);

        root.Controls.Add(table, 0, 0);
        root.Controls.Add(btnRow, 0, 1);
        form.Controls.Add(root);
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        if (form.ShowDialog(this) == DialogResult.OK)
        {
            _settings.BraveExe = bravePathTextBox.Text.Trim();
            _settings.SourceUserData = userDataTextBox.Text.Trim();
            PersistSettings();
        }
    }
    private void ApplyImportBigSellerToAllProfiles(bool enabled, string cookieFile, bool importRunningNow = true)
    {
        _detailPanel.FlushToConfig();

        foreach (var entry in _entries)
        {
            entry.Config.ExportBigSeller = enabled;
            entry.Config.BigSellerCookieFile = cookieFile.Trim();
            entry.Session.ApplyConfig(entry.Config);
            if (importRunningNow && enabled && entry.Session.IsRunning && File.Exists(entry.Config.BigSellerCookieFile))
                _ = ImportBigSellerCookieIntoRunningProfileAsync(entry);
            RefreshListItem(entry.Config.Id);
        }

        if (_selected is not null)
            _detailPanel.Bind(_selected.Config, _selected.Session, GetBraveExe, GetSourceUserData);
    }

    private async Task ImportBigSellerCookieIntoRunningProfilesAsync(string cookieFile, bool enabled)
    {
        if (!enabled)
        {
            AppendLog("Import BigSeller đang tắt — bật checkbox rồi bấm Import ngay.");
            return;
        }

        var runningEntries = _entries.Where(e => e.Session.IsRunning).ToList();
        if (runningEntries.Count == 0)
        {
            AppendLog("Không có profile nào đang mở để import BigSeller. Profile chưa mở sẽ tự import khi chạy.");
            return;
        }

        AppendLog($"Import BigSeller thủ công cho {runningEntries.Count} profile đang mở…");
        foreach (var entry in runningEntries)
        {
            entry.Config.BigSellerCookieFile = cookieFile.Trim();
            entry.Config.ExportBigSeller = true;
            entry.Session.ApplyConfig(entry.Config);
            await ImportBigSellerCookieIntoRunningProfileAsync(entry).ConfigureAwait(true);
        }
    }

    private async Task ImportBigSellerCookieIntoRunningProfileAsync(InstanceEntry entry)
    {
        try
        {
            if (!await entry.Session.WaitForCdpReadyAsync().ConfigureAwait(true))
            {
                AppendLog($"[{entry.Config.DisplayName}] Bo qua import BigSeller: profile dang restart hoac CDP da tat.");
                RefreshListItem(entry.Config.Id);
                return;
            }

            var count = await entry.Session.ImportCookiesFromFileAsync(
                entry.Config.BigSellerCookieFile,
                includeShopee: false,
                includeBigSeller: true,
                prepareBigSellerPage: true).ConfigureAwait(true);
            AppendLog($"[{entry.Config.DisplayName}] Đã nhập {count} cookie BigSeller.");
        }
        catch (Exception ex)
        {
            AppendLog($"[{entry.Config.DisplayName}] Nhập cookie BigSeller lỗi: {ex.Message}");
        }
    }

    private void SeedAutoSheetCombo(ComboBox combo)
    {
        var candidates = new[]
            {
                _settings.RangeSheetName,
                _settings.AutoSheetName,
                _entries.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.Config.DataSheet))?.Config.DataSheet,
            }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var sheet in candidates)
            combo.Items.Add(sheet);

        var selected = candidates.FirstOrDefault(s =>
            string.Equals(s, _settings.RangeSheetName, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(selected))
            combo.SelectedItem = selected;

        _ = LoadAutoSheetComboAsync(combo);
    }

    private async Task LoadAutoSheetComboAsync(ComboBox combo)
    {
        List<string> sheets;
        try
        {
            if (!await ApiServerHelper.EnsureReachableAsync(tryAutoStart: true).ConfigureAwait(true))
                return;
            sheets = await ApiServerHelper.GetSheetNamesAsync().ConfigureAwait(true);
        }
        catch
        {
            return;
        }

        if (combo.IsDisposed)
            return;

        var keep = combo.SelectedItem?.ToString()
                   ?? _settings.RangeSheetName
                   ?? _settings.AutoSheetName
                   ?? _entries.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.Config.DataSheet))?.Config.DataSheet
                   ?? "";

        combo.BeginUpdate();
        try
        {
            combo.Items.Clear();
            foreach (var sheet in sheets)
                combo.Items.Add(sheet);

            if (!string.IsNullOrWhiteSpace(keep))
            {
                var existing = combo.Items
                    .Cast<object>()
                    .FirstOrDefault(x => string.Equals(x.ToString(), keep, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    combo.Items.Add(keep);
                    existing = keep;
                }
                combo.SelectedItem = existing;
            }
            else if (combo.Items.Count > 0)
            {
                combo.SelectedIndex = 0;
            }
        }
        finally
        {
            combo.EndUpdate();
        }
    }

    private void ApplyAutoRowRangesToInstances(string autoSheetName, int autoStartRow, int rowsPerProfile)
    {
        var targets = GetActiveShopEntries();
        if (targets.Count == 0)
            return;

        _detailPanel.FlushToConfig();

        var start = Math.Max(2, autoStartRow);
        var step = Math.Max(1, rowsPerProfile);
        foreach (var entry in targets)
        {
            var c = entry.Config;
            var end = checked(start + step);

            c.DataSheet = autoSheetName.Trim();
            c.StartRow = start;
            c.EndRow = end;
            c.NextRunRow = start;
            c.CurrentRow = null;
            c.LastCompletedRow = null;
            c.LastSku = null;
            c.RunnerPhase = null;
            c.RunnerRunning = null;
            c.LastRunnerMessage = null;
            c.RunLog.Clear();
            c.ProgressSyncedAt = null;

            entry.Session.ApplyConfig(c);
            RefreshListItem(c.Id);

            start = checked(end + 1);
        }

        if (_selected is not null)
            _detailPanel.RefreshProgressFromConfig();

        _statusStripLabel.Text =
            $"Da set row sheet \"{autoSheetName}\", row tu {Math.Max(2, autoStartRow)}, moi profile + {Math.Max(1, rowsPerProfile)}";
        AppendLog(_statusStripLabel.Text);
    }

    private void BrowseExe(TextBox target)
    {
        using var d = new OpenFileDialog { Filter = "brave.exe|brave.exe|*.exe|*.exe", Title = "Chọn brave.exe" };
        if (d.ShowDialog() == DialogResult.OK)
            target.Text = d.FileName;
    }

    private void BrowseUserData(TextBox target)
    {
        using var d = new FolderBrowserDialog { Description = "Chọn thư mục Brave User Data" };
        if (d.ShowDialog() == DialogResult.OK)
            target.Text = d.SelectedPath;
    }

    private void BrowseCookieFile(TextBox target)
    {
        using var d = new OpenFileDialog
        {
            Filter = "JSON cookie (*.json)|*.json|All files|*.*",
            Title = "Chọn file cookie BigSeller",
        };
        if (d.ShowDialog(this) == DialogResult.OK)
            target.Text = d.FileName;
    }

    private void OnInstanceSelected()
    {
        _detailPanel.FlushToConfig();

        if (_instanceList.SelectedItems.Count == 0)
        {
            _selected = null;
            _detailPanel.Bind(null, null, GetBraveExe, GetSourceUserData);
            return;
        }

        var id = _instanceList.SelectedItems[0].Tag as string;
        _selected = _entries.FirstOrDefault(e => e.Config.Id == id);
        if (_selected is null) return;

        var entry = _selected;
        _detailPanel.Bind(entry.Config, entry.Session, GetBraveExe, GetSourceUserData);
        if (entry.Session.IsRunning || _instanceLogTextBoxes.ContainsKey(entry.Config.Id))
            EnsureInstanceLogTab(entry.Config, selectTab: true);
        ApplyAutoRunUi();
        _ = entry.Session.SyncExtensionProgressAsync(silent: true).ContinueWith(
            t =>
            {
                if (t.IsCompletedSuccessfully && t.Result)
                {
                    BeginInvoke(() =>
                    {
                        if (_selected?.Config.Id == entry.Config.Id)
                            _detailPanel.RefreshProgressFromConfig();
                        RefreshListItem(entry.Config.Id);
                    });
                }
            },
            TaskScheduler.Default);
    }

    private void OnDetailConfigChanged()
    {
        if (_selected is null) return;
        RefreshListItem(_selected.Config.Id);
        PersistSettings();
    }

    private async void OnStopRunnerRequested()
    {
        if (_selected is null) return;
        _detailPanel.FlushToConfig();

        var entry = _selected;
        var c = entry.Config;

        if (!entry.Session.IsRunnerLoopActive)
        {
            MessageBox.Show(this,
                "Runner chưa chạy — bấm \"Chạy tiếp\" để bắt đầu xử lý dòng.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _statusStripLabel.Text = $"{c.DisplayName}: đang dừng runner…";
        AppendLog($"[{c.DisplayName}] Dừng chạy runner…");

        try
        {
            entry.Session.ApplyConfig(c);
            await entry.Session.StopRunnerAsync();
            _detailPanel.RefreshProgressFromConfig();
            RefreshListItem(c.Id);
            PersistSettings();
            var last = c.LastCompletedRow;
            var resume = c.SuggestedResumeRow;
            _statusStripLabel.Text = last is > 0
                ? $"{c.DisplayName}: đã dừng — xong dòng {last}, tiếp từ {resume}"
                : $"{c.DisplayName}: đã dừng chạy";
        }
        catch (Exception ex)
        {
            AppendLog($"[{c.DisplayName}] Dừng chạy lỗi: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Dừng chạy", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _statusStripLabel.Text = "Sẵn sàng";
        }
    }

    private async void OnRunRequested()
    {
        if (_selected is null) return;
        if (_settings.AutoRunEnabled)
        {
            MessageBox.Show(this,
                "Đang bật chế độ chạy tự động — dùng \"Chạy tự động\" ở phần Auto.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _detailPanel.FlushToConfig();

        var entry = _selected;
        var c = entry.Config;

        if (!entry.Session.IsRunning)
        {
            MessageBox.Show(this, "Bấm \"Mở profile\" trước.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(c.DataSheet) || c.StartRow is not > 0)
        {
            MessageBox.Show(this, "Nhập sheet và \"Từ dòng\" trước.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var runRow = c.GetEffectiveRunRow();
        if (runRow is not > 0)
        {
            MessageBox.Show(this, "Dòng tiếp theo không hợp lệ.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!c.TryValidateRunRow(runRow.Value, out var rangeErr))
        {
            MessageBox.Show(this, rangeErr ?? "Dòng tiếp theo ngoài phạm vi.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        c.NextRunRow = runRow;

        var brave = GetBraveExe();
        var userData = GetSourceUserData();
        if (string.IsNullOrWhiteSpace(brave) || string.IsNullOrWhiteSpace(userData))
        {
            MessageBox.Show(this, "Cấu hình Brave exe và User Data mẫu trong Cài đặt chung.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _statusStripLabel.Text = $"{c.DisplayName}: đang chạy từ dòng {runRow}…";
        AppendLog($"[{c.DisplayName}] Chạy sheet \"{c.DataSheet}\" từ dòng {runRow} (phạm vi {c.StartRow}–{c.EndRow?.ToString() ?? "hết"})…");
        AppendInstanceLog(c, $"Chạy sheet \"{c.DataSheet}\" từ dòng {runRow} (phạm vi {c.StartRow}–{c.EndRow?.ToString() ?? "hết"})…", selectTab: true);

        _detailPanel.SetBusy(true, "Đang chạy…");
        try
        {
            entry.Session.ApplyConfig(c);
            await RunManualWithExtensionRetryAsync(entry, brave, userData, preferSuggestedResume: true)
                .ConfigureAwait(true);
            _detailPanel.RefreshProgressFromConfig();
            RefreshListItem(c.Id);
            PersistSettings();
            AppendLog($"[{c.DisplayName}] Runner đã khởi động — theo dõi tab Brave và nhật ký.");
        }
        catch (ApiNotRunningException ex)
        {
            AppendLog($"[{c.DisplayName}] {ex.Message.Replace("\n", " ")}");
            var startApi = MessageBox.Show(
                this,
                ex.Message + "\n\nBạn có muốn mở API (python api\\main.py) ngay không?",
                "API chưa chạy (port 8012)",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (startApi == DialogResult.Yes)
                ApiServerHelper.TryStartApiInBackground(msg => AppendLog($"[{c.DisplayName}] {msg}"));
        }
        catch (Exception ex)
        {
            AppendLog($"[{c.DisplayName}] Chạy lỗi: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Chạy", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _detailPanel.SetBusy(false);
            _statusStripLabel.Text = "Sẵn sàng";
        }
    }

    private async void OnResumeContinueRequested()
    {
        if (_selected is null) return;
        if (_settings.AutoRunEnabled)
        {
            MessageBox.Show(this,
                "Đang bật chế độ chạy tự động — dùng \"Chạy tự động\" ở phần Auto.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _detailPanel.FlushToConfig();

        var entry = _selected;
        var c = entry.Config;
        var runRow = c.GetEffectiveRunRow();
        if (runRow is not > 0)
        {
            MessageBox.Show(this, "Dòng tiếp theo không hợp lệ.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!c.TryValidateRunRow(runRow.Value, out var rangeErr))
        {
            MessageBox.Show(this, rangeErr ?? "Dòng tiếp theo ngoài phạm vi.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        c.NextRunRow = runRow;

        PersistSettings();

        var brave = GetBraveExe();
        var userData = GetSourceUserData();
        if (string.IsNullOrWhiteSpace(brave) || string.IsNullOrWhiteSpace(userData))
        {
            MessageBox.Show(this, "Cấu hình Brave exe và User Data mẫu trong Cài đặt chung.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _statusStripLabel.Text = $"{c.DisplayName}: đang chạy tiếp từ dòng {runRow}…";
        AppendLog($"[{c.DisplayName}] Chạy tiếp từ dòng {runRow} (sheet \"{c.DataSheet}\")…");
        AppendInstanceLog(c, $"Chạy tiếp từ dòng {runRow} (sheet \"{c.DataSheet}\")…", selectTab: true);

        _detailPanel.SetBusy(true, "Đang chạy tiếp…");
        try
        {
            entry.Session.ApplyConfig(c);
            await RunManualWithExtensionRetryAsync(entry, brave, userData, preferSuggestedResume: true)
                .ConfigureAwait(true);
            _detailPanel.RefreshProgressFromConfig();
            RefreshListItem(c.Id);
            PersistSettings();
            AppendLog($"[{c.DisplayName}] Đã kích hoạt extension — theo dõi tab Brave và nhật ký.");
        }
        catch (ApiNotRunningException ex)
        {
            AppendLog($"[{c.DisplayName}] {ex.Message.Replace("\n", " ")}");
            var startApi = MessageBox.Show(
                this,
                ex.Message + "\n\nBạn có muốn mở API (python api\\main.py) ngay không?",
                "API chưa chạy (port 8012)",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (startApi == DialogResult.Yes)
                ApiServerHelper.TryStartApiInBackground(msg => AppendLog($"[{c.DisplayName}] {msg}"));
        }
        catch (Exception ex)
        {
            AppendLog($"[{c.DisplayName}] Chạy tiếp lỗi: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Chạy tiếp", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _detailPanel.SetBusy(false);
            _statusStripLabel.Text = "Sẵn sàng";
        }
    }

    private void SyncDetailToConfig()
    {
        // Detail panel syncs on control change; force selection refresh
        if (_selected is not null)
            _detailPanel.Bind(_selected.Config, _selected.Session, GetBraveExe, GetSourceUserData);
    }

    private async Task RunManualWithExtensionRetryAsync(
        InstanceEntry entry,
        string brave,
        string userData,
        bool preferSuggestedResume)
    {
        await entry.Session.ResumeContinueAsync(
                brave,
                userData,
                preferSuggestedResume,
                retryExtensionStart: true)
            .ConfigureAwait(true);
    }

    private RichTextBox EnsureInstanceLogTab(InstanceConfig config, bool selectTab = false)
    {
        if (_instanceLogTextBoxes.TryGetValue(config.Id, out var existing) && !existing.IsDisposed)
        {
            var page = existing.Parent as TabPage;
            if (page is not null)
            {
                page.Text = config.DisplayName;
                if (selectTab)
                    _logTabs.SelectedTab = page;
            }
            return existing;
        }

        var textBox = CreateLogTextBox();
        var tabPage = new TabPage(config.DisplayName)
        {
            Tag = config.Id,
        };
        tabPage.Controls.Add(textBox);
        _logTabs.TabPages.Add(tabPage);
        _instanceLogTextBoxes[config.Id] = textBox;
        if (selectTab)
            _logTabs.SelectedTab = tabPage;
        return textBox;
    }

    private void AppendInstanceLog(InstanceConfig config, string message, bool selectTab = false)
    {
        if (!_instanceLogTextBoxes.ContainsKey(config.Id) && !selectTab)
            return;
        AppendLogToBox(EnsureInstanceLogTab(config, selectTab), message, includePrefix: false);
    }

    private void CloseInstanceLogTab(InstanceConfig config)
    {
        if (!_instanceLogTextBoxes.Remove(config.Id, out var textBox))
            return;

        if (textBox.Parent is TabPage page)
            _logTabs.TabPages.Remove(page);
        textBox.Dispose();
    }

    private static RichTextBox CreateLogTextBox() =>
        new()
        {
            ReadOnly = true,
            Dock = DockStyle.Fill,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            WordWrap = true,
            Font = new Font("Segoe UI", 9f),
            BackColor = Color.FromArgb(22, 22, 26),
            ForeColor = Color.FromArgb(215, 215, 220),
            BorderStyle = BorderStyle.None,
        };

    private void RefreshListItem(string id)
    {
        var entry = _entries.FirstOrDefault(e => e.Config.Id == id);
        if (entry is null) return;

        var visible = IsVisibleInActiveShop(entry);
        foreach (ListViewItem item in _instanceList.Items)
        {
            if (item.Tag as string != id) continue;
            if (!visible)
            {
                _instanceList.Items.Remove(item);
                return;
            }
            item.Text = entry.Config.DisplayName;
            item.SubItems[1].Text = entry.Session.StatusText;
            item.SubItems[2].Text = TruncateProgress(entry.Config.ProgressSummary);
            item.SubItems[3].Text = TruncateProxy(entry.Session.ProxySummary);
            return;
        }

        if (visible)
            _instanceList.Items.Add(CreateListItem(entry));
    }

    private static string TruncateProxy(string proxy)
    {
        if (string.IsNullOrWhiteSpace(proxy)) return "";
        return proxy.Length <= 40 ? proxy : proxy[..37] + "...";
    }

    private static string TruncateProgress(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        return text.Length <= 36 ? text : text[..33] + "...";
    }

    private void PersistSettings()
    {
        _settings.Instances = _entries.Select(e => e.Config).ToList();
        _settings.MaxConcurrentProfiles = Math.Clamp(_settings.MaxConcurrentProfiles, 1, 50);
        try
        {
            LauncherSettings.Save(_settings);
        }
        catch (Exception ex)
        {
            AppendLog($"Lưu cấu hình lỗi: {ex.Message}");
        }
    }

    private string GetBraveExe() => _settings.BraveExe.Trim();
    private string GetSourceUserData() => _settings.SourceUserData.Trim();

    private void AppendLog(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLog(message));
            return;
        }

        var time = DateTime.Now.ToString("HH:mm:ss");
        _logTextBox.SelectionStart = _logTextBox.TextLength;
        _logTextBox.SelectionLength = 0;
        _logTextBox.SelectionColor = Color.FromArgb(110, 110, 120);
        _logTextBox.AppendText($"{time} ");

        var (prefix, body) = SplitInstancePrefix(message);
        if (prefix is not null)
        {
            _logTextBox.SelectionColor = Color.FromArgb(100, 180, 255);
            _logTextBox.AppendText(prefix);
            _logTextBox.SelectionColor = ClassifyLogLineColor(body);
            _logTextBox.AppendText(body);
        }
        else
        {
            _logTextBox.SelectionColor = ClassifyLogLineColor(message);
            _logTextBox.AppendText(message);
        }

        _logTextBox.AppendText(Environment.NewLine);
        _logTextBox.SelectionStart = _logTextBox.TextLength;
        _logTextBox.ScrollToCaret();

        if (_logTextBox.Lines.Length > 600)
        {
            _logTextBox.Select(0, _logTextBox.GetFirstCharIndexFromLine(100));
            _logTextBox.SelectedText = "";
        }
    }

    private static void AppendLogToBox(RichTextBox target, string message, bool includePrefix)
    {
        var time = DateTime.Now.ToString("HH:mm:ss");
        target.SelectionStart = target.TextLength;
        target.SelectionLength = 0;
        target.SelectionColor = Color.FromArgb(110, 110, 120);
        target.AppendText($"{time} ");

        var (prefix, body) = includePrefix ? SplitInstancePrefix(message) : (null, message);
        if (prefix is not null)
        {
            target.SelectionColor = Color.FromArgb(100, 180, 255);
            target.AppendText(prefix);
            target.SelectionColor = ClassifyLogLineColor(body);
            target.AppendText(body);
        }
        else
        {
            target.SelectionColor = ClassifyLogLineColor(message);
            target.AppendText(message);
        }

        target.AppendText(Environment.NewLine);
        target.SelectionStart = target.TextLength;
        target.ScrollToCaret();

        if (target.Lines.Length > 800)
        {
            target.Select(0, target.GetFirstCharIndexFromLine(150));
            target.SelectedText = "";
        }
    }

    private static (string? prefix, string body) SplitInstancePrefix(string message)
    {
        if (!message.StartsWith('[')) return (null, message);
        var close = message.IndexOf(']');
        if (close <= 0 || close > 40) return (null, message);
        var prefix = message[..(close + 1)] + " ";
        return (prefix, message[(close + 1)..].TrimStart());
    }

    private static Color ClassifyLogLineColor(string text)
    {
        var t = text.ToLowerInvariant();
        if (t.Contains("lỗi") || t.Contains("error") || t.Contains("proxy") && t.Contains("không"))
            return Color.FromArgb(255, 120, 120);
        if (t.Contains("đã tải") || t.Contains("click scrape") || t.Contains("hoàn tất") || t.Contains("sẵn sàng"))
            return Color.FromArgb(120, 220, 140);
        if (t.Contains("chờ") || t.Contains("đang ") || t.Contains("kiểm tra"))
            return Color.FromArgb(200, 200, 130);
        return Color.FromArgb(215, 215, 220);
    }

    private static FileInfo? DetectBraveExe()
    {
        var candidates = new List<string>();
        foreach (var folder in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        })
        {
            if (!string.IsNullOrWhiteSpace(folder))
                candidates.Add(Path.Combine(folder, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"));
        }

        foreach (var (root, sub) in new[]
        {
            (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\App Paths\brave.exe"),
            (Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\App Paths\brave.exe"),
        })
        {
            try
            {
                using var key = root.OpenSubKey(sub);
                var val = key?.GetValue(string.Empty)?.ToString();
                if (!string.IsNullOrWhiteSpace(val)) candidates.Add(val);
            }
            catch { }
        }

        foreach (var c in candidates)
            if (File.Exists(c)) return new FileInfo(c);
        return null;
    }

    private static DirectoryInfo? DetectBraveUserData()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local)) return null;
        var path = Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data");
        return Directory.Exists(path) ? new DirectoryInfo(path) : null;
    }

    private sealed record RunnerConfigSnapshot(
        string DataSheet,
        int? StartRow,
        int? EndRow,
        int? NextRunRow,
        int? CurrentRow,
        int? LastCompletedRow,
        string? LastSku,
        string? RunnerPhase,
        bool? RunnerRunning,
        string? LastRunnerMessage,
        List<RunnerLogEntry> RunLog,
        DateTimeOffset? ProgressSyncedAt)
    {
        public bool HasMeaningfulConfig =>
            !string.IsNullOrWhiteSpace(DataSheet) ||
            StartRow is > 0 ||
            EndRow is > 0 ||
            NextRunRow is > 0 ||
            CurrentRow is > 0 ||
            LastCompletedRow is > 0 ||
            RunLog.Count > 0;
    }

    private sealed class InstanceEntry(InstanceConfig config, BraveInstanceSession session, int cdpPort)
    {
        public InstanceConfig Config { get; } = config;
        public BraveInstanceSession Session { get; } = session;
        public int CdpPort { get; } = cdpPort;
    }
}
