using System.Text.Json;

namespace OpenMultiBraveLauncherV3;

internal sealed class ManagerForm : Form
{
    private readonly LauncherSettingsFile _settings;
    private readonly Dictionary<string, TabPage> _openWorkspaces = new(StringComparer.Ordinal);
    private readonly SplitContainer _split;
    private readonly ListView _accountList;
    private readonly TabControl _workspaceTabs;
    private readonly ImageList _accountStatusImages;
    private bool _syncingSelection;

    public ManagerForm()
    {
        _settings = LauncherSettings.LoadOrCreate();
        _accountStatusImages = CreateAccountStatusImages();

        Text = "Multi Brave Manager v31";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 1680;
        Height = 1320;
        MinimumSize = new Size(1500, 1180);

        _split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 6,
        };

        _accountList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = false,
            SmallImageList = _accountStatusImages,
        };
        _accountList.Columns.Add("Account", 160);
        _accountList.Columns.Add("Shop", 92);
        _accountList.Columns.Add("TT", 64);
        _accountList.DoubleClick += (_, _) => OpenSelectedAccount(goToSettings: true);

        _workspaceTabs = new TabControl
        {
            Dock = DockStyle.Fill,
            DrawMode = TabDrawMode.OwnerDrawFixed,
            Padding = new Point(18, 4),
        };
        _workspaceTabs.DrawItem += DrawWorkspaceTab;
        _workspaceTabs.MouseDown += OnWorkspaceTabMouseDown;
        _workspaceTabs.SelectedIndexChanged += (_, _) =>
        {
            if (_syncingSelection) return;
            if (_workspaceTabs.SelectedTab?.Tag is string accountId)
            {
                _syncingSelection = true;
                try { SelectAccountInList(accountId); }
                finally { _syncingSelection = false; }
            }
        };
        _accountList.SelectedIndexChanged += (_, _) =>
        {
            UpdateAccountIcons();
            if (_syncingSelection) return;
            if (SelectedAccountId is { } accountId && _openWorkspaces.TryGetValue(accountId, out var page))
            {
                _syncingSelection = true;
                try { _workspaceTabs.SelectedTab = page; }
                finally { _syncingSelection = false; }
            }
        };

        _split.Panel1.Controls.Add(CreateNavPanel());
        _split.Panel2.Controls.Add(_workspaceTabs);
        Controls.Add(_split);

        Load += (_, _) => RefreshAccounts();
        Shown += (_, _) => ApplySplitLayout();
        Resize += (_, _) => ApplySplitLayout();
        FormClosing += (_, e) =>
        {
            AppDiagnostics.Log($"ManagerForm closing. Reason={e.CloseReason}, tabs={_workspaceTabs.TabPages.Count}");
            ShutdownAllWorkspaces();
        };
    }

    private void ApplySplitLayout()
    {
        if (_split.Width <= 0)
            return;

        var panel1Min = 260;
        var panel2Min = 1120;
        if (_split.Width <= panel1Min + panel2Min + _split.SplitterWidth)
        {
            panel1Min = 120;
            panel2Min = 240;
        }

        _split.Panel1MinSize = panel1Min;
        _split.Panel2MinSize = panel2Min;

        var maxLeft = Math.Max(panel1Min, _split.Width - panel2Min - _split.SplitterWidth);
        var left = Math.Clamp(300, panel1Min, maxLeft);
        try
        {
            if (Math.Abs(_split.SplitterDistance - left) > 2)
                _split.SplitterDistance = left;
        }
        catch
        {
        }
    }

    private Control CreateNavPanel()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 4,
            ColumnCount = 1,
            Padding = new Padding(8),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = new Label
        {
            Text = "Account",
            Dock = DockStyle.Fill,
            Font = new Font(Font, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 4),
        };
        root.Controls.Add(title, 0, 0);

        // Right-click selects item before menu opens
        _accountList.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            var hit = _accountList.GetItemAt(e.X, e.Y);
            if (hit is not null) { hit.Selected = true; hit.Focused = true; }
        };
        _accountList.ContextMenuStrip = BuildAccountContextMenu();
        root.Controls.Add(_accountList, 0, 1);

        // [+] [−] compact toolbar
        var listBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 3, 0, 4),
        };
        var btnAdd = CreateIconButton("+  Thêm Acc", "Thêm account", Color.FromArgb(46, 160, 67), Color.FromArgb(232, 245, 233));
        btnAdd.Click += (_, _) => AddAccount();
        var btnRemove = CreateIconButton("−  Xóa Acc", "Xóa account", Color.FromArgb(200, 70, 60), Color.FromArgb(250, 235, 233));
        btnRemove.Click += (_, _) => RemoveAccount();
        listBar.Controls.Add(btnAdd);
        listBar.Controls.Add(btnRemove);
        root.Controls.Add(listBar, 0, 2);

        // Action buttons
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            WrapContents = true,
            Padding = new Padding(0, 4, 0, 0),
        };
        var runButton = CreateButton("Chạy", 90);
        runButton.Click += (_, _) => OpenSelectedAccount(goToSettings: false);
        var settingsButton = CreateButton("Cài đặt", 90);
        settingsButton.Click += (_, _) => ShowGlobalSettings();
        buttons.Controls.Add(runButton);
        buttons.Controls.Add(settingsButton);
        root.Controls.Add(buttons, 0, 3);

        return root;
    }

    private ContextMenuStrip BuildAccountContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Mở workspace", null, (_, _) => OpenSelectedAccount(goToSettings: false));
        menu.Items.Add("Cài đặt account", null, (_, _) => OpenSelectedAccount(goToSettings: true));
        menu.Items.Add("Đổi tên...", null, (_, _) => RenameAccount());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Thêm account mới", null, (_, _) => AddAccount());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Xóa", null, (_, _) => RemoveAccount());
        return menu;
    }

    private void RenameAccount()
    {
        var accountId = SelectedAccountId;
        if (string.IsNullOrWhiteSpace(accountId)) return;
        var account = _settings.Accounts.FirstOrDefault(a => a.Id == accountId);
        if (account is null) return;

        var newEmail = PromptText("Đổi tên account", "Email account:", account.Email);
        if (newEmail is null) return;

        account.Email = newEmail;
        LauncherSettings.Save(_settings);
        RefreshAccounts();

        if (_openWorkspaces.TryGetValue(accountId, out var page))
            page.Text = account.DisplayName;
    }

    private static Button CreateButton(string text, int width) => new()
    {
        Text = text,
        Width = width,
        Height = 32,
        Margin = new Padding(0, 0, 6, 6),
    };

    private readonly ToolTip _navToolTip = new();

    private Button CreateIconButton(string text, string tooltip, Color accentColor, Color hoverColor)
    {
        var btn = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Height = 28,
            FlatStyle = FlatStyle.Flat,
            ForeColor = accentColor,
            BackColor = Color.FromArgb(248, 249, 250),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            Padding = new Padding(8, 2, 8, 2),
            Margin = new Padding(0, 0, 5, 0),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
            UseCompatibleTextRendering = false,
        };
        btn.FlatAppearance.BorderColor = Color.FromArgb(214, 217, 222);
        btn.FlatAppearance.BorderSize = 1;
        btn.FlatAppearance.MouseOverBackColor = hoverColor;
        btn.FlatAppearance.MouseDownBackColor = hoverColor;
        _navToolTip.SetToolTip(btn, tooltip);
        return btn;
    }

    private void RefreshAccounts()
    {
        var previousSelectedId = SelectedAccountId;
        _accountList.Items.Clear();
        foreach (var account in _settings.Accounts)
        {
            var shopName = account.Shops.FirstOrDefault(s => s.Id == _settings.ActiveShopId)?.DisplayName
                ?? account.Shops.FirstOrDefault()?.DisplayName
                ?? "";
            var isOpen = _openWorkspaces.ContainsKey(account.Id);
            var isRunning = IsAccountRunning(account.Id);
            var baseStatus = isRunning ? "running" : isOpen ? "open" : "idle";
            var item = new ListViewItem(account.DisplayName)
            {
                Name = baseStatus,                 // lưu status gốc để ghép với mũi tên
                ImageKey = $"no_{baseStatus}",
            };
            item.SubItems.Add(shopName);
            item.SubItems.Add(isRunning ? "Chạy" : isOpen ? "Mở" : "");
            item.Tag = account.Id;
            _accountList.Items.Add(item);
        }

        // Ưu tiên giữ selection theo tab workspace đang mở, rồi tới account đang chọn trước đó.
        var targetId = (_workspaceTabs.SelectedTab?.Tag as string) ?? previousSelectedId;
        if (string.IsNullOrWhiteSpace(targetId) || !SelectAccountInList(targetId))
        {
            if (_accountList.Items.Count > 0 && _accountList.SelectedItems.Count == 0)
                _accountList.Items[0].Selected = true;
        }

        UpdateAccountIcons();
    }

    private void UpdateAccountIcons()
    {
        foreach (ListViewItem item in _accountList.Items)
        {
            var baseStatus = string.IsNullOrEmpty(item.Name) ? "idle" : item.Name;
            item.ImageKey = $"{(item.Selected ? "sel" : "no")}_{baseStatus}";
        }
    }

    private bool SelectAccountInList(string accountId)
    {
        foreach (ListViewItem item in _accountList.Items)
        {
            if (item.Tag as string == accountId)
            {
                if (!item.Selected)
                {
                    _accountList.SelectedItems.Clear();
                    item.Selected = true;
                }
                item.Focused = true;
                item.EnsureVisible();
                return true;
            }
        }
        return false;
    }

    private string? SelectedAccountId =>
        _accountList.SelectedItems.Count == 0
            ? null
            : _accountList.SelectedItems[0].Tag as string;

    private void OpenSelectedAccount(bool goToSettings = false)
    {
        var accountId = SelectedAccountId;
        if (string.IsNullOrWhiteSpace(accountId))
            return;

        AccountWorkspaceControl? workspace;

        if (_openWorkspaces.TryGetValue(accountId, out var existing))
        {
            _workspaceTabs.SelectedTab = existing;
            workspace = existing.Controls.OfType<AccountWorkspaceControl>().FirstOrDefault();
        }
        else
        {
            var account = _settings.Accounts.FirstOrDefault(a => a.Id == accountId);
            if (account is null) return;

            _settings.ActiveAccountId = account.Id;
            var activeShop = account.Shops.FirstOrDefault(s => s.Id == _settings.ActiveShopId)
                          ?? account.Shops.FirstOrDefault();
            if (activeShop is not null)
                _settings.ActiveShopId = activeShop.Id;

            workspace = new AccountWorkspaceControl(_settings, account.Id);
            workspace.RunningStateChanged += () => SafeBeginInvoke(RefreshAccounts);
            var page = new TabPage(account.DisplayName) { Tag = account.Id };
            page.Controls.Add(workspace);
            _workspaceTabs.TabPages.Add(page);
            _workspaceTabs.SelectedTab = page;
            _openWorkspaces[account.Id] = page;
            RefreshAccounts();
        }

        if (goToSettings)
            workspace?.SelectSettingsTab();
    }

    private async Task CloseWorkspaceAsync(TabPage page)
    {
        if (page.Tag is not string accountId)
            return;

        var workspace = page.Controls.OfType<AccountWorkspaceControl>().FirstOrDefault();
        var running = workspace?.HasRunningWork == true;
        var message = running
            ? $"\"{page.Text}\" đang chạy. Đóng tab sẽ dừng tiến trình. Tiếp tục?"
            : $"Đóng tab \"{page.Text}\"?";
        var confirm = MessageBox.Show(
            this, message, "Đóng tab",
            MessageBoxButtons.YesNo,
            running ? MessageBoxIcon.Warning : MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.Yes)
            return;

        var busy = new BusyDialog("Đang đóng profile, vui lòng chờ…");
        busy.Show(this);
        Enabled = false;
        // Giữ hộp thoại hiện ít nhất ~1.5s để không bị nháy khi đóng nhanh.
        var minDisplay = Task.Delay(1500);
        try
        {
            if (workspace is not null)
                await workspace.ShutdownAsync().ConfigureAwait(true);

            _openWorkspaces.Remove(accountId);
            _workspaceTabs.TabPages.Remove(page);
            page.Dispose();
            RefreshAccounts();

            await minDisplay.ConfigureAwait(true);
        }
        finally
        {
            // Chỉ đóng dialog sau khi profile + tab đã đóng xong.
            Enabled = true;
            busy.Close();
            busy.Dispose();
            Activate();
        }
    }

    private bool IsAccountRunning(string accountId)
    {
        if (!_openWorkspaces.TryGetValue(accountId, out var page))
            return false;

        return page.Controls.OfType<AccountWorkspaceControl>().FirstOrDefault()?.HasRunningWork == true;
    }

    private void SafeBeginInvoke(Action action)
    {
        if (IsDisposed || !IsHandleCreated)
            return;

        if (InvokeRequired)
            BeginInvoke(action);
        else
            action();
    }

    private static ImageList CreateAccountStatusImages()
    {
        var images = new ImageList
        {
            ColorDepth = ColorDepth.Depth32Bit,
            ImageSize = new Size(28, 16),
        };
        // 6 biến thể: {active}_{status}. active = acc đang chọn (có mũi tên ►).
        var statuses = new (string Key, Color Fill, Color Border)[]
        {
            ("idle", Color.Transparent, Color.Transparent),
            ("open", Color.FromArgb(175, 180, 188), Color.FromArgb(145, 150, 158)),
            ("running", Color.FromArgb(46, 204, 113), Color.FromArgb(30, 150, 80)),
        };
        foreach (var s in statuses)
        {
            images.Images.Add($"no_{s.Key}", CreateStatusIcon(false, s.Fill, s.Border));
            images.Images.Add($"sel_{s.Key}", CreateStatusIcon(true, s.Fill, s.Border));
        }
        return images;
    }

    private static Bitmap CreateStatusIcon(bool active, Color fill, Color border)
    {
        var bitmap = new Bitmap(28, 16);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        // Mũi tên ► bên trái cho acc đang active
        if (active)
        {
            using var arrowBrush = new SolidBrush(Color.FromArgb(60, 120, 215));
            graphics.FillPolygon(arrowBrush, new[]
            {
                new Point(1, 3),
                new Point(9, 8),
                new Point(1, 13),
            });
        }

        // Chấm trạng thái bên phải
        if (fill.A != 0)
        {
            using var brush = new SolidBrush(fill);
            using var pen = new Pen(border);
            graphics.FillEllipse(brush, 15, 3, 10, 10);
            graphics.DrawEllipse(pen, 15, 3, 10, 10);
        }
        return bitmap;
    }

    private void DrawWorkspaceTab(object? sender, DrawItemEventArgs e)
    {
        var page = _workspaceTabs.TabPages[e.Index];
        var rect = _workspaceTabs.GetTabRect(e.Index);
        var selected = e.Index == _workspaceTabs.SelectedIndex;
        using var back = new SolidBrush(selected ? Color.White : Color.FromArgb(238, 241, 245));
        e.Graphics.FillRectangle(back, rect);
        TextRenderer.DrawText(
            e.Graphics,
            page.Text,
            Font,
            new Rectangle(rect.Left + 8, rect.Top + 3, rect.Width - 28, rect.Height - 6),
            Color.FromArgb(30, 35, 45),
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(
            e.Graphics,
            "x",
            Font,
            new Rectangle(rect.Right - 20, rect.Top + 3, 16, rect.Height - 6),
            Color.FromArgb(90, 95, 105),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private async void OnWorkspaceTabMouseDown(object? sender, MouseEventArgs e)
    {
        for (var i = 0; i < _workspaceTabs.TabPages.Count; i++)
        {
            var rect = _workspaceTabs.GetTabRect(i);
            var closeRect = new Rectangle(rect.Right - 22, rect.Top + 4, 18, rect.Height - 8);
            if (closeRect.Contains(e.Location))
            {
                await CloseWorkspaceAsync(_workspaceTabs.TabPages[i]).ConfigureAwait(true);
                return;
            }
        }
    }

    private void ShowAccountShopManager()
    {
        var activeAccountId = SelectedAccountId ?? _settings.ActiveAccountId;
        var activeAccount = _settings.Accounts.FirstOrDefault(a => a.Id == activeAccountId) ?? _settings.Accounts[0];
        var activeShopId = activeAccount.Shops.Any(s => s.Id == _settings.ActiveShopId)
            ? _settings.ActiveShopId
            : activeAccount.Shops[0].Id;

        using var form = new AccountShopManagerForm(
            _settings.Accounts,
            activeAccount.Id,
            activeShopId,
            _settings.BraveExe,
            _settings.SourceUserData);

        if (form.ShowDialog(this) != DialogResult.OK)
            return;

        _settings.Accounts = Clone(form.Accounts).ToList();
        _settings.ActiveAccountId = form.ActiveAccountId;
        _settings.ActiveShopId = form.ActiveShopId;
        LauncherSettings.Save(_settings);
        RefreshAccounts();
    }

    private void AddAccount()
    {
        var email = PromptText("Thêm account", "Email account:", $"account{_settings.Accounts.Count + 1}@email.com");
        if (email is null) return;

        var account = new AccountConfig { Email = email };
        _settings.Accounts.Add(account);
        _settings.ActiveAccountId = account.Id;
        _settings.ActiveShopId = "";
        LauncherSettings.Save(_settings);
        RefreshAccounts();

        // Chọn account vừa tạo và mở thẳng tab Cài đặt
        foreach (ListViewItem item in _accountList.Items)
        {
            if (item.Tag as string == account.Id)
            {
                item.Selected = true;
                item.Focused = true;
                break;
            }
        }
        OpenSelectedAccount(goToSettings: true);
    }

    private string? PromptText(string title, string label, string defaultValue = "")
    {
        using var dlg = new Form
        {
            Text = title,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(340, 118),
        };

        var lbl = new Label
        {
            Text = label,
            AutoSize = true,
            Location = new Point(12, 14),
        };
        var box = new TextBox
        {
            Text = defaultValue,
            Location = new Point(12, 36),
            Width = 316,
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
        };
        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Width = 80,
            Height = 28,
            Location = new Point(248, 76),
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
        };
        var cancel = new Button
        {
            Text = "Hủy",
            DialogResult = DialogResult.Cancel,
            Width = 80,
            Height = 28,
            Location = new Point(160, 76),
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
        };

        dlg.Controls.AddRange([lbl, box, ok, cancel]);
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;
        dlg.ActiveControl = box;
        box.SelectAll();

        if (dlg.ShowDialog(this) != DialogResult.OK) return null;
        var result = box.Text.Trim();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private void RemoveAccount()
    {
        var accountId = SelectedAccountId;
        if (string.IsNullOrWhiteSpace(accountId)) return;
        if (_settings.Accounts.Count <= 1)
        {
            MessageBox.Show("Phải có ít nhất 1 account.", "Xóa account", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var account = _settings.Accounts.FirstOrDefault(a => a.Id == accountId);
        if (account is null) return;

        var confirm = MessageBox.Show(
            $"Xóa account \"{account.DisplayName}\"?",
            "Xóa account",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        if (_openWorkspaces.TryGetValue(accountId, out var page))
            _ = CloseWorkspaceAsync(page);

        _settings.Accounts.Remove(account);
        _settings.ActiveAccountId = _settings.Accounts[0].Id;
        _settings.ActiveShopId = _settings.Accounts[0].Shops[0].Id;
        LauncherSettings.Save(_settings);
        RefreshAccounts();
    }

    private void ShowGlobalSettings()
    {
        using var dialog = new Form
        {
            Text = "Cài đặt chung",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(720, 170),
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 3,
            Padding = new Padding(10),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var braveBox = new TextBox { Text = _settings.BraveExe, Dock = DockStyle.Fill };
        var userDataBox = new TextBox { Text = _settings.SourceUserData, Dock = DockStyle.Fill };
        var braveBrowse = CreateButton("...", 34);
        braveBrowse.Click += (_, _) =>
        {
            using var ofd = new OpenFileDialog { Filter = "brave.exe|brave.exe|Exe|*.exe|All|*.*" };
            if (ofd.ShowDialog(dialog) == DialogResult.OK)
                braveBox.Text = ofd.FileName;
        };
        var userBrowse = CreateButton("...", 34);
        userBrowse.Click += (_, _) =>
        {
            using var fbd = new FolderBrowserDialog();
            if (fbd.ShowDialog(dialog) == DialogResult.OK)
                userDataBox.Text = fbd.SelectedPath;
        };
        layout.Controls.Add(new Label { Text = "Brave exe", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 0, 0);
        layout.Controls.Add(braveBox, 1, 0);
        layout.Controls.Add(braveBrowse, 2, 0);
        layout.Controls.Add(new Label { Text = "User Data", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 0, 1);
        layout.Controls.Add(userDataBox, 1, 1);
        layout.Controls.Add(userBrowse, 2, 1);

        var actions = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
        var ok = CreateButton("OK", 82);
        ok.DialogResult = DialogResult.OK;
        var cancel = CreateButton("Hủy", 82);
        cancel.DialogResult = DialogResult.Cancel;
        actions.Controls.Add(ok);
        actions.Controls.Add(cancel);
        layout.SetColumnSpan(actions, 3);
        layout.Controls.Add(actions, 0, 2);
        dialog.Controls.Add(layout);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        _settings.BraveExe = braveBox.Text.Trim();
        _settings.SourceUserData = userDataBox.Text.Trim();
        LauncherSettings.Save(_settings);
    }

    private void ShutdownAllWorkspaces()
    {
        foreach (var page in _workspaceTabs.TabPages.Cast<TabPage>().ToList())
        {
            if (page.Controls.OfType<AccountWorkspaceControl>().FirstOrDefault() is { } workspace)
                workspace.Shutdown();
        }
    }

    private static IReadOnlyList<T> Clone<T>(IReadOnlyList<T> source)
    {
        var json = JsonSerializer.Serialize(source);
        return JsonSerializer.Deserialize<List<T>>(json) ?? [];
    }
}
