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

    public ManagerForm()
    {
        _settings = LauncherSettings.LoadOrCreate();
        _accountStatusImages = CreateAccountStatusImages();

        Text = "Multi Brave Manager v31";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 1480;
        Height = 1080;
        MinimumSize = new Size(1180, 900);

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
        _accountList.DoubleClick += (_, _) => OpenSelectedAccount();

        _workspaceTabs = new TabControl
        {
            Dock = DockStyle.Fill,
            DrawMode = TabDrawMode.OwnerDrawFixed,
            Padding = new Point(18, 4),
        };
        _workspaceTabs.DrawItem += DrawWorkspaceTab;
        _workspaceTabs.MouseDown += OnWorkspaceTabMouseDown;

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
        var panel2Min = 520;
        if (_split.Width <= panel1Min + panel2Min + _split.SplitterWidth)
        {
            panel1Min = 120;
            panel2Min = 240;
        }

        _split.Panel1MinSize = panel1Min;
        _split.Panel2MinSize = panel2Min;

        var maxLeft = Math.Max(panel1Min, _split.Width - panel2Min - _split.SplitterWidth);
        var left = Math.Clamp(320, panel1Min, maxLeft);
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
            RowCount = 3,
            ColumnCount = 1,
            Padding = new Padding(8),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = new Label
        {
            Text = "Account / shop",
            Dock = DockStyle.Fill,
            Font = new Font(Font, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8),
        };
        root.Controls.Add(title, 0, 0);
        root.Controls.Add(_accountList, 0, 1);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            WrapContents = true,
            Padding = new Padding(0, 8, 0, 0),
        };

        var runButton = CreateButton("Chạy", 90);
        runButton.Click += (_, _) => OpenSelectedAccount();
        var manageButton = CreateButton("Account / shop", 118);
        manageButton.Click += (_, _) => ShowAccountShopManager();
        var settingsButton = CreateButton("Cài đặt", 90);
        settingsButton.Click += (_, _) => ShowGlobalSettings();
        buttons.Controls.Add(runButton);
        buttons.Controls.Add(manageButton);
        buttons.Controls.Add(settingsButton);
        root.Controls.Add(buttons, 0, 2);

        return root;
    }

    private static Button CreateButton(string text, int width) => new()
    {
        Text = text,
        Width = width,
        Height = 32,
        Margin = new Padding(0, 0, 6, 6),
    };

    private void RefreshAccounts()
    {
        _accountList.Items.Clear();
        foreach (var account in _settings.Accounts)
        {
            var shopName = account.Shops.FirstOrDefault(s => s.Id == _settings.ActiveShopId)?.DisplayName
                ?? account.Shops.FirstOrDefault()?.DisplayName
                ?? "";
            var isOpen = _openWorkspaces.ContainsKey(account.Id);
            var isRunning = IsAccountRunning(account.Id);
            var item = new ListViewItem(account.DisplayName)
            {
                ImageKey = isRunning ? "running" : isOpen ? "open" : "idle",
            };
            item.SubItems.Add(shopName);
            item.SubItems.Add(isRunning ? "Chạy" : isOpen ? "Mở" : "");
            item.Tag = account.Id;
            _accountList.Items.Add(item);
        }

        if (_accountList.Items.Count > 0 && _accountList.SelectedItems.Count == 0)
            _accountList.Items[0].Selected = true;
    }

    private string? SelectedAccountId =>
        _accountList.SelectedItems.Count == 0
            ? null
            : _accountList.SelectedItems[0].Tag as string;

    private void OpenSelectedAccount()
    {
        var accountId = SelectedAccountId;
        if (string.IsNullOrWhiteSpace(accountId))
            return;

        if (_openWorkspaces.TryGetValue(accountId, out var existing))
        {
            _workspaceTabs.SelectedTab = existing;
            return;
        }

        var account = _settings.Accounts.FirstOrDefault(a => a.Id == accountId);
        if (account is null)
            return;

        _settings.ActiveAccountId = account.Id;
        if (!account.Shops.Any(s => s.Id == _settings.ActiveShopId))
            _settings.ActiveShopId = account.Shops.First().Id;

        var workspace = new AccountWorkspaceControl(_settings, account.Id);
        workspace.RunningStateChanged += () => SafeBeginInvoke(RefreshAccounts);
        var page = new TabPage(account.DisplayName)
        {
            Tag = account.Id,
        };
        page.Controls.Add(workspace);
        _workspaceTabs.TabPages.Add(page);
        _workspaceTabs.SelectedTab = page;
        _openWorkspaces[account.Id] = page;
        RefreshAccounts();
    }

    private async Task CloseWorkspaceAsync(TabPage page)
    {
        if (page.Tag is not string accountId)
            return;

        if (page.Controls.OfType<AccountWorkspaceControl>().FirstOrDefault() is { } workspace)
            await workspace.ShutdownAsync().ConfigureAwait(true);

        _openWorkspaces.Remove(accountId);
        _workspaceTabs.TabPages.Remove(page);
        page.Dispose();
        RefreshAccounts();
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
            ImageSize = new Size(14, 14),
        };
        images.Images.Add("idle", CreateStatusDot(Color.Transparent, Color.Transparent));
        images.Images.Add("open", CreateStatusDot(Color.FromArgb(175, 180, 188), Color.FromArgb(145, 150, 158)));
        images.Images.Add("running", CreateStatusDot(Color.FromArgb(38, 185, 92), Color.FromArgb(22, 128, 64)));
        return images;
    }

    private static Bitmap CreateStatusDot(Color fill, Color border)
    {
        var bitmap = new Bitmap(14, 14);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        if (fill.A == 0)
            return bitmap;

        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(fill);
        using var pen = new Pen(border);
        graphics.FillEllipse(brush, 3, 3, 8, 8);
        graphics.DrawEllipse(pen, 3, 3, 8, 8);
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
                workspace.ShutdownAsync().GetAwaiter().GetResult();
        }
    }

    private static IReadOnlyList<T> Clone<T>(IReadOnlyList<T> source)
    {
        var json = JsonSerializer.Serialize(source);
        return JsonSerializer.Deserialize<List<T>>(json) ?? [];
    }
}
