namespace OpenMultiBraveLauncherV3;

internal sealed class AccountWorkspaceControl : UserControl
{
    private readonly LauncherSettingsFile _settings;
    private readonly string _accountId;
    private readonly ShopeeWorkspaceControl _shopeeWorkspace;

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
        Controls.Add(_shopeeWorkspace);
    }

    public string AccountId => _accountId;

    public string AccountTitle => ActiveAccount.DisplayName;

    public bool HasRunningWork => _shopeeWorkspace.HasRunningWork;

    public event Action? RunningStateChanged;

    public async Task ShutdownAsync()
    {
        await _shopeeWorkspace.ShutdownAsync().ConfigureAwait(true);
    }

    private AccountConfig ActiveAccount =>
        _settings.Accounts.FirstOrDefault(a => a.Id == _accountId)
        ?? _settings.Accounts.First();

    private void NotifyRunningStateChanged() => RunningStateChanged?.Invoke();
}
