namespace UpdateProduct;

internal enum BigSellerActionKind
{
    Import,
    UpdateProduct,
    UpdateName,
}

internal sealed class BigSellerActionView : UserControl, IAsyncShutdown
{
    private readonly UpdateProductSettingsFile _settingsFile;
    private readonly BigSellerActionKind _kind;
    private readonly Action _onChanged;
    private readonly RichTextBox _logBox;
    private readonly Button _runButton;
    private readonly Button _stopButton;
    private readonly Button _pauseButton;
    private readonly Label _contextLabel;
    private readonly WorkflowPauseToken _pauseToken = new();
    private readonly BigSellerPythonRunner _pythonRunner = new();
    private readonly ProductNameRewriteRunner _nameRunner = new();
    private readonly object _importRunnerLock = new();
    private readonly object _updateRunnerLock = new();

    private TextBox? _crawlUrlBox;
    private ComboBox? _accountBox;
    private ComboBox? _shopBox;
    private CheckBox? _claimedTabBox;
    private NumericUpDown? _maxProcessInput;
    private TextBox? _imageBox;
    private TextBox? _videoBox;
    private NumericUpDown? _reloadInput;
    private NumericUpDown? _startRowInput;
    private NumericUpDown? _endRowInput;
    private TextBox? _modelBox;
    private NumericUpDown? _batchInput;
    private readonly List<BigSellerImportToStoreRunner> _importRunners = [];
    private readonly List<BigSellerPythonRunner> _updateRunners = [];
    private CancellationTokenSource? _importCts;
    private CancellationTokenSource? _updateCts;
    private BigSellerWorkflowSettings? _lastUpdateWorkflow;
    private bool _markedRunning;

    public BigSellerActionView(UpdateProductSettingsFile settingsFile, BigSellerActionKind kind, Action onChanged)
    {
        _settingsFile = settingsFile;
        _kind = kind;
        _onChanged = onChanged;

        _contextLabel = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            ForeColor = Color.DimGray,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _runButton = new Button { Text = kind == BigSellerActionKind.UpdateName ? "Rewrite ten" : "Run", AutoSize = true, Padding = new Padding(10, 5, 10, 5) };
        _stopButton = new Button { Text = "Stop", Enabled = false, AutoSize = true, Padding = new Padding(10, 5, 10, 5) };
        _pauseButton = new Button { Text = "Pause", Enabled = false, AutoSize = true, Padding = new Padding(10, 5, 10, 5) };
        _logBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            WordWrap = false,
            ScrollBars = RichTextBoxScrollBars.Both,
            Font = new Font("Consolas", 9f),
            BackColor = Color.FromArgb(18, 18, 18),
            ForeColor = Color.Gainsboro,
        };

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(2),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        root.Controls.Add(_contextLabel, 0, 0);
        root.Controls.Add(BuildSettingsPanel(), 0, 1);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        buttons.Controls.AddRange([_runButton, _stopButton, _pauseButton]);
        root.Controls.Add(buttons, 0, 2);
        root.Controls.Add(_logBox, 0, 3);

        _runButton.Click += async (_, _) => await RunAsync().ConfigureAwait(true);
        _stopButton.Click += (_, _) => Stop();
        _pauseButton.Click += (_, _) => TogglePause();

        ReloadContext();
    }

    public async Task ShutdownAsync()
    {
        Stop(logStop: false);
        await DisposeImportRunnerAsync().ConfigureAwait(true);
        DisposeUpdateRunners();
    }

    public void ReloadContext()
    {
        var workflow = BigSellerContextFactory.Build(_settingsFile);
        _contextLabel.Text =
            $"Shop: {workflow.ShopName}  |  Sheet: {workflow.DataSheet}\r\n" +
            $"Workbook: {workflow.WorkbookPath}";
    }

    private Control BuildSettingsPanel()
    {
        return _kind switch
        {
            BigSellerActionKind.Import => BuildImportSettings(),
            BigSellerActionKind.UpdateProduct => BuildUpdateSettings(),
            BigSellerActionKind.UpdateName => BuildNameSettings(),
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    private Control BuildImportSettings()
    {
        var workflow = BigSellerContextFactory.Build(_settingsFile);
        _accountBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        _shopBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        _crawlUrlBox = new TextBox { Text = string.IsNullOrWhiteSpace(workflow.CrawlUrl) ? BigSellerCrawlHelper.CrawlUrl : workflow.CrawlUrl, Dock = DockStyle.Fill };
        _claimedTabBox = new CheckBox { Text = "Nhap tu tab Da nhan", AutoSize = true, Checked = workflow.ImportFromClaimedTab };
        _maxProcessInput = NewNumber(1, 10, workflow.ImportMaxProcess);

        FillAccountSelector();
        _accountBox.SelectedIndexChanged += (_, _) => FillShopSelector();
        _shopBox.SelectedIndexChanged += (_, _) => LoadFieldsFromSelectedShop();

        var group = BuildGroup("Cau hinh import", 4);
        AddRow(group.Grid, 0, "Account", _accountBox, "Shop", _shopBox);
        group.Grid.Controls.Add(MakeLabel("Crawl URL"), 0, 1);
        group.Grid.Controls.Add(_crawlUrlBox, 1, 1);
        group.Grid.SetColumnSpan(_crawlUrlBox, 3);
        group.Grid.Controls.Add(_claimedTabBox, 1, 2);
        group.Grid.SetColumnSpan(_claimedTabBox, 3);
        group.Grid.Controls.Add(MakeLabel("Max process"), 0, 3);
        group.Grid.Controls.Add(_maxProcessInput, 1, 3);
        return group.Group;
    }

    private void FillAccountSelector()
    {
        if (_accountBox is null)
            return;

        _accountBox.Items.Clear();
        _accountBox.Items.Add(new SelectorItem("", "-- Chon Acc --"));
        foreach (var account in _settingsFile.Accounts)
            _accountBox.Items.Add(new SelectorItem(account.Id, account.DisplayName));

        _accountBox.SelectedIndex = 0;
        FillShopSelector();
    }

    private void FillShopSelector()
    {
        if (_accountBox is null || _shopBox is null)
            return;

        _shopBox.Items.Clear();
        _shopBox.Items.Add(new SelectorItem("", "-- Chon Shop --"));
        var account = SelectedAccount();
        if (account is null)
        {
            _shopBox.SelectedIndex = 0;
            return;
        }

        foreach (var shop in account.Shops)
            _shopBox.Items.Add(new SelectorItem(shop.Id, shop.DisplayName));

        _shopBox.SelectedIndex = 0;
        LoadFieldsFromSelectedShop();
    }

    private void LoadFieldsFromSelectedShop()
    {
        var shop = SelectedShop();
        if (shop is null)
            return;

        if (_crawlUrlBox is not null)
            _crawlUrlBox.Text = string.IsNullOrWhiteSpace(shop.BigSellerCrawlUrl) ? BigSellerCrawlHelper.CrawlUrl : shop.BigSellerCrawlUrl;
        if (_claimedTabBox is not null)
            _claimedTabBox.Checked = shop.BigSellerImportFromClaimedTab;
        if (_maxProcessInput is not null)
            _maxProcessInput.Value = _kind == BigSellerActionKind.UpdateProduct
                ? Math.Clamp(shop.BigSellerUpdateMaxProcess, 1, 10)
                : Math.Clamp(shop.BigSellerImportMaxProcess, 1, 10);
        if (_imageBox is not null)
            _imageBox.Text = shop.BigSellerImagePath;
        if (_videoBox is not null)
            _videoBox.Text = shop.BigSellerVideoFolder;
        if (_reloadInput is not null)
            _reloadInput.Value = Math.Clamp(shop.BigSellerListingReloadSeconds, 3, 600);
        if (_startRowInput is not null)
            _startRowInput.Value = Math.Max(2, shop.BigSellerStartRow);
        if (_endRowInput is not null)
            _endRowInput.Value = Math.Max(0, shop.BigSellerEndRow);
        if (_modelBox is not null)
            _modelBox.Text = string.IsNullOrWhiteSpace(shop.OpenAiModel) ? "gpt-4.1-mini" : shop.OpenAiModel;
    }

    private BigSellerAccountConfig? SelectedAccount() =>
        _accountBox?.SelectedItem is SelectorItem item && !string.IsNullOrWhiteSpace(item.Id)
            ? _settingsFile.Accounts.FirstOrDefault(a => a.Id == item.Id)
            : null;

    private ShopConfig? SelectedShop()
    {
        var account = SelectedAccount();
        return account is not null && _shopBox?.SelectedItem is SelectorItem item && !string.IsNullOrWhiteSpace(item.Id)
            ? account.Shops.FirstOrDefault(s => s.Id == item.Id)
            : null;
    }

    private Control BuildUpdateSettings()
    {
        var workflow = BigSellerContextFactory.Build(_settingsFile);
        _accountBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        _shopBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        _imageBox = new TextBox { Text = workflow.ImagePath, Dock = DockStyle.Fill };
        _videoBox = new TextBox { Text = workflow.VideoFolder, Dock = DockStyle.Fill };
        _reloadInput = NewNumber(3, 600, workflow.ListingReloadSeconds);
        _startRowInput = NewNumber(2, 1_000_000, workflow.StartRow);
        _endRowInput = NewNumber(0, 1_000_000, workflow.EndRow);
        _modelBox = new TextBox { Text = workflow.OpenAiModel, Dock = DockStyle.Fill };
        _maxProcessInput = NewNumber(1, 10, workflow.UpdateMaxProcess);

        FillAccountSelector();
        _accountBox.SelectedIndexChanged += (_, _) => FillShopSelector();
        _shopBox.SelectedIndexChanged += (_, _) => LoadFieldsFromSelectedShop();

        var group = BuildGroup("Cau hinh update product", 5);
        AddRow(group.Grid, 0, "Account", _accountBox, "Shop", _shopBox);
        AddRow(group.Grid, 1, "Anh", WrapBrowse(_imageBox, "Image|*.jpg;*.jpeg;*.png;*.webp|All|*.*"), "Video", WrapBrowseFolder(_videoBox));
        AddRow(group.Grid, 2, "Tu dong", _startRowInput, "Den dong", _endRowInput);
        AddRow(group.Grid, 3, "Reload (s)", _reloadInput, "Model API", _modelBox);
        group.Grid.Controls.Add(MakeLabel("Max process"), 0, 4);
        group.Grid.Controls.Add(_maxProcessInput, 1, 4);
        return group.Group;
    }

    private Control BuildNameSettings()
    {
        var workflow = BigSellerContextFactory.Build(_settingsFile);
        _accountBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        _shopBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        _startRowInput = NewNumber(2, 1_000_000, workflow.StartRow);
        _endRowInput = NewNumber(0, 1_000_000, workflow.EndRow);
        _modelBox = new TextBox { Text = workflow.OpenAiModel, Dock = DockStyle.Fill };
        _batchInput = NewNumber(1, 500, workflow.OpenAiBatchSize);

        FillAccountSelector();
        _accountBox.SelectedIndexChanged += (_, _) => FillShopSelector();
        _shopBox.SelectedIndexChanged += (_, _) => LoadFieldsFromSelectedShop();

        var group = BuildGroup("Cau hinh update name", 3);
        AddRow(group.Grid, 0, "Account", _accountBox, "Shop", _shopBox);
        AddRow(group.Grid, 1, "Tu dong", _startRowInput, "Den dong", _endRowInput);
        AddRow(group.Grid, 2, "AI model", _modelBox, "Batch", _batchInput);
        return group.Group;
    }

    private async Task RunAsync()
    {
        if (IsRunning)
        {
            MessageBox.Show(this, "Workflow dang chay.", "Update Product", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            SaveFields();
            var workflow = BigSellerContextFactory.Build(_settingsFile);
            ValidateWorkflow(workflow);
            UpdateProductSettings.Save(_settingsFile);
            _onChanged();
            _pauseToken.Resume();

            switch (_kind)
            {
                case BigSellerActionKind.Import:
                    await StartImportAsync(workflow).ConfigureAwait(true);
                    break;
                case BigSellerActionKind.UpdateProduct:
                    await StartUpdateProductAsync(workflow).ConfigureAwait(true);
                    break;
                case BigSellerActionKind.UpdateName:
                    StartNameRewrite(workflow);
                    break;
            }
        }
        catch (Exception ex)
        {
            SetRunning(false);
            Log($"Loi: {ex.Message}");
        }
    }

    private async Task StartImportAsync(BigSellerWorkflowSettings workflow)
    {
        SetRunning(true);
        _importCts = new CancellationTokenSource();
        var maxProcess = Math.Clamp(workflow.ImportMaxProcess, 1, 10);
        Log($"Start Import to store voi {maxProcess} process.");
        try
        {
            var tasks = Enumerable.Range(0, maxProcess)
                .Select(workerIndex => RunImportWorkerAsync(workflow, workerIndex, _importCts.Token))
                .ToArray();
            await Task.WhenAll(tasks).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Log("Da dung Import to store.");
        }
        catch (Exception ex)
        {
            if (_importCts?.IsCancellationRequested == true)
                Log("Da dung Import to store.");
            else
                throw;
        }
        finally
        {
            await DisposeImportRunnerAsync().ConfigureAwait(true);
            SetRunning(false);
        }
    }

    private async Task RunImportWorkerAsync(
        BigSellerWorkflowSettings workflow,
        int workerIndex,
        CancellationToken cancellationToken)
    {
        var workerNumber = workerIndex + 1;
        var worker = BuildImportWorkerSettings(workflow, workerIndex);
        var restartCount = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            BigSellerImportToStoreRunner? runner = null;
            try
            {
                runner = new BigSellerImportToStoreRunner(
                    worker,
                    message => Log($"[P{workerNumber}] {message}"),
                    _pauseToken,
                    workerIndex,
                    Math.Clamp(workflow.ImportMaxProcess, 1, 10));
                lock (_importRunnerLock)
                {
                    _importRunners.Add(runner);
                }

                await runner.RunAsync(cancellationToken).ConfigureAwait(true);
                return;
            }
            catch (OperationCanceledException)
            {
                Log($"[P{workerNumber}] Da dung.");
                return;
            }
            catch (Exception ex)
            {
                restartCount++;
                Log($"[P{workerNumber}] Loi: {ex.Message}");
                Log($"[P{workerNumber}] Dong profile va khoi dong lai lan {restartCount} sau 5s...");
            }
            finally
            {
                if (runner is not null)
                {
                    try { await runner.DisposeAsync().ConfigureAwait(true); } catch { }
                    lock (_importRunnerLock)
                    {
                        _importRunners.Remove(runner);
                    }
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                Log($"[P{workerNumber}] Da dung.");
                return;
            }
        }
    }

    private static BigSellerWorkflowSettings BuildImportWorkerSettings(BigSellerWorkflowSettings source, int workerIndex)
    {
        if (workerIndex == 0)
            return source;

        return new BigSellerWorkflowSettings
        {
            BravePath = source.BravePath,
            ProfileDir = $"{source.ProfileDir}-import-p{workerIndex + 1}",
            DebugPort = source.DebugPort + workerIndex,
            ImportProfileDir = $"{source.ImportProfileDir}-import-p{workerIndex + 1}",
            ImportDebugPort = source.ImportDebugPort + workerIndex,
            ShopName = source.ShopName,
            WorkbookPath = source.WorkbookPath,
            DataSheet = source.DataSheet,
            BigSellerCookieFile = source.BigSellerCookieFile,
            StartRow = source.StartRow,
            EndRow = source.EndRow,
            PythonDir = source.PythonDir,
            PythonExe = source.PythonExe,
            ImagePath = source.ImagePath,
            VideoFolder = source.VideoFolder,
            CrawlUrl = source.CrawlUrl,
            ImportFromClaimedTab = source.ImportFromClaimedTab,
            ImportMaxProcess = source.ImportMaxProcess,
            ListingReloadSeconds = source.ListingReloadSeconds,
            OpenAiModel = source.OpenAiModel,
            OpenAiApiKeyFile = source.OpenAiApiKeyFile,
            OpenAiBatchSize = source.OpenAiBatchSize,
        };
    }

    private static BigSellerWorkflowSettings BuildUpdateWorkerSettings(
        BigSellerWorkflowSettings source,
        int workerIndex)
    {
        return new BigSellerWorkflowSettings
        {
            BravePath = source.BravePath,
            ProfileDir = workerIndex == 0 ? source.ProfileDir : $"{source.ProfileDir}-update-p{workerIndex + 1}",
            DebugPort = source.DebugPort + workerIndex,
            ImportProfileDir = source.ImportProfileDir,
            ImportDebugPort = source.ImportDebugPort,
            ShopName = source.ShopName,
            WorkbookPath = source.WorkbookPath,
            DataSheet = source.DataSheet,
            BigSellerCookieFile = source.BigSellerCookieFile,
            BatchId = source.BatchId,
            StartRow = source.StartRow,
            EndRow = source.EndRow,
            PythonDir = source.PythonDir,
            PythonExe = source.PythonExe,
            ImagePath = source.ImagePath,
            VideoFolder = source.VideoFolder,
            CrawlUrl = source.CrawlUrl,
            ImportFromClaimedTab = source.ImportFromClaimedTab,
            ImportMaxProcess = source.ImportMaxProcess,
            UpdateMaxProcess = source.UpdateMaxProcess,
            ListingReloadSeconds = source.ListingReloadSeconds,
            OpenAiModel = source.OpenAiModel,
            OpenAiApiKeyFile = source.OpenAiApiKeyFile,
            OpenAiBatchSize = source.OpenAiBatchSize,
        };
    }

    private void StartPython(BigSellerWorkflowSettings workflow)
    {
        SetRunning(true);
        _pythonRunner.Start(
            workflow,
            nameOnly: false,
            Log,
            () => SafeBeginInvoke(() => SetRunning(false)));
    }

    private async Task StartUpdateProductAsync(BigSellerWorkflowSettings workflow)
    {
        SetRunning(true);
        _updateCts = new CancellationTokenSource();
        var maxProcess = Math.Clamp(workflow.UpdateMaxProcess, 1, 10);
        var startRow = Math.Max(2, workflow.StartRow);
        var endRow = workflow.EndRow <= 0 ? 0 : Math.Max(startRow, workflow.EndRow);
        workflow = WithUpdateRunSettings(workflow, startRow, endRow, maxProcess);
        _lastUpdateWorkflow = workflow;

        // Dọn worker Brave còn sót + claim store/cache cũ trước khi chạy để restart luôn sạch.
        Log("Dang don worker/claim cu truoc khi chay...");
        await BigSellerPythonRunner.RunCleanupStateAsync(workflow, Log).ConfigureAwait(true);

        var endRowText = endRow <= 0 ? "het" : endRow.ToString();
        Log($"Start Update product voi {maxProcess} worker, dong {startRow}..{endRowText}.");

        for (var workerIndex = 0; workerIndex < maxProcess; workerIndex++)
        {
            _ = RunUpdateWorkerAsync(workflow, workerIndex, _updateCts.Token);
        }
    }

    private static BigSellerWorkflowSettings WithUpdateRunSettings(
        BigSellerWorkflowSettings source,
        int startRow,
        int endRow,
        int maxProcess)
    {
        return new BigSellerWorkflowSettings
        {
            BravePath = source.BravePath,
            ProfileDir = source.ProfileDir,
            DebugPort = source.DebugPort,
            ImportProfileDir = source.ImportProfileDir,
            ImportDebugPort = source.ImportDebugPort,
            ShopName = source.ShopName,
            WorkbookPath = source.WorkbookPath,
            DataSheet = source.DataSheet,
            BigSellerCookieFile = source.BigSellerCookieFile,
            BatchId = string.IsNullOrWhiteSpace(source.BatchId) ? Guid.NewGuid().ToString("N") : source.BatchId,
            StartRow = startRow,
            EndRow = endRow,
            PythonDir = source.PythonDir,
            PythonExe = source.PythonExe,
            ImagePath = source.ImagePath,
            VideoFolder = source.VideoFolder,
            CrawlUrl = source.CrawlUrl,
            ImportFromClaimedTab = source.ImportFromClaimedTab,
            ImportMaxProcess = source.ImportMaxProcess,
            UpdateMaxProcess = maxProcess,
            ListingReloadSeconds = source.ListingReloadSeconds,
            OpenAiModel = source.OpenAiModel,
            OpenAiApiKeyFile = source.OpenAiApiKeyFile,
            OpenAiBatchSize = source.OpenAiBatchSize,
        };
    }

    private async Task RunUpdateWorkerAsync(
        BigSellerWorkflowSettings workflow,
        int workerIndex,
        CancellationToken cancellationToken)
    {
        var workerNumber = workerIndex + 1;
        var runner = new BigSellerPythonRunner();
        lock (_updateRunnerLock)
        {
            _updateRunners.Add(runner);
        }

        try
        {
            var workerWorkflow = BuildUpdateWorkerSettings(workflow, workerIndex);
            Log($"[P{workerNumber}] Update listing, tra XLSX dong {workflow.StartRow}..{(workflow.EndRow <= 0 ? "het" : workflow.EndRow.ToString())}.");
            Log($"[P{workerNumber}] Profile: {workerWorkflow.ProfileDir} | CDP port: {workerWorkflow.DebugPort}.");
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            runner.Start(
                workerWorkflow,
                nameOnly: false,
                message => Log($"[P{workerNumber}] {message}"),
                () => done.TrySetResult());

            await WaitForProcessExitOrCancelAsync(done.Task, runner, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Log($"[P{workerNumber}] Da dung.");
            return;
        }
        catch (Exception ex)
        {
            Log($"[P{workerNumber}] Loi: {ex.Message}");
        }
        finally
        {
            runner.Stop();
            var allDone = false;
            lock (_updateRunnerLock)
            {
                _updateRunners.Remove(runner);
                allDone = _updateRunners.Count == 0;
            }

            if (allDone)
            {
                // Tất cả worker đã thoát -> dọn Brave còn sót + claim store/cache của batch.
                await BigSellerPythonRunner.RunCleanupStateAsync(workflow, Log).ConfigureAwait(false);
                SafeBeginInvoke(() => SetRunning(false));
            }
        }
    }

    private static async Task WaitForProcessExitOrCancelAsync(
        Task processDone,
        BigSellerPythonRunner runner,
        CancellationToken cancellationToken)
    {
        var cancelTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        var finished = await Task.WhenAny(processDone, cancelTask).ConfigureAwait(false);
        if (finished == cancelTask)
        {
            runner.Stop();
            cancellationToken.ThrowIfCancellationRequested();
        }

        await processDone.ConfigureAwait(false);
    }

    private void StartNameRewrite(BigSellerWorkflowSettings workflow)
    {
        SetRunning(true, enablePause: false);
        _nameRunner.Start(
            workflow,
            Log,
            () => SafeBeginInvoke(() => SetRunning(false, enablePause: false)));
    }

    private void Stop(bool logStop = true)
    {
        switch (_kind)
        {
            case BigSellerActionKind.Import:
                _importCts?.Cancel();
                _pauseToken.Resume();
                if (logStop && _importRunners.Count > 0)
                    Log("Dang dung Import to store...");
                break;
            case BigSellerActionKind.UpdateProduct:
                _updateCts?.Cancel();
                BigSellerPythonRunner[] updateRunners;
                lock (_updateRunnerLock)
                {
                    updateRunners = _updateRunners.ToArray();
                }
                foreach (var runner in updateRunners)
                {
                    if (logStop)
                        runner.Stop(Log);
                    else
                        runner.Stop();
                }
                if (logStop && updateRunners.Length > 0)
                    Log("Dang dung Update product...");
                _pauseToken.Resume();
                try { _updateCts?.Dispose(); } catch { }
                _updateCts = null;
                // Stop: đóng HẾT profile đã mở (gồm cả base/worker0) + dọn claim store/cache.
                if (_lastUpdateWorkflow is { } stopWorkflow)
                    _ = BigSellerPythonRunner.RunCleanupStateAsync(stopWorkflow, logStop ? Log : null, includeBase: true);
                break;
            case BigSellerActionKind.UpdateName:
                if (logStop)
                    _nameRunner.Stop(Log);
                else
                    _nameRunner.Stop();
                break;
        }
    }

    private void TogglePause()
    {
        if (!IsRunning || _kind == BigSellerActionKind.UpdateName)
            return;

        if (_kind == BigSellerActionKind.Import)
        {
            if (_pauseToken.IsPaused)
            {
                _pauseToken.Resume();
                _pauseButton.Text = "Pause";
                Log("Tiep tuc Import to store.");
            }
            else
            {
                _pauseToken.Pause();
                _pauseButton.Text = "Resume";
                Log("Tam dung Import to store.");
            }
            return;
        }

        if (_kind == BigSellerActionKind.UpdateProduct)
        {
            BigSellerPythonRunner[] runners;
            lock (_updateRunnerLock)
                runners = _updateRunners.ToArray();

            if (_pauseToken.IsPaused)
            {
                _pauseToken.Resume();
                foreach (var runner in runners)
                    runner.Resume(Log);
                _pauseButton.Text = "Pause";
            }
            else
            {
                _pauseToken.Pause();
                foreach (var runner in runners)
                    runner.Pause(Log);
                _pauseButton.Text = "Resume";
            }
            return;
        }

        if (_pythonRunner.IsPaused)
        {
            _pythonRunner.Resume(Log);
            _pauseButton.Text = "Pause";
        }
        else
        {
            _pythonRunner.Pause(Log);
            _pauseButton.Text = "Resume";
        }
    }

    private bool IsRunning => _kind switch
    {
        BigSellerActionKind.Import => _importRunners.Count > 0,
        BigSellerActionKind.UpdateProduct => _updateRunners.Count > 0 || _pythonRunner.IsRunning,
        BigSellerActionKind.UpdateName => _nameRunner.IsRunning,
        _ => false,
    };

    private void SetRunning(bool running, bool enablePause = true)
    {
        _markedRunning = running;
        _runButton.Enabled = !running;
        _stopButton.Enabled = running;
        _pauseButton.Enabled = running && enablePause;
        if (!running)
        {
            _pauseButton.Text = "Pause";
            _pauseToken.Resume();
        }
    }

    private void SaveFields()
    {
        var shop = BigSellerContextFactory.ActiveShop(_settingsFile);
        if (_kind == BigSellerActionKind.Import)
        {
            var account = SelectedAccount();
            shop = SelectedShop() ?? throw new InvalidOperationException("Hay chon Account va Shop truoc khi chay.");
            if (account is null)
                throw new InvalidOperationException("Hay chon Account truoc khi chay.");
            _settingsFile.ActiveAccountId = account.Id;
            _settingsFile.ActiveShopId = shop.Id;
            shop.BigSellerCrawlUrl = string.IsNullOrWhiteSpace(_crawlUrlBox?.Text) ? BigSellerCrawlHelper.CrawlUrl : _crawlUrlBox.Text.Trim();
            shop.BigSellerImportFromClaimedTab = _claimedTabBox?.Checked == true;
            shop.BigSellerImportMaxProcess = _maxProcessInput is null ? shop.BigSellerImportMaxProcess : (int)_maxProcessInput.Value;
        }
        else if (_kind == BigSellerActionKind.UpdateProduct)
        {
            var account = SelectedAccount();
            shop = SelectedShop() ?? throw new InvalidOperationException("Hay chon Account va Shop truoc khi chay.");
            if (account is null)
                throw new InvalidOperationException("Hay chon Account truoc khi chay.");
            _settingsFile.ActiveAccountId = account.Id;
            _settingsFile.ActiveShopId = shop.Id;
            shop.BigSellerImagePath = _imageBox?.Text.Trim() ?? shop.BigSellerImagePath;
            shop.BigSellerVideoFolder = _videoBox?.Text.Trim() ?? shop.BigSellerVideoFolder;
            shop.BigSellerListingReloadSeconds = _reloadInput is null ? shop.BigSellerListingReloadSeconds : (int)_reloadInput.Value;
            shop.BigSellerUpdateMaxProcess = _maxProcessInput is null ? shop.BigSellerUpdateMaxProcess : (int)_maxProcessInput.Value;
            SaveCommonRowsAndAi(shop);
        }
        else
        {
            var account = SelectedAccount();
            shop = SelectedShop() ?? throw new InvalidOperationException("Hay chon Account va Shop truoc khi chay.");
            if (account is null)
                throw new InvalidOperationException("Hay chon Account truoc khi chay.");
            _settingsFile.ActiveAccountId = account.Id;
            _settingsFile.ActiveShopId = shop.Id;
            SaveCommonRowsAndAi(shop);
            shop.OpenAiBatchSize = _batchInput is null ? shop.OpenAiBatchSize : (int)_batchInput.Value;
        }
    }

    private void SaveCommonRowsAndAi(ShopConfig shop)
    {
        shop.BigSellerStartRow = _startRowInput is null ? shop.BigSellerStartRow : (int)_startRowInput.Value;
        shop.BigSellerEndRow = _endRowInput is null ? shop.BigSellerEndRow : (int)_endRowInput.Value;
        shop.OpenAiModel = _modelBox?.Text.Trim() ?? shop.OpenAiModel;
    }

    private static void ValidateWorkflow(BigSellerWorkflowSettings workflow)
    {
        if (string.IsNullOrWhiteSpace(workflow.WorkbookPath) || !File.Exists(workflow.WorkbookPath))
            throw new FileNotFoundException($"Khong tim thay workbook: {workflow.WorkbookPath}");
        if (string.IsNullOrWhiteSpace(workflow.DataSheet))
            throw new InvalidOperationException("Chua chon sheet.");
        if (string.IsNullOrWhiteSpace(workflow.BravePath) || !File.Exists(workflow.BravePath))
            throw new FileNotFoundException($"Khong tim thay Brave: {workflow.BravePath}");
    }

    private async Task DisposeImportRunnerAsync()
    {
        BigSellerImportToStoreRunner[] runners;
        lock (_importRunnerLock)
        {
            runners = _importRunners.ToArray();
            _importRunners.Clear();
        }
        foreach (var runner in runners)
        {
            try { await runner.DisposeAsync(); } catch { }
        }
        try { _importCts?.Dispose(); } catch { }
        _importCts = null;
    }

    private void DisposeUpdateRunners()
    {
        BigSellerPythonRunner[] runners;
        lock (_updateRunnerLock)
        {
            runners = _updateRunners.ToArray();
            _updateRunners.Clear();
        }

        foreach (var runner in runners)
            runner.Stop();
        try { _updateCts?.Dispose(); } catch { }
        _updateCts = null;
    }

    private void Log(string message)
    {
        if (IsDisposed || Disposing || _logBox.IsDisposed)
            return;
        if (InvokeRequired)
        {
            SafeBeginInvoke(() => Log(message));
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

    private void SafeBeginInvoke(Action action)
    {
        if (IsDisposed || Disposing || !IsHandleCreated)
            return;
        try { BeginInvoke(action); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
    }

    private static (GroupBox Group, TableLayoutPanel Grid) BuildGroup(string title, int rows)
    {
        var group = new GroupBox { Text = title, Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, RowCount = rows };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        group.Controls.Add(grid);
        return (group, grid);
    }

    private static Label MakeLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(0, 4, 0, 0),
    };

    private static void AddRow(TableLayoutPanel grid, int row, string lbl1, Control ctrl1, string lbl2, Control ctrl2)
    {
        grid.Controls.Add(MakeLabel(lbl1), 0, row);
        grid.Controls.Add(ctrl1, 1, row);
        grid.Controls.Add(MakeLabel(lbl2), 2, row);
        grid.Controls.Add(ctrl2, 3, row);
    }

    private static NumericUpDown NewNumber(int min, int max, int value) => new()
    {
        Minimum = min,
        Maximum = max,
        Value = Math.Clamp(value, min, max),
        Width = 90,
    };

    private Control WrapBrowse(TextBox textBox, string filter)
    {
        var layout = BrowseLayout(textBox);
        var btn = (Button)layout.Controls[1];
        btn.Click += (_, _) =>
        {
            using var f = new OpenFileDialog { Filter = filter };
            if (f.ShowDialog(this) == DialogResult.OK)
                textBox.Text = f.FileName;
        };
        return layout;
    }

    private Control WrapBrowseFolder(TextBox textBox)
    {
        var layout = BrowseLayout(textBox);
        var btn = (Button)layout.Controls[1];
        btn.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog();
            if (d.ShowDialog(this) == DialogResult.OK)
                textBox.Text = d.SelectedPath;
        };
        return layout;
    }

    private static TableLayoutPanel BrowseLayout(TextBox textBox)
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = new Padding(0, 2, 4, 2) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        textBox.Dock = DockStyle.Fill;
        textBox.Margin = Padding.Empty;
        var btn = new Button { Text = "...", Dock = DockStyle.Fill, Margin = new Padding(4, 0, 0, 0) };
        layout.Controls.Add(textBox, 0, 0);
        layout.Controls.Add(btn, 1, 0);
        return layout;
    }
}
