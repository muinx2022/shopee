using System.Diagnostics;

namespace OpenMultiBraveLauncherV3;

internal sealed class InstanceDetailPanel : UserControl
{
    private InstanceConfig? _config;
    private BraveInstanceSession? _session;
    private Func<string>? _getBraveExe;
    private Func<string>? _getSourceUserData;
    private bool _suppressSync;
    private bool _autoRunMode;

    private readonly TextBox _labelTextBox;
    private readonly TextBox _keyTextBox;
    private readonly TextBox _manualProxyTextBox;
    private readonly ComboBox _regionCombo;
    private readonly ComboBox _proxyTypeCombo;
    private readonly CheckBox _newProfileCheck;
    private readonly Button _startButton;
    private readonly Button _closeProfileButton;
    private readonly Button _openProfileButton;
    private readonly Label _autoModeNoticeLabel;
    private readonly Label _statusLabel;

    private readonly GroupBox _runnerGroup;
    private readonly ComboBox _sheetCombo;
    private readonly Label _sheetValueLabel;
    private readonly NumericUpDown _startRowInput;
    private readonly NumericUpDown _endRowInput;
    private readonly NumericUpDown _nextRowInput;
    private readonly Label _nextRowSuggestLabel;
    private readonly Label _resumeHintLabel;
    private readonly Label _interruptLabel;
    private readonly Button _runButton;
    private readonly Button _resumeRunButton;
    private readonly Button _stopRunButton;
    private readonly CheckBox _autoCloseOnFinishCheck;
    private readonly TextBox _shopeeAccountTextBox;
    private readonly CheckBox _openWithShopeeAccountCheck;
    private readonly Label _syncedAtLabel;
    private readonly Button _pushExtButton;

    public event Action? ConfigChanged;
    public event Action? RunRequested;
    public event Action? ResumeContinueRequested;
    public event Action? StopRunnerRequested;

    public InstanceDetailPanel()
    {
        Dock = DockStyle.Fill;
        Padding = new Padding(12);
        AutoScroll = false;

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 4,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        var row = 0;
        AddRowSpan(table, "T\u00ean hi\u1ec3n th\u1ecb", _labelTextBox = new TextBox { Dock = DockStyle.Fill }, ref row);
        AddRowSpan(table, "KiotProxy key", _keyTextBox = new TextBox { Dock = DockStyle.Fill }, ref row);
        AddDualRow(
            table,
            "V\u00f9ng", _regionCombo = MakeCombo("random", "random", "bac", "trung", "nam"),
            "Lo\u1ea1i proxy", _proxyTypeCombo = MakeCombo("http", "http", "socks5"),
            ref row);
        _manualProxyTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            PlaceholderText = "host:port ho\u1eb7c http://... (n\u1ebfu kh\u00f4ng d\u00f9ng Kiot)",
        };
        AddRowSpan(table, "Proxy", _manualProxyTextBox, ref row);

        _newProfileCheck = new CheckBox
        {
            Text = "T\u1ea1o profile m\u1edbi khi m\u1edf profile",
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 0),
        };
        table.Controls.Add(_newProfileCheck, 0, row);
        table.SetColumnSpan(_newProfileCheck, 4);
        row++;
_openWithShopeeAccountCheck = new CheckBox
        {
            AutoSize = true,
            Text = "M\u1edf v\u1edbi tk shopee",
            Padding = new Padding(0, 2, 0, 0),
            Margin = new Padding(0, 4, 12, 0),
        };
        _shopeeAccountTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Enabled = false,
            PlaceholderText = "username|password|.shopee.vn=SPC_F=value",
        };
        var shopeeAccountRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            Margin = new Padding(0),
        };
        shopeeAccountRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        shopeeAccountRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shopeeAccountRow.Controls.Add(_openWithShopeeAccountCheck, 0, 0);
        shopeeAccountRow.Controls.Add(_shopeeAccountTextBox, 1, 0);
        table.Controls.Add(new Label { Text = "Shopee acc", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 0, row);
        table.Controls.Add(shopeeAccountRow, 1, row);
        table.SetColumnSpan(shopeeAccountRow, 3);
        row++;

        _runnerGroup = new GroupBox
        {
            Text = "Shopee Data Runner (d\u00f2ng)",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10, 10, 10, 12),
            Margin = new Padding(0, 10, 0, 0),
            Enabled = false,
        };
        var runnerTable = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 7,
            RowCount = 6,
        };
        runnerTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        runnerTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        runnerTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        runnerTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        runnerTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94));
        runnerTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        runnerTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 6; i++)
            runnerTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _sheetCombo = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Visible = false,
        };
        _sheetValueLabel = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ForeColor = Color.FromArgb(45, 45, 45),
            Padding = new Padding(0, 6, 0, 0),
            Text = "(ch\u01b0a ch\u1ecdn)",
        };
        _startRowInput = MakeRowInput(2);
        _endRowInput = MakeRowInput(0);
        _nextRowInput = MakeRowInput(2);
        _nextRowSuggestLabel = new Label
        {
            AutoSize = true,
            ForeColor = Color.Gray,
            Margin = new Padding(4, 8, 0, 0),
            Text = "",
        };
        _autoCloseOnFinishCheck = new CheckBox
        {
            AutoSize = true,
            Text = "T\u1ef1 \u0111\u00f3ng profile khi ch\u1ea1y xong",
            Checked = true,
            Padding = new Padding(0, 2, 0, 0),
        };
        AddCompactRunnerField(runnerTable, 0, "T\u1eeb d\u00f2ng", _startRowInput, 0);
        AddCompactRunnerField(runnerTable, 0, "\u0110\u1ebfn d\u00f2ng", _endRowInput, 2);
        AddCompactRunnerField(runnerTable, 0, "D\u00f2ng ti\u1ebfp", _nextRowInput, 4);
        runnerTable.Controls.Add(_nextRowSuggestLabel, 0, 1);
        runnerTable.SetColumnSpan(_nextRowSuggestLabel, 7);
        runnerTable.Controls.Add(_autoCloseOnFinishCheck, 0, 2);
        runnerTable.SetColumnSpan(_autoCloseOnFinishCheck, 7);

        _resumeHintLabel = new Label
        {
            AutoSize = true,
            ForeColor = Color.DarkGreen,
            Margin = new Padding(0, 8, 0, 4),
            MaximumSize = new Size(640, 0),
            Text = "Ch\u1ea1y t\u1eeb d\u00f2ng \"T\u1eeb d\u00f2ng\" tr\u00ean form. B\u1ea5m M\u1edf profile tr\u01b0\u1edbc.",
        };
        runnerTable.Controls.Add(_resumeHintLabel, 0, 3);
        runnerTable.SetColumnSpan(_resumeHintLabel, 7);

        _interruptLabel = new Label
        {
            AutoSize = true,
            ForeColor = Color.FromArgb(180, 80, 0),
            Margin = new Padding(0, 4, 0, 4),
            MaximumSize = new Size(640, 0),
            Visible = false,
            Text = "",
        };
        runnerTable.Controls.Add(_interruptLabel, 0, 4);
        runnerTable.SetColumnSpan(_interruptLabel, 7);

        _runButton = new Button
        {
            AutoSize = true,
            Margin = new Padding(0, 6, 8, 0),
            Padding = new Padding(8, 4, 10, 4),
        };
        UiButtonHelper.Style(_runButton, UiButtonHelper.Run(), "Ch\u1ea1y");
        _runButton.Click += (_, _) => ApplyRun();

        _resumeRunButton = new Button
        {
            AutoSize = true,
            Visible = false,
            Margin = new Padding(0, 6, 8, 0),
            Padding = new Padding(8, 4, 10, 4),
        };
        UiButtonHelper.Style(_resumeRunButton, UiButtonHelper.Resume(), "Ch\u1ea1y ti\u1ebfp");
        _resumeRunButton.Click += (_, _) => ApplyResumeContinue();

        _stopRunButton = new Button
        {
            AutoSize = true,
            Visible = false,
            Margin = new Padding(0, 6, 8, 0),
            Padding = new Padding(8, 4, 10, 4),
        };
        UiButtonHelper.Style(_stopRunButton, UiButtonHelper.Stop(), "D\u1eebng ch\u1ea1y");
        _stopRunButton.Click += (_, _) => StopRunnerRequested?.Invoke();

        _pushExtButton = new Button
        {
            AutoSize = true,
            Margin = new Padding(0, 8, 8, 0),
            Padding = new Padding(8, 4, 10, 4),
        };
        UiButtonHelper.Style(_pushExtButton, UiButtonHelper.PushExt(), "C\u1eadp nh\u1eadt");
        _pushExtButton.Click += async (_, _) => await PushConfigToExtensionAsync(manual: true);

        _syncedAtLabel = new Label
        {
            AutoSize = true,
            ForeColor = Color.Gray,
            Padding = new Padding(0, 12, 0, 0),
            Text = "",
        };

        var syncRow = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            WrapContents = true,
            Margin = new Padding(0, 6, 0, 0),
        };
        syncRow.Controls.Add(_runButton);
        syncRow.Controls.Add(_stopRunButton);
        syncRow.Controls.Add(_resumeRunButton);
        syncRow.Controls.Add(_pushExtButton);
        syncRow.Controls.Add(_syncedAtLabel);
        runnerTable.Controls.Add(syncRow, 0, 5);
        runnerTable.SetColumnSpan(syncRow, 7);

        _runnerGroup.Controls.Add(runnerTable);
        row++;
        table.Controls.Add(_runnerGroup, 0, row);
        table.SetColumnSpan(_runnerGroup, 4);
        row++;

        _autoModeNoticeLabel = new Label
        {
            AutoSize = true,
            ForeColor = Color.FromArgb(120, 70, 0),
            BackColor = Color.FromArgb(255, 246, 214),
            Padding = new Padding(8, 6, 8, 6),
            Margin = new Padding(0, 8, 0, 0),
            MaximumSize = new Size(720, 0),
            Visible = false,
            Text = "\u0110ang \u1edf ch\u1ebf \u0111\u1ed9 Auto: c\u00e1c instance d\u00f9ng c\u1ea5u h\u00ecnh t\u1ef1 \u0111\u1ed9ng. Mu\u1ed1n g\u1ee1/t\u1eaft Auto, b\u1ecf tick Ch\u1ea1y t\u1ef1 \u0111\u1ed9ng \u1edf ph\u1ea7n Auto.",
        };
        table.Controls.Add(_autoModeNoticeLabel, 0, row);
        table.SetColumnSpan(_autoModeNoticeLabel, 4);
        row++;

        _statusLabel = new Label
        {
            Text = "Ch\u1ecdn instance b\u00ean tr\u00e1i",
            AutoSize = true,
            ForeColor = Color.Gray,
            Padding = new Padding(0, 8, 0, 0),
        };
        table.Controls.Add(_statusLabel, 0, row);
        table.SetColumnSpan(_statusLabel, 4);
        row++;

        var btnFlow = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
        _startButton = new Button { Width = 110, Height = 32 };
        UiButtonHelper.Style(_startButton, UiButtonHelper.OpenProfile(), "M\u1edf profile");
        _closeProfileButton = new Button
        {
            AutoSize = true,
            Height = 32,
            Enabled = false,
            Margin = new Padding(8, 0, 8, 0),
            Padding = new Padding(8, 0, 10, 0),
        };
        UiButtonHelper.Style(_closeProfileButton, UiButtonHelper.CloseProfile(), "\u0110\u00f3ng profile");
        _openProfileButton = new Button
        {
            AutoSize = true,
            Height = 32,
            Padding = new Padding(8, 0, 10, 0),
        };
        UiButtonHelper.Style(_openProfileButton, UiButtonHelper.OpenFolder(), "M\u1edf th\u01b0 m\u1ee5c profile");
        btnFlow.Controls.AddRange([_startButton, _closeProfileButton, _openProfileButton]);
        table.Controls.Add(btnFlow, 0, row);
        table.SetColumnSpan(btnFlow, 4);

        Controls.Add(table);

        _labelTextBox.TextChanged += (_, _) => SyncToConfig();
        _keyTextBox.TextChanged += (_, _) => SyncToConfig();
        _manualProxyTextBox.TextChanged += (_, _) => SyncToConfig();
        _regionCombo.SelectedIndexChanged += (_, _) => SyncToConfig();
        _proxyTypeCombo.SelectedIndexChanged += (_, _) => SyncToConfig();
        _newProfileCheck.CheckedChanged += (_, _) => SyncToConfig();
        _shopeeAccountTextBox.TextChanged += (_, _) => SyncToConfig();
        _openWithShopeeAccountCheck.CheckedChanged += (_, _) =>
        {
            UpdateShopeeAccountUi();
            SyncToConfig();
        };
        _sheetCombo.SelectedIndexChanged += (_, _) => SyncToConfig();
        _sheetCombo.DropDown += async (_, _) => await LoadSheetsAsync();
        _autoCloseOnFinishCheck.CheckedChanged += (_, _) => SyncToConfig();
        _startRowInput.ValueChanged += (_, _) =>
        {
            if (_suppressSync) return;
            AutoFillEndRowFromStartRow();
            ResetRunnerProgressForStartRowEdit();
            AlignNextRunRowToRange();
            SyncToConfig();
        };
        _startRowInput.TextChanged += (_, _) =>
        {
            if (_suppressSync) return;
            AutoFillEndRowFromStartRow();
            ResetRunnerProgressForStartRowEdit();
            SyncToConfig();
        };
        _endRowInput.ValueChanged += (_, _) => SyncToConfig();
        _nextRowInput.ValueChanged += (_, _) => SyncToConfig();

        _startButton.Click += async (_, _) => await StartBraveAsync();
        _closeProfileButton.Click += async (_, _) => await CloseProfileAsync();
        _openProfileButton.Click += (_, _) => OpenProfileFolder();
    }

    public void FlushToConfig() => SyncToConfig();

    /// <summary>Bật chế độ chạy lượt tự động — khóa Chạy / Chạy tiếp thủ công.</summary>
    public void SetAutoRunMode(bool enabled)
    {
        _autoRunMode = enabled;
        _autoModeNoticeLabel.Visible = enabled;
        UpdateShopeeAccountUi();
        UpdateStatusUi();
    }

    public void SetBusy(bool busy, string? statusHint = null)
    {
        if (InvokeRequired)
        {
            SafeBeginInvoke(() => SetBusy(busy, statusHint), "InstanceDetailPanel.SetBusy");
            return;
        }

        var running = _session?.IsRunning == true;
        _startButton.Enabled = !busy && !running && !_autoRunMode;
        _closeProfileButton.Enabled = !busy && running;
        _runnerGroup.Enabled = running && !busy;

        if (!string.IsNullOrWhiteSpace(statusHint) && busy)
            _statusLabel.Text = statusHint;

        var form = FindForm();
        if (form is not null)
            form.Cursor = busy ? Cursors.WaitCursor : Cursors.Default;

        if (!busy)
            UpdateStatusUi();
        UpdateShopeeAccountUi();
    }

    private void UpdateShopeeAccountUi()
    {
        _shopeeAccountTextBox.Enabled = _openWithShopeeAccountCheck.Checked && !_autoRunMode;
    }

    public void RefreshProgressFromConfig(bool fromProgressSync = false)
    {
        if (_config is null) return;
        _suppressSync = true;
        try
        {
            SetSheetSelection(_config.DataSheet);
            _sheetValueLabel.Text = string.IsNullOrWhiteSpace(_config.DataSheet) ? "(chưa chọn)" : _config.DataSheet;
            SetNumeric(_startRowInput, _config.StartRow);
            SetNumeric(_endRowInput, _config.EndRow);
            UpdateNextRowUi(fromProgressSync);
            UpdateResumeHint();
            UpdateInterruptUi();
            UpdateSyncedLabel();
        }
        finally
        {
            _suppressSync = false;
        }
    }

    public void Bind(
        InstanceConfig? config,
        BraveInstanceSession? session,
        Func<string> getBraveExe,
        Func<string> getSourceUserData)
    {
        if (_config is not null && !ReferenceEquals(_config, config))
            SyncToConfig();

        if (_session is not null)
        {
            _session.StatusChanged -= OnSessionStatusChanged;
            _session.ExtensionProgressSynced -= OnExtensionProgressSynced;
        }

        _config = config;
        _session = session;
        _getBraveExe = getBraveExe;
        _getSourceUserData = getSourceUserData;

        if (_session is not null)
        {
            _session.StatusChanged += OnSessionStatusChanged;
            _session.ExtensionProgressSynced += OnExtensionProgressSynced;
        }

        _suppressSync = true;
        try
        {
            if (config is null)
            {
                Enabled = false;
                _labelTextBox.Clear();
                _keyTextBox.Clear();
                _sheetCombo.SelectedIndex = -1;
                _statusLabel.Text = "Chọn instance bên trái";
                _statusLabel.ForeColor = Color.Gray;
                return;
            }

            Enabled = true;
            _labelTextBox.Text = config.Label;
            _keyTextBox.Text = config.KiotProxyKey;
            _manualProxyTextBox.Text = config.ManualProxy ?? "";
            SelectCombo(_regionCombo, config.Region);
            SelectCombo(_proxyTypeCombo, config.ProxyType);
            _newProfileCheck.Checked = config.CreateNewProfileOnNextStart;
            _shopeeAccountTextBox.Text = config.ShopeeAccountLogin ?? "";
            _openWithShopeeAccountCheck.Checked = config.OpenWithShopeeAccount;
            UpdateShopeeAccountUi();
            _autoCloseOnFinishCheck.Checked = config.AutoCloseProfileOnFinish;
            RefreshProgressFromConfig();
            UpdateStatusUi();
        }
        finally
        {
            _suppressSync = false;
        }
    }

    private void OnExtensionProgressSynced()
    {
        if (InvokeRequired)
        {
            SafeBeginInvoke(ApplyExtensionProgressSyncedUi, "InstanceDetailPanel.OnExtensionProgressSynced");
            return;
        }
        ApplyExtensionProgressSyncedUi();
    }

    private void ApplyExtensionProgressSyncedUi()
    {
        RefreshProgressFromConfig(fromProgressSync: true);
        UpdateStatusUi();
        ConfigChanged?.Invoke();
    }

    private void OnSessionStatusChanged()
    {
        if (InvokeRequired)
        {
            SafeBeginInvoke(UpdateStatusUi, "InstanceDetailPanel.OnSessionStatusChanged");
            return;
        }
        UpdateStatusUi();
    }

    private void SafeBeginInvoke(Action action, string context)
    {
        if (IsDisposed || Disposing || !IsHandleCreated)
        {
            AppDiagnostics.Log($"{context}: skipped invoke because panel is disposed or handle is missing.");
            return;
        }

        try
        {
            BeginInvoke(() =>
            {
                try
                {
                    if (!IsDisposed && !Disposing)
                        action();
                }
                catch (Exception ex)
                {
                    AppDiagnostics.LogException(context, ex);
                }
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            AppDiagnostics.LogException($"{context}: BeginInvoke failed", ex);
        }
    }

    private void UpdateStatusUi()
    {
        if (_session is null || _config is null)
            return;

        _autoModeNoticeLabel.Visible = _autoRunMode;
        _statusLabel.Text = _session.StatusText;
        _statusLabel.ForeColor = _session.IsRunning ? Color.DarkGreen : Color.Gray;
        _startButton.Enabled = !_session.IsRunning && !_autoRunMode;
        _closeProfileButton.Enabled = _session.IsRunning;

        UpdateExtSectionUi();
    }

    private void UpdateExtSectionUi()
    {
        if (_session is null || _config is null)
            return;

        var profileOpen = _session.IsRunning;
        _runnerGroup.Enabled = profileOpen;

        if (!profileOpen)
        {
            _resumeHintLabel.ForeColor = Color.Gray;
            _resumeHintLabel.Text = _autoRunMode
                ? "Ch\u1ebf \u0111\u1ed9 auto - b\u1ea5m Ch\u1ea1y t\u1ef1 \u0111\u1ed9ng trong ph\u1ea7n Auto (profile t\u1ef1 m\u1edf khi \u0111\u1ebfn l\u01b0\u1ee3t)."
                : "B\u1ea5m \"M\u1edf profile\" tr\u01b0\u1edbc - ph\u1ea7n n\u00e0y ch\u1ec9 d\u00f9ng \u0111\u01b0\u1ee3c khi Brave \u0111ang m\u1edf.";
            _interruptLabel.Visible = false;
            _resumeRunButton.Visible = false;
            _stopRunButton.Visible = false;
            return;
        }

        _resumeHintLabel.ForeColor = Color.DarkGreen;
        if (_autoRunMode)
        {
            _runButton.Enabled = false;
            _resumeRunButton.Enabled = false;
            _resumeHintLabel.ForeColor = Color.DarkSlateBlue;
            _resumeHintLabel.Text =
                "Ch\u1ebf \u0111\u1ed9 auto - b\u1ea5m Ch\u1ea1y t\u1ef1 \u0111\u1ed9ng trong ph\u1ea7n Auto (kh\u00f4ng ch\u1ea1y th\u1ee7 c\u00f4ng t\u1eebng profile).";
        }
        else
        {
            _runButton.Enabled = !_session.IsRunnerLoopActive;
            _resumeRunButton.Enabled = !_session.IsRunnerLoopActive;
            UpdateResumeHint();
        }
        _stopRunButton.Visible = _session.IsRunnerLoopActive;
        _stopRunButton.Enabled = _session.IsRunnerLoopActive;
        _pushExtButton.Enabled = true;
        UpdateInterruptUi();
    }

    private void SyncToConfig()
    {
        if (_suppressSync || _config is null) return;
        _config.Label = _labelTextBox.Text.Trim();
        _config.KiotProxyKey = _keyTextBox.Text.Trim();
        _config.ManualProxy = _manualProxyTextBox.Text.Trim();
        _config.Region = _regionCombo.SelectedItem?.ToString() ?? "random";
        _config.ProxyType = _proxyTypeCombo.SelectedItem?.ToString() ?? "http";
        _config.CreateNewProfileOnNextStart = _newProfileCheck.Checked;
        _config.ShopeeAccountLogin = _shopeeAccountTextBox.Text.Trim();
        _config.OpenWithShopeeAccount = _openWithShopeeAccountCheck.Checked;
        _config.AutoCloseProfileOnFinish = _autoCloseOnFinishCheck.Checked;
        _config.StartRow = (int)_startRowInput.Value;
        _config.EndRow = _endRowInput.Value >= 2 ? (int)_endRowInput.Value : null;
        AlignNextRunRowToRange();
        UpdateNextRowSuggestLabel();
        UpdateResumeHint();
        ConfigChanged?.Invoke();
    }

    private void AlignNextRunRowToRange()
    {
        if (_config is null) return;
        _config.NextRunRow = (int)_nextRowInput.Value;
    }

    private void AutoFillEndRowFromStartRow()
    {
        var startRow = int.TryParse(_startRowInput.Text, out var typedStartRow)
            ? typedStartRow
            : (int)_startRowInput.Value;
        if (startRow < (int)_startRowInput.Minimum)
            return;

        var endRow = Math.Min((int)_endRowInput.Maximum, startRow + 30);
        if ((int)_endRowInput.Value != endRow)
            SetNumeric(_endRowInput, endRow);
    }

    private void ResetRunnerProgressForStartRowEdit()
    {
        if (_config is null) return;
        if (!int.TryParse(_startRowInput.Text, out var startRow))
            startRow = (int)_startRowInput.Value;
        if (startRow < (int)_startRowInput.Minimum)
            return;

        _config.LastCompletedRow = null;
        _config.CurrentRow = null;
        _config.LastSku = null;
        _config.RunnerPhase = null;
        _config.RunnerRunning = null;
        _config.LastRunnerMessage = null;
        _config.ProgressSyncedAt = null;
        _config.RunLog.Clear();
        _config.NextRunRow = startRow;
        SetNumeric(_nextRowInput, startRow);
    }

    private void UpdateNextRowUi(bool fromProgressSync = false)
    {
        if (_config is null) return;

        var suggested = _config.ComputeSuggestedNextRow();
        if (fromProgressSync &&
            suggested is > 0 &&
            (_config.NextRunRow is not > 0 || !_config.TryValidateRunRow(_config.NextRunRow.Value, out _)))
        {
            _config.NextRunRow = suggested;
            SetNumeric(_nextRowInput, suggested);
        }
        else
        {
            var value = _config.NextRunRow is > 0 ? _config.NextRunRow : suggested;
            if (value is > 0)
                SetNumeric(_nextRowInput, value);
        }
        UpdateNextRowSuggestLabel();
    }

    private void UpdateNextRowSuggestLabel()
    {
        if (_config is null)
        {
            _nextRowSuggestLabel.Text = "";
            return;
        }

        var suggested = _config.ComputeSuggestedNextRow();
        if (suggested is not > 0)
        {
            _nextRowSuggestLabel.Text = "";
            return;
        }

        var current = (int)_nextRowInput.Value;
        _nextRowSuggestLabel.Text = current == suggested
            ? $"Gợi ý: {suggested}"
            : $"Gợi ý: {suggested} (đang chọn {current})";
    }

    private bool TryValidateNextRunRow(out string? error)
    {
        AlignNextRunRowToRange();
        SyncToConfig();
        if (_config is null)
        {
            error = "Chưa chọn instance.";
            return false;
        }

        return _config.TryValidateRunRow((int)_nextRowInput.Value, out error);
    }

    private async Task PushConfigToExtensionAsync(bool manual = false)
    {
        if (_session is null || _config is null)
            return;

        SyncToConfig();
        _session.ApplyConfig(_config);

        if (string.IsNullOrWhiteSpace(_config.DataSheet))
        {
            if (manual)
            {
                MessageBox.Show(FindForm(), "Nhập tên sheet trước.", "Cập nhật",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return;
        }

        if (!_session.IsRunning)
        {
            if (manual)
            {
                MessageBox.Show(FindForm(), "Brave chưa chạy — bấm Mở profile trước.", "Cập nhật",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return;
        }

        _pushExtButton.Enabled = false;
        try
        {
            await _session.PushFormConfigToExtensionAsync(logSuccess: manual);
        }
        catch (Exception ex)
        {
            if (manual)
            {
                MessageBox.Show(FindForm(), ex.Message, "Cập nhật",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        finally
        {
            if (_session?.IsRunning == true)
                UpdateExtSectionUi();
        }
    }

    private void UpdateResumeHint()
    {
        if (_config is null)
        {
            _resumeHintLabel.Text = "";
            return;
        }

        if (_session is not { IsRunning: true })
        {
            _resumeHintLabel.ForeColor = Color.Gray;
            _resumeHintLabel.Text = "B\u1ea5m \"M\u1edf profile\" tr\u01b0\u1edbc - ph\u1ea7n n\u00e0y ch\u1ec9 d\u00f9ng \u0111\u01b0\u1ee3c khi Brave \u0111ang m\u1edf.";
            return;
        }

        _resumeHintLabel.ForeColor = Color.DarkGreen;
        if (_config.IsInterruptedMidRun)
        {
            _resumeHintLabel.Text = "B\u1ecb d\u1eebng gi\u1eefa ch\u1eebng - b\u1ea5m Ch\u1ea1y ti\u1ebfp, ho\u1eb7c Ch\u1ea1y t\u1eeb d\u00f2ng kh\u00e1c tr\u00ean form.";
            return;
        }

        var sheet = string.IsNullOrWhiteSpace(_config.DataSheet) ? "(sheet)" : _config.DataSheet;
        var from = _config.StartRow is > 0 ? _config.StartRow.ToString() : "?";
        var to = _config.EndRow?.ToString() ?? "hết";
        var next = (int)_nextRowInput.Value;
        var suggested = _config.ComputeSuggestedNextRow();
        _resumeHintLabel.Text = suggested is > 0
            ? $"Sheet \"{sheet}\", ph\u1ea1m vi {from}-{to}. Ch\u1ea1y t\u1eeb d\u00f2ng {next} (g\u1ee3i \u00fd {suggested})."
            : $"Sheet \"{sheet}\", ph\u1ea1m vi {from}-{to}. Nh\u1eadp d\u00f2ng ti\u1ebfp theo.";
    }

    private void ApplyRun()
    {
        if (_config is null || _session is null)
            return;

        if (_autoRunMode)
        {
            MessageBox.Show(
                FindForm(),
                "\u0110ang b\u1eadt ch\u1ebf \u0111\u1ed9 ch\u1ea1y t\u1ef1 \u0111\u1ed9ng - d\u00f9ng n\u00fat Ch\u1ea1y t\u1ef1 \u0111\u1ed9ng trong ph\u1ea7n Auto.",
                "Chạy",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        SyncToConfig();
        AlignNextRunRowToRange();

        if (!_session.IsRunning)
        {
            MessageBox.Show(
                FindForm(),
                "B\u1ea5m \"M\u1edf profile\" tr\u01b0\u1edbc, r\u1ed3i b\u1ea5m Ch\u1ea1y.",
                "Chạy",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        if (_session.IsRunnerLoopActive)
        {
            MessageBox.Show(
                FindForm(),
                "Runner \u0111ang ch\u1ea1y - b\u1ea5m D\u1eebng ch\u1ea1y n\u1ebfu mu\u1ed1n d\u1eebng.",
                "Chạy",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(_config.DataSheet))
        {
            MessageBox.Show(FindForm(), "Nh\u1eadp t\u00ean sheet tr\u01b0\u1edbc.", "Ch\u1ea1y",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_config.StartRow is not > 0)
        {
            MessageBox.Show(FindForm(), "Nh\u1eadp \"T\u1eeb d\u00f2ng\" (>= 2) tr\u01b0\u1edbc.", "Ch\u1ea1y",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!TryValidateNextRunRow(out var err))
        {
            MessageBox.Show(FindForm(), err ?? "D\u00f2ng ch\u1ea1y kh\u00f4ng h\u1ee3p l\u1ec7.", "Ch\u1ea1y",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        RunRequested?.Invoke();
    }

    private void UpdateInterruptUi()
    {
        if (_autoRunMode || _config is null || !_config.IsInterruptedMidRun)
        {
            _interruptLabel.Visible = false;
            _resumeRunButton.Visible = false;
            return;
        }

        var at = _config.StoppedAtRow;
        var resume = _config.GetEffectiveRunRow();
        var sheet = string.IsNullOrWhiteSpace(_config.DataSheet) ? "?" : _config.DataSheet;
        _interruptLabel.Visible = true;
        _interruptLabel.Text =
            $"B\u1ecb d\u1eebng gi\u1eefa ch\u1eebng: d\u00f2ng {at} (sheet \"{sheet}\"). S\u1eeda \"D\u00f2ng ti\u1ebfp theo\" r\u1ed3i Ch\u1ea1y ti\u1ebfp.";
        _resumeRunButton.Visible = true;
    }

    private void ApplyResumeContinue()
    {
        if (_config is null) return;

        if (_autoRunMode)
        {
            MessageBox.Show(
                FindForm(),
                "\u0110ang b\u1eadt ch\u1ebf \u0111\u1ed9 ch\u1ea1y t\u1ef1 \u0111\u1ed9ng - d\u00f9ng n\u00fat Ch\u1ea1y t\u1ef1 \u0111\u1ed9ng trong ph\u1ea7n Auto.",
                "Chạy tiếp",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        SyncToConfig();
        AlignNextRunRowToRange();

        if (!TryValidateNextRunRow(out var err))
        {
            MessageBox.Show(FindForm(), err ?? "D\u00f2ng ch\u1ea1y kh\u00f4ng h\u1ee3p l\u1ec7.", "Ch\u1ea1y ti\u1ebfp",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Ch\u1ea1y ti\u1ebfp c\u00f3 th\u1ec3 t\u1ef1 m\u1edf Brave qua v\u00f2ng runner -> c\u0169ng ph\u1ea3i qua x\u00e1c nh\u1eadn proxy nh\u01b0 M\u1edf profile.
        if (!EnsureProxyOrConfirm())
            return;

        ResumeContinueRequested?.Invoke();
    }

    private bool EnsureProxyOrConfirm() =>
        _config is null || _session is null ||
        ProxyLaunchGuard.ConfirmOrAllow(_config, _session, FindForm());

    private void UpdateSyncedLabel()
    {
        if (_config?.ProgressSyncedAt is { } at)
            _syncedAtLabel.Text = $"Ti\u1ebfn \u0111\u1ed9 c\u1eadp nh\u1eadt l\u00fac {at.LocalDateTime:HH:mm:ss dd/MM}";
        else
            _syncedAtLabel.Text = "";
    }

    private async Task StartBraveAsync()
    {
        if (_session is null || _config is null || _getBraveExe is null || _getSourceUserData is null)
            return;

        if (_autoRunMode)
        {
            MessageBox.Show(
                FindForm(),
                "\u0110ang b\u1eadt ch\u1ebf \u0111\u1ed9 auto - profile \u0111\u01b0\u1ee3c m\u1edf b\u1edfi n\u00fat Ch\u1ea1y t\u1ef1 \u0111\u1ed9ng trong ph\u1ea7n Auto.",
                "Mở profile",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        if (_session.IsRunning)
            return;

        SyncToConfig();
        _session.ApplyConfig(_config);

        if (!EnsureProxyOrConfirm())
            return;

        var openShopeeAccount = _config.OpenWithShopeeAccount;

        _startButton.Enabled = false;
        var form = FindForm();
        var prevCursor = form?.Cursor ?? Cursors.Default;
        if (form is not null)
            form.Cursor = Cursors.WaitCursor;
        _statusLabel.Text = "Đang khởi động Brave…";

        try
        {
            await _session.StartAsync(_getBraveExe(), _getSourceUserData()).ConfigureAwait(true);
            if (openShopeeAccount)
            {
                await _session.OpenShopeeAccountLoginAsync().ConfigureAwait(true);
                _config.OpenWithShopeeAccount = false;
                _openWithShopeeAccountCheck.Checked = false;
                UpdateShopeeAccountUi();
                ConfigChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(form, ex.Message, _config.DisplayName,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (form is not null)
                form.Cursor = prevCursor;
            UpdateStatusUi();
        }
    }

    private async Task CloseProfileAsync()
    {
        if (_session is null || !_session.IsRunning)
            return;

        var name = _config?.DisplayName ?? "Instance";
        if (MessageBox.Show(
                FindForm(),
                $"Dừng extension và đóng Brave cho \"{name}\"?",
                "Đóng profile",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        _closeProfileButton.Enabled = false;
        _startButton.Enabled = false;
        var form = FindForm();
        var prevCursor = form?.Cursor ?? Cursors.Default;
        if (form is not null)
            form.Cursor = Cursors.WaitCursor;

        try
        {
            await _session.StopAsync();
            RefreshProgressFromConfig();
            UpdateStatusUi();
        }
        catch (Exception ex)
        {
            MessageBox.Show(form, ex.Message, "Đóng profile", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (form is not null)
                form.Cursor = prevCursor;
            UpdateStatusUi();
        }
    }
    private void OpenProfileFolder()
    {
        if (_config is null) return;
        var path = BraveProfileManager.GetProfileRootDirectory(_config).FullName;
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
        });
    }

    /// <summary>Nạp lại danh sách sheet từ workbook (gọi khi mở form / chọn instance).</summary>
    public Task RefreshSheetsFromWorkbookAsync() => LoadSheetsAsync();

    private static void AddRowSpan(TableLayoutPanel table, string label, Control control, ref int row)
    {
        table.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Padding = new Padding(0, 6, 0, 0),
        }, 0, row);
        control.Dock = DockStyle.Fill;
        table.Controls.Add(control, 1, row);
        table.SetColumnSpan(control, 3);
        row++;
    }

    private static void AddDualRow(
        TableLayoutPanel table,
        string l1, Control c1,
        string l2, Control c2,
        ref int row)
    {
        table.Controls.Add(new Label { Text = l1, AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 0, row);
        c1.Dock = DockStyle.Fill;
        table.Controls.Add(c1, 1, row);
        table.Controls.Add(new Label { Text = l2, AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 2, row);
        c2.Dock = DockStyle.Fill;
        table.Controls.Add(c2, 3, row);
        row++;
    }

    private static void AddRow(TableLayoutPanel table, string label, Control control, ref int row)
    {
        table.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Padding = new Padding(0, 6, 0, 0),
        }, 0, row);
        table.Controls.Add(control, 1, row);
        row++;
    }

    private static void AddRunnerRow(TableLayoutPanel table, int row, string l1, Control c1, string? l2, Control? c2)
    {
        var labelPad = new Padding(0, 8, 0, 4);
        table.Controls.Add(new Label { Text = l1, AutoSize = true, Padding = labelPad, Margin = new Padding(0, 0, 0, 2) }, 0, row);
        c1.Dock = DockStyle.Fill;
        c1.Margin = new Padding(0, 4, 0, 6);
        table.Controls.Add(c1, 1, row);
        if (!string.IsNullOrEmpty(l2) && c2 is not null)
        {
            table.Controls.Add(new Label { Text = l2, AutoSize = true, Padding = labelPad, Margin = new Padding(0, 0, 0, 2) }, 2, row);
            c2.Dock = DockStyle.Fill;
            c2.Margin = new Padding(0, 4, 0, 6);
            table.Controls.Add(c2, 3, row);
        }
    }

    private static void AddCompactRunnerField(TableLayoutPanel table, int row, string label, Control control, int column)
    {
        table.Controls.Add(new Label
        {
            Text = label,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(0, 4, 4, 0),
            Margin = new Padding(0, 0, 0, 2),
        }, column, row);
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(0, 4, 8, 6);
        table.Controls.Add(control, column + 1, row);
    }

    /// <summary>Chọn sheet trong dropdown; nếu chưa có trong danh sách thì thêm vào để vẫn hiển thị.</summary>
    private void SetSheetSelection(string? sheet)
    {
        var name = sheet?.Trim() ?? "";
        if (name.Length == 0)
        {
            _sheetCombo.SelectedIndex = -1;
            return;
        }

        var idx = _sheetCombo.Items.IndexOf(name);
        if (idx < 0)
        {
            _sheetCombo.Items.Add(name);
            idx = _sheetCombo.Items.IndexOf(name);
        }
        _sheetCombo.SelectedIndex = idx;
    }

    /// <summary>Nạp danh sách sheet từ workbook qua API /sheets vào dropdown.</summary>
    private async Task LoadSheetsAsync()
    {
        if (!await ApiServerHelper.EnsureReachableAsync(tryAutoStart: true))
            return;

        List<string> names;
        try
        {
            names = await ApiServerHelper.GetSheetNamesAsync();
        }
        catch
        {
            return;
        }

        if (IsDisposed) return;

        var keep = _config?.DataSheet;
        _suppressSync = true;
        try
        {
            _sheetCombo.BeginUpdate();
            _sheetCombo.Items.Clear();
            foreach (var n in names)
                _sheetCombo.Items.Add(n);
            _sheetCombo.EndUpdate();
            SetSheetSelection(keep);
        }
        finally
        {
            _suppressSync = false;
        }
    }

    private static NumericUpDown MakeRowInput(int min) =>
        new()
        {
            Minimum = min,
            Maximum = 999_999,
            Width = 68,
            Height = 28,
            Dock = DockStyle.Left,
        };

    private static void SetNumeric(NumericUpDown control, int? value)
    {
        if (value is null || value < (int)control.Minimum)
        {
            control.Value = control.Minimum;
            return;
        }
        control.Value = Math.Min((int)control.Maximum, value.Value);
    }

    private static ComboBox MakeCombo(string defaultVal, params string[] items)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        c.Items.AddRange(items);
        c.SelectedItem = defaultVal;
        return c;
    }

    private static void SelectCombo(ComboBox combo, string value)
    {
        if (combo.Items.Contains(value))
            combo.SelectedItem = value;
        else if (combo.Items.Count > 0)
            combo.SelectedIndex = 0;
    }
}
