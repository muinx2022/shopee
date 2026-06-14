namespace OpenMultiBraveLauncherV3;

internal sealed class BigSellerWorkflowForm : Form
{
    private readonly BigSellerWorkflowPanel _panel;

    public BigSellerWorkflowForm(
        BigSellerWorkflowSettings initialSettings,
        ShopConfig shop,
        Action<ShopConfig> saveShop)
    {
        Text = "BigSeller Tools";
        StartPosition = FormStartPosition.CenterParent;
        Width = 1020;
        Height = 800;
        MinimumSize = new Size(820, 620);

        _panel = new BigSellerWorkflowPanel(initialSettings, shop, saveShop)
        {
            Dock = DockStyle.Fill,
        };
        Controls.Add(_panel);

        FormClosing += async (_, _) => await _panel.ShutdownAsync().ConfigureAwait(true);
    }
}

internal sealed class BigSellerWorkflowPanel : UserControl
{
    private readonly BigSellerWorkflowSettings _initialSettings;
    private readonly ShopConfig _shop;
    private readonly Action<ShopConfig> _saveShop;

    private readonly ActionTab _importTab;
    private readonly ActionTab _updateTab;
    private readonly ActionTab _nameTab;

    public BigSellerWorkflowPanel(
        BigSellerWorkflowSettings initialSettings,
        ShopConfig shop,
        Action<ShopConfig> saveShop)
    {
        _initialSettings = initialSettings;
        _shop = shop;
        _saveShop = saveShop;

        MinimumSize = new Size(820, 620);

        _importTab = BuildImportTab();
        _updateTab = BuildUpdateTab();
        _nameTab = BuildNameTab();

        var tabControl = new TabControl { Dock = DockStyle.Fill };
        tabControl.TabPages.Add(_importTab.Page);
        tabControl.TabPages.Add(_updateTab.Page);
        tabControl.TabPages.Add(_nameTab.Page);
        // 3 tab BigSeller: sub-tab — nhỏ, xám nhạt, phụ thuộc tab chính.
        TabStyler.ApplySub(tabControl);
        Controls.Add(tabControl);

        Disposed += (_, _) =>
        {
            if (!IsDisposed)
                return;

            StopTab(_importTab, logStop: false);
            StopTab(_updateTab, logStop: false);
            StopTab(_nameTab, logStop: false);
            _nameTab.NameRewriteRunner.Stop();
            SaveTabFields(_importTab);
            SaveTabFields(_updateTab);
            SaveTabFields(_nameTab);
            _ = DisposeImportRunnerAsync(_importTab);
        };
    }

    public async Task ShutdownAsync()
    {
        StopTab(_importTab, logStop: false);
        StopTab(_updateTab, logStop: false);
        StopTab(_nameTab, logStop: false);
        _nameTab.NameRewriteRunner.Stop();
        SaveTabFields(_importTab);
        SaveTabFields(_updateTab);
        SaveTabFields(_nameTab);
        await DisposeImportRunnerAsync(_importTab).ConfigureAwait(true);
    }

    private enum ActionKind
    {
        Import,
        UpdateProduct,
        UpdateName,
    }

    private sealed class ActionTab
    {
        public required ActionKind Kind { get; init; }
        public required TabPage Page { get; init; }
        public required RichTextBox LogBox { get; init; }
        public required Button RunBtn { get; init; }
        public required Button StopBtn { get; init; }
        public required Button PauseBtn { get; init; }

        public TextBox? CrawlUrlBox { get; set; }
        public CheckBox? ClaimedTabBox { get; set; }
        public TextBox? ImageBox { get; set; }
        public TextBox? VideoBox { get; set; }
        public NumericUpDown? ReloadInput { get; set; }
        public NumericUpDown? StartRowInput { get; set; }
        public NumericUpDown? EndRowInput { get; set; }
        public TextBox? ModelBox { get; set; }
        public TextBox? ApiKeyFileBox { get; set; }
        public NumericUpDown? BatchInput { get; set; }

        public WorkflowPauseToken PauseToken { get; } = new();
        public BigSellerPythonRunner PythonRunner { get; } = new();
        public ProductNameRewriteRunner NameRewriteRunner { get; } = new();
        public BigSellerImportToStoreRunner? ImportRunner { get; set; }
        public CancellationTokenSource? ImportCts { get; set; }
        public bool IsMarkedRunning { get; set; }

        public bool IsRunning => Kind switch
        {
            ActionKind.Import => ImportRunner is not null,
            ActionKind.UpdateName => NameRewriteRunner.IsRunning,
            _ => PythonRunner.IsRunning,
        };
    }

    public bool HasRunningWork =>
        _importTab.IsMarkedRunning ||
        _updateTab.IsMarkedRunning ||
        _nameTab.IsMarkedRunning;

    public event Action? RunningStateChanged;

    private ActionTab BuildImportTab()
    {
        var crawlUrlBox = new TextBox
        {
            Text = ResolveCrawlUrl(_initialSettings.CrawlUrl),
            Dock = DockStyle.Fill,
        };
        var claimedTabBox = new CheckBox
        {
            Text = "Nhap tu tab Da nhan",
            AutoSize = true,
            Checked = _initialSettings.ImportFromClaimedTab,
            Margin = new Padding(0, 4, 0, 0),
        };

        var tab = CreateActionTab(
            ActionKind.Import,
            "Import to store",
            BuildImportContextLabel(),
            BuildImportSettings(crawlUrlBox, claimedTabBox));

        tab.CrawlUrlBox = crawlUrlBox;
        tab.ClaimedTabBox = claimedTabBox;
        tab.RunBtn.Click += async (_, _) => await StartImportAsync(tab);
        return tab;
    }

    private ActionTab BuildUpdateTab()
    {
        var imageBox = new TextBox { Text = _initialSettings.ImagePath, Dock = DockStyle.Fill };
        var videoBox = new TextBox { Text = _initialSettings.VideoFolder, Dock = DockStyle.Fill };
        var reloadInput = new NumericUpDown
        {
            Minimum = 3,
            Maximum = 600,
            Value = Math.Clamp(_initialSettings.ListingReloadSeconds, 3, 600),
            Width = 90,
        };
        var startRowInput = new NumericUpDown
        {
            Minimum = 2,
            Maximum = 1_000_000,
            Value = Math.Max(2, _initialSettings.StartRow),
            Width = 90,
        };
        var endRowInput = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 1_000_000,
            Value = Math.Max(0, _initialSettings.EndRow),
            Width = 90,
        };
        var modelBox = new TextBox { Text = _initialSettings.OpenAiModel, Dock = DockStyle.Fill };
        var apiKeyFileBox = new TextBox { Text = _initialSettings.OpenAiApiKeyFile, Dock = DockStyle.Fill };

        var tab = CreateActionTab(
            ActionKind.UpdateProduct,
            "Update product bigseller",
            BuildContextLabel(),
            BuildUpdateSettings(imageBox, videoBox, reloadInput, startRowInput, endRowInput, modelBox, apiKeyFileBox));

        tab.ImageBox = imageBox;
        tab.VideoBox = videoBox;
        tab.ReloadInput = reloadInput;
        tab.StartRowInput = startRowInput;
        tab.EndRowInput = endRowInput;
        tab.ModelBox = modelBox;
        tab.ApiKeyFileBox = apiKeyFileBox;
        tab.RunBtn.Click += (_, _) => StartPython(tab);
        return tab;
    }

    private ActionTab BuildNameTab()
    {
        var startRowInput = new NumericUpDown
        {
            Minimum = 2,
            Maximum = 1_000_000,
            Value = Math.Max(2, _initialSettings.StartRow),
            Width = 90,
        };
        var endRowInput = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 1_000_000,
            Value = Math.Max(0, _initialSettings.EndRow),
            Width = 90,
        };
        var modelBox = new TextBox { Text = _initialSettings.OpenAiModel, Dock = DockStyle.Fill };
        var apiKeyFileBox = new TextBox { Text = _initialSettings.OpenAiApiKeyFile, Dock = DockStyle.Fill };
        var batchInput = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 500,
            Value = Math.Clamp(_initialSettings.OpenAiBatchSize, 1, 500),
            Width = 90,
        };

        var tab = CreateActionTab(
            ActionKind.UpdateName,
            "Update product name",
            BuildNameContextLabel(),
            BuildNameSettings(startRowInput, endRowInput, modelBox, apiKeyFileBox, batchInput));

        tab.StartRowInput = startRowInput;
        tab.EndRowInput = endRowInput;
        tab.ModelBox = modelBox;
        tab.ApiKeyFileBox = apiKeyFileBox;
        tab.BatchInput = batchInput;
        tab.RunBtn.Text = "Rewrite ten";
        tab.RunBtn.Click += (_, _) => StartNameRewrite(tab);
        return tab;
    }

    private ActionTab CreateActionTab(ActionKind kind, string title, Label contextLabel, Control settingsGroup)
    {
        var runBtn = new Button { Text = "Run", AutoSize = true, Padding = new Padding(8, 5, 8, 5) };
        var stopBtn = new Button { Text = "Stop", Enabled = false, AutoSize = true, Padding = new Padding(8, 5, 8, 5) };
        var pauseBtn = new Button { Text = "Pause", Enabled = false, AutoSize = true, Padding = new Padding(8, 5, 8, 5) };
        var logBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            WordWrap = false,
            ScrollBars = RichTextBoxScrollBars.Both,
            Font = new Font("Consolas", 9f),
            BackColor = Color.FromArgb(18, 18, 18),
            ForeColor = Color.Gainsboro,
        };

        var tab = new ActionTab
        {
            Kind = kind,
            Page = new TabPage(title),
            RunBtn = runBtn,
            StopBtn = stopBtn,
            PauseBtn = pauseBtn,
            LogBox = logBox,
        };

        stopBtn.Click += (_, _) => StopTab(tab);
        pauseBtn.Click += (_, _) => TogglePause(tab);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            Margin = new Padding(0, 8, 0, 0),
        };
        buttons.Controls.AddRange([runBtn, stopBtn, pauseBtn]);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(8),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(contextLabel, 0, 0);
        root.Controls.Add(settingsGroup, 0, 1);
        root.Controls.Add(buttons, 0, 2);
        root.Controls.Add(logBox, 0, 3);
        tab.Page.Controls.Add(root);
        return tab;
    }

    private Label BuildNameContextLabel() => new()
    {
        Text =
            $"Shop: {_initialSettings.ShopName}  |  Sheet: {_initialSettings.DataSheet}\r\n" +
            $"Workbook: {_initialSettings.WorkbookPath}\r\n" +
            "Rewrite ten SP trong XLSX (OpenAI) — ghi vao cot \"Ten sp da sua\" (C#).",
        AutoSize = true,
        Margin = new Padding(0, 0, 0, 4),
        ForeColor = Color.DimGray,
        MaximumSize = new Size(960, 0),
    };

    private Label BuildImportContextLabel() => new()
    {
        Text =
            $"Shop: {_initialSettings.ShopName}  |  Sheet: {_initialSettings.DataSheet}\r\n" +
            $"Cookie: {_initialSettings.BigSellerCookieFile}\r\n" +
            $"Brave Import (rieng): {ResolveImportProfileDir()}  |  CDP: {ResolveImportDebugPort()}",
        AutoSize = true,
        Margin = new Padding(0, 0, 0, 4),
        ForeColor = Color.DimGray,
        MaximumSize = new Size(960, 0),
    };

    private Label BuildContextLabel() => new()
    {
        Text =
            $"Shop: {_initialSettings.ShopName}  |  Sheet: {_initialSettings.DataSheet}\r\n" +
            $"Cookie: {_initialSettings.BigSellerCookieFile}\r\n" +
            $"Brave Update (rieng): {_initialSettings.ProfileDir}  |  CDP: {_initialSettings.DebugPort}",
        AutoSize = true,
        Margin = new Padding(0, 0, 0, 4),
        ForeColor = Color.DimGray,
        MaximumSize = new Size(960, 0),
    };

    private GroupBox BuildImportSettings(TextBox crawlUrlBox, CheckBox claimedTabBox)
    {
        var group = new GroupBox
        {
            Text = "Cau hinh import",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(8),
        };

        var grid = CreateFieldGrid(3);
        grid.Controls.Add(MakeLabel("Crawl URL"), 0, 0);
        grid.Controls.Add(crawlUrlBox, 1, 0);
        grid.SetColumnSpan(crawlUrlBox, 3);
        grid.Controls.Add(claimedTabBox, 1, 1);
        grid.SetColumnSpan(claimedTabBox, 3);
        group.Controls.Add(grid);
        return group;
    }

    private GroupBox BuildUpdateSettings(
        TextBox imageBox,
        TextBox videoBox,
        NumericUpDown reloadInput,
        NumericUpDown startRowInput,
        NumericUpDown endRowInput,
        TextBox modelBox,
        TextBox apiKeyFileBox)
    {
        var group = new GroupBox
        {
            Text = "Cau hinh update product",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(8),
        };

        var grid = CreateFieldGrid(4);
        AddRow(grid, 0, "Anh", WrapBrowse(imageBox, "Image files|*.jpg;*.jpeg;*.png;*.webp|All|*.*"),
            "Video", WrapBrowse(videoBox, "All|*.*", folder: true));
        AddRow(grid, 1, "Tu dong", startRowInput, "Den dong (0=het)", endRowInput);
        AddRow(grid, 2, "Reload (s)", reloadInput, "Model API", modelBox);
        grid.Controls.Add(MakeLabel("API key file"), 0, 3);
        var keyPanel = WrapBrowse(apiKeyFileBox, "Key/Text|*.key;*.txt|All|*.*");
        grid.Controls.Add(keyPanel, 1, 3);
        grid.SetColumnSpan(keyPanel, 3);
        group.Controls.Add(grid);
        return group;
    }

    private GroupBox BuildNameSettings(
        NumericUpDown startRowInput,
        NumericUpDown endRowInput,
        TextBox modelBox,
        TextBox apiKeyFileBox,
        NumericUpDown batchInput)
    {
        var group = new GroupBox
        {
            Text = "Cau hinh update name",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(8),
        };

        var grid = CreateFieldGrid(3);
        AddRow(grid, 0, "Tu dong", startRowInput, "Den dong (0=het)", endRowInput);
        AddRow(grid, 1, "AI model", modelBox, "Batch", batchInput);
        grid.Controls.Add(MakeLabel("API key file"), 0, 2);
        var keyPanel = WrapBrowse(apiKeyFileBox, "Key/Text|*.key;*.txt|All|*.*");
        grid.Controls.Add(keyPanel, 1, 2);
        grid.SetColumnSpan(keyPanel, 3);
        group.Controls.Add(grid);
        return group;
    }

    private static TableLayoutPanel CreateFieldGrid(int rowCount)
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 4,
            RowCount = rowCount,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        return grid;
    }

    private static void AddRow(TableLayoutPanel grid, int row,
        string lbl1, Control ctrl1, string lbl2, Control ctrl2)
    {
        grid.Controls.Add(MakeLabel(lbl1), 0, row);
        grid.Controls.Add(ctrl1, 1, row);
        grid.Controls.Add(MakeLabel(lbl2), 2, row);
        grid.Controls.Add(ctrl2, 3, row);
    }

    private static Label MakeLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(0, 4, 0, 0),
    };

    private Control WrapBrowse(TextBox textBox, string filter, bool folder = false)
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = true,
            Margin = new Padding(0, 2, 4, 2),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        textBox.Dock = DockStyle.Fill;
        textBox.Margin = Padding.Empty;

        var btn = new Button { Text = "...", Dock = DockStyle.Fill, Margin = new Padding(4, 0, 0, 0) };
        btn.Click += (_, _) =>
        {
            if (folder)
            {
                using var d = new FolderBrowserDialog();
                if (d.ShowDialog(this) == DialogResult.OK)
                    textBox.Text = d.SelectedPath;
                return;
            }

            using var f = new OpenFileDialog { Filter = filter };
            if (f.ShowDialog(this) == DialogResult.OK)
                textBox.Text = f.FileName;
        };
        layout.Controls.Add(textBox, 0, 0);
        layout.Controls.Add(btn, 1, 0);
        return layout;
    }

    private string ResolveImportProfileDir() =>
        string.IsNullOrWhiteSpace(_initialSettings.ImportProfileDir)
            ? _initialSettings.ProfileDir
            : _initialSettings.ImportProfileDir;

    private int ResolveImportDebugPort() =>
        _initialSettings.ImportDebugPort > 0
            ? _initialSettings.ImportDebugPort
            : _initialSettings.DebugPort;

    private BigSellerWorkflowSettings BuildSettings(ActionTab tab) => new()
    {
        BravePath = _initialSettings.BravePath,
        ProfileDir = tab.Kind == ActionKind.Import ? ResolveImportProfileDir() : _initialSettings.ProfileDir,
        DebugPort = tab.Kind == ActionKind.Import ? ResolveImportDebugPort() : _initialSettings.DebugPort,
        ImportProfileDir = ResolveImportProfileDir(),
        ImportDebugPort = ResolveImportDebugPort(),
        ShopName = _initialSettings.ShopName,
        WorkbookPath = _initialSettings.WorkbookPath,
        DataSheet = _initialSettings.DataSheet,
        BigSellerCookieFile = _initialSettings.BigSellerCookieFile,
        StartRow = tab.StartRowInput is not null ? (int)tab.StartRowInput.Value : _initialSettings.StartRow,
        EndRow = tab.EndRowInput is not null ? (int)tab.EndRowInput.Value : _initialSettings.EndRow,
        PythonDir = _initialSettings.PythonDir,
        PythonExe = _initialSettings.PythonExe,
        ImagePath = tab.ImageBox?.Text.Trim() ?? _initialSettings.ImagePath,
        VideoFolder = tab.VideoBox?.Text.Trim() ?? _initialSettings.VideoFolder,
        CrawlUrl = ResolveCrawlUrl(tab.CrawlUrlBox?.Text ?? _initialSettings.CrawlUrl),
        ImportFromClaimedTab = tab.ClaimedTabBox?.Checked ?? _initialSettings.ImportFromClaimedTab,
        ListingReloadSeconds = tab.ReloadInput is not null
            ? (int)tab.ReloadInput.Value
            : _initialSettings.ListingReloadSeconds,
        OpenAiModel = tab.ModelBox?.Text.Trim() ?? _initialSettings.OpenAiModel,
        OpenAiApiKeyFile = tab.ApiKeyFileBox?.Text.Trim() ?? _initialSettings.OpenAiApiKeyFile,
        OpenAiBatchSize = tab.BatchInput is not null
            ? (int)tab.BatchInput.Value
            : _initialSettings.OpenAiBatchSize,
    };

    private void SaveTabFields(ActionTab tab)
    {
        switch (tab.Kind)
        {
            case ActionKind.Import:
                if (tab.CrawlUrlBox is not null)
                    _shop.BigSellerCrawlUrl = ResolveCrawlUrl(tab.CrawlUrlBox.Text);
                if (tab.ClaimedTabBox is not null)
                    _shop.BigSellerImportFromClaimedTab = tab.ClaimedTabBox.Checked;
                break;

            case ActionKind.UpdateProduct:
                if (tab.ImageBox is not null)
                    _shop.BigSellerImagePath = tab.ImageBox.Text.Trim();
                if (tab.VideoBox is not null)
                    _shop.BigSellerVideoFolder = tab.VideoBox.Text.Trim();
                if (tab.ReloadInput is not null)
                    _shop.BigSellerListingReloadSeconds = (int)tab.ReloadInput.Value;
                if (tab.StartRowInput is not null)
                    _shop.BigSellerStartRow = (int)tab.StartRowInput.Value;
                if (tab.EndRowInput is not null)
                    _shop.BigSellerEndRow = (int)tab.EndRowInput.Value;
                if (tab.ModelBox is not null)
                    _shop.OpenAiModel = tab.ModelBox.Text.Trim();
                if (tab.ApiKeyFileBox is not null)
                    _shop.OpenAiApiKeyFile = tab.ApiKeyFileBox.Text.Trim();
                break;

            case ActionKind.UpdateName:
                if (tab.StartRowInput is not null)
                    _shop.BigSellerStartRow = (int)tab.StartRowInput.Value;
                if (tab.EndRowInput is not null)
                    _shop.BigSellerEndRow = (int)tab.EndRowInput.Value;
                if (tab.ModelBox is not null)
                    _shop.OpenAiModel = tab.ModelBox.Text.Trim();
                if (tab.ApiKeyFileBox is not null)
                    _shop.OpenAiApiKeyFile = tab.ApiKeyFileBox.Text.Trim();
                if (tab.BatchInput is not null)
                    _shop.OpenAiBatchSize = (int)tab.BatchInput.Value;
                break;
        }

        _saveShop(_shop);
    }

    private async Task StartImportAsync(ActionTab tab)
    {
        if (tab.IsRunning)
        {
            MessageBox.Show(this, "Import dang chay.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        SaveTabFields(tab);
        SetTabRunning(tab, true);
        tab.PauseToken.Resume();
        tab.ImportCts = new CancellationTokenSource();
        tab.ImportRunner = new BigSellerImportToStoreRunner(
            BuildSettings(tab),
            msg => TabLog(tab, msg),
            tab.PauseToken);

        try
        {
            await tab.ImportRunner.RunAsync(tab.ImportCts.Token);
        }
        catch (OperationCanceledException)
        {
            TabLog(tab, "Da dung Import to store.");
        }
        catch (Exception ex)
        {
            if (tab.ImportCts?.IsCancellationRequested == true ||
                ex is ObjectDisposedException ||
                ex.Message.Contains("SemaphoreSlim", StringComparison.OrdinalIgnoreCase))
            {
                TabLog(tab, "Da dung Import to store.");
                return;
            }

            TabLog(tab, $"Import to store loi: {ex.Message}");
            if (!IsDisposed && IsHandleCreated)
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            await DisposeImportRunnerAsync(tab);
            if (!IsDisposed && IsHandleCreated)
                SetTabRunning(tab, false);
        }
    }

    private void StartPython(ActionTab tab)
    {
        if (tab.IsRunning)
        {
            MessageBox.Show(this, "Workflow dang chay.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        SaveTabFields(tab);
        SetTabRunning(tab, true);
        tab.PauseToken.Resume();

        try
        {
            tab.PythonRunner.Start(
                BuildSettings(tab),
                nameOnly: false,
                msg => TabLog(tab, msg),
                () => SafeBeginInvoke(() => SetTabRunning(tab, false)));
        }
        catch (Exception ex)
        {
            SetTabRunning(tab, false);
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void StartNameRewrite(ActionTab tab)
    {
        if (tab.IsRunning)
        {
            MessageBox.Show(this, "Rewrite ten dang chay.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        SaveTabFields(tab);
        SetTabRunning(tab, true, enablePause: false);

        try
        {
            tab.NameRewriteRunner.Start(
                BuildSettings(tab),
                msg => TabLog(tab, msg),
                () => SafeBeginInvoke(() => SetTabRunning(tab, false, enablePause: false)));
        }
        catch (Exception ex)
        {
            SetTabRunning(tab, false, enablePause: false);
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void StopTab(ActionTab tab, bool logStop = true)
    {
        if (tab.Kind == ActionKind.Import)
        {
            tab.ImportCts?.Cancel();
            if (tab.ImportRunner is not null && logStop)
                TabLog(tab, "Dang dung Import to store...");
            tab.PauseToken.Resume();
            return;
        }

        if (tab.Kind == ActionKind.UpdateName)
        {
            if (logStop)
                tab.NameRewriteRunner.Stop(msg => TabLog(tab, msg));
            else
                tab.NameRewriteRunner.Stop();
            return;
        }

        if (logStop)
            tab.PythonRunner.Stop(msg => TabLog(tab, msg));
        else
            tab.PythonRunner.Stop();

        tab.PauseToken.Resume();
    }

    private void TogglePause(ActionTab tab)
    {
        if (!tab.IsRunning)
            return;

        if (tab.Kind == ActionKind.UpdateName)
            return;

        if (tab.Kind == ActionKind.Import)
        {
            if (tab.PauseToken.IsPaused)
            {
                tab.PauseToken.Resume();
                tab.PauseBtn.Text = "Pause";
                TabLog(tab, "Tiep tuc Import to store.");
            }
            else
            {
                tab.PauseToken.Pause();
                tab.PauseBtn.Text = "Resume";
                TabLog(tab, "Tam dung Import to store.");
            }
            return;
        }

        if (tab.PythonRunner.IsPaused)
        {
            tab.PythonRunner.Resume(msg => TabLog(tab, msg));
            tab.PauseBtn.Text = "Pause";
        }
        else
        {
            tab.PythonRunner.Pause(msg => TabLog(tab, msg));
            tab.PauseBtn.Text = "Resume";
        }
    }

    private void SetTabRunning(ActionTab tab, bool running, bool enablePause = true)
    {
        var changed = tab.IsMarkedRunning != running;
        tab.IsMarkedRunning = running;
        tab.RunBtn.Enabled = !running;
        tab.StopBtn.Enabled = running;
        tab.PauseBtn.Enabled = running && enablePause;
        if (!running)
        {
            tab.PauseBtn.Text = "Pause";
            tab.PauseToken.Resume();
        }

        if (changed)
            RunningStateChanged?.Invoke();
    }

    private static async Task DisposeImportRunnerAsync(ActionTab tab)
    {
        var runner = tab.ImportRunner;
        tab.ImportRunner = null;

        if (runner is not null)
        {
            try { await runner.DisposeAsync(); } catch { }
        }

        try { tab.ImportCts?.Dispose(); } catch { }
        tab.ImportCts = null;
    }

    private void TabLog(ActionTab tab, string message)
    {
        if (IsDisposed || Disposing || tab.LogBox.IsDisposed)
            return;

        if (InvokeRequired)
        {
            SafeBeginInvoke(() => TabLog(tab, message));
            return;
        }

        if (IsDisposed || Disposing || tab.LogBox.IsDisposed)
            return;

        tab.LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        tab.LogBox.SelectionStart = tab.LogBox.TextLength;
        tab.LogBox.ScrollToCaret();
    }

    private void SafeBeginInvoke(Action action)
    {
        if (IsDisposed || Disposing || !IsHandleCreated)
            return;

        try { BeginInvoke(action); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
    }

    private static string ResolveCrawlUrl(string? url) =>
        string.IsNullOrWhiteSpace(url) ? BigSellerCrawlHelper.CrawlUrl : url.Trim();
}
