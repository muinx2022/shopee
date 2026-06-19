namespace OpenMultiBraveLauncherV3;

internal sealed class AccountWorkspaceControl : UserControl
{
    private readonly LauncherSettingsFile _settings;
    private readonly string _accountId;
    private readonly ShopeeWorkspaceControl _shopeeWorkspace;
    private TabControl? _tabs;

    public AccountWorkspaceControl(LauncherSettingsFile settings, string accountId)
    {
        _settings = settings;
        _accountId = accountId;
        Dock = DockStyle.Fill;

        _shopeeWorkspace = new ShopeeWorkspaceControl(_settings, _accountId)
        {
            Dock = DockStyle.Fill,
        };
        _shopeeWorkspace.RunningStateChanged += NotifyRunningStateChanged;

        _tabs = new TabControl { Dock = DockStyle.Fill };
        var tabs = _tabs;

        var shopeeTab = new TabPage("Shopee");
        shopeeTab.Controls.Add(_shopeeWorkspace);

        var settingsTab = new TabPage("Cài đặt");
        var settingsLoaded = false;
        var loadingLabel = new Label
        {
            Text = "Đang tải...",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(Font, FontStyle.Italic),
            ForeColor = SystemColors.GrayText,
        };
        settingsTab.Controls.Add(loadingLabel);

        tabs.TabPages.Add(shopeeTab);
        tabs.TabPages.Add(settingsTab);

        tabs.SelectedIndexChanged += (_, _) =>
        {
            if (tabs.SelectedTab == settingsTab && !settingsLoaded)
            {
                settingsLoaded = true;
                settingsTab.Controls.Remove(loadingLabel);
                loadingLabel.Dispose();
                settingsTab.Controls.Add(new AccountSettingsPanel(_settings, _accountId) { Dock = DockStyle.Fill });
            }
        };

        Controls.Add(tabs);
    }

    public void SelectSettingsTab() => _tabs?.SelectTab(1);

    public string AccountId => _accountId;

    public string AccountTitle => ActiveAccount.DisplayName;

    public bool HasRunningWork => _shopeeWorkspace.HasRunningWork;

    public event Action? RunningStateChanged;

    public async Task ShutdownAsync()
    {
        await _shopeeWorkspace.ShutdownAsync().ConfigureAwait(true);
    }

    /// <summary>Đóng đồng bộ — dùng khi thoát app (không cần spinner, tránh deadlock do block UI thread).</summary>
    public void Shutdown() => _shopeeWorkspace.Shutdown();

    private AccountConfig ActiveAccount =>
        _settings.Accounts.FirstOrDefault(a => a.Id == _accountId)
        ?? _settings.Accounts.First();

    private void NotifyRunningStateChanged() => RunningStateChanged?.Invoke();
}
