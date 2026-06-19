namespace UpdateProduct;

internal sealed class MainForm : Form
{
    private readonly UpdateProductSettingsFile _settings;
    private readonly Panel _contentHost = new() { Dock = DockStyle.Fill };
    private readonly Label _contextLabel = new() { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Dictionary<ActionViewKind, Button> _navButtons = [];
    private readonly Dictionary<ActionViewKind, Control> _views = [];
    private Control? _activeView;
    private ActionViewKind _activeKind = ActionViewKind.Settings;

    public MainForm()
    {
        _settings = UpdateProductSettings.LoadOrCreate();

        Text = "Update Product";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 1360;
        Height = 860;
        MinimumSize = new Size(1120, 720);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(root);

        root.Controls.Add(BuildNav(), 0, 0);
        root.Controls.Add(BuildRightHost(), 1, 0);

        FormClosing += async (_, _) =>
        {
            foreach (var view in _views.Values)
            {
                if (view is IAsyncShutdown asyncShutdown)
                    await asyncShutdown.ShutdownAsync().ConfigureAwait(true);
            }
            UpdateProductSettings.Save(_settings);
        };

        ShowView(ActionViewKind.Settings);
    }

    private Control BuildNav()
    {
        var nav = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(245, 247, 250),
            Padding = new Padding(10),
            ColumnCount = 1,
            RowCount = 7,
        };
        nav.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        nav.RowStyles.Add(new RowStyle(SizeType.Absolute, 12));
        nav.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        nav.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        nav.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        nav.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        nav.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        nav.Controls.Add(new Label
        {
            Text = "Update Product",
            Font = new Font(Font.FontFamily, 13f, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8),
        }, 0, 0);

        AddNavButton(nav, ActionViewKind.Settings, "Cau hinh", 2);
        AddNavButton(nav, ActionViewKind.Import, "Import to store", 3);
        AddNavButton(nav, ActionViewKind.UpdateProduct, "Update product", 4);
        AddNavButton(nav, ActionViewKind.UpdateName, "Update product name", 5);
        return nav;
    }

    private Control BuildRightHost()
    {
        var right = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(12),
        };
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.Controls.Add(_contextLabel, 0, 0);
        right.Controls.Add(_contentHost, 0, 1);
        return right;
    }

    private void AddNavButton(TableLayoutPanel nav, ActionViewKind kind, string text, int row)
    {
        var button = new Button
        {
            Text = text,
            Dock = DockStyle.Top,
            Height = 38,
            FlatStyle = FlatStyle.Flat,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 0, 0, 8),
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(210, 214, 220);
        button.Click += (_, _) => ShowView(kind);
        _navButtons[kind] = button;
        nav.Controls.Add(button, 0, row);
    }

    private void ShowView(ActionViewKind kind)
    {
        _contentHost.Controls.Clear();
        _activeKind = kind;
        _activeView = GetOrCreateView(kind);

        _activeView.Dock = DockStyle.Fill;
        _contentHost.Controls.Add(_activeView);
        RefreshContext();
        RefreshNav();
    }

    private Control GetOrCreateView(ActionViewKind kind)
    {
        if (_views.TryGetValue(kind, out var view))
            return view;

        view = kind switch
        {
            ActionViewKind.Settings => new SettingsView(_settings, OnSettingsChanged),
            ActionViewKind.Import => new BigSellerActionView(_settings, BigSellerActionKind.Import, OnSettingsChanged),
            ActionViewKind.UpdateProduct => new BigSellerActionView(_settings, BigSellerActionKind.UpdateProduct, OnSettingsChanged),
            ActionViewKind.UpdateName => new BigSellerActionView(_settings, BigSellerActionKind.UpdateName, OnSettingsChanged),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
        _views[kind] = view;
        return view;
    }

    private void OnSettingsChanged()
    {
        UpdateProductSettings.Save(_settings);
        RefreshContext();
        foreach (var view in _views.Values.OfType<BigSellerActionView>())
            view.ReloadContext();
    }

    private void RefreshContext()
    {
        UpdateProductSettings.Normalize(_settings);
        var account = BigSellerContextFactory.ActiveAccount(_settings);
        var shop = BigSellerContextFactory.ActiveShop(_settings);
        _contextLabel.Text = $"Account: {account.DisplayName}    |    Shop: {shop.DisplayName}    |    Settings: {UpdateProductSettings.SettingsPath}";
    }

    private void RefreshNav()
    {
        foreach (var pair in _navButtons)
        {
            var selected = pair.Key == _activeKind;
            pair.Value.BackColor = selected ? Color.FromArgb(32, 91, 166) : Color.White;
            pair.Value.ForeColor = selected ? Color.White : Color.FromArgb(32, 38, 46);
        }
    }
}

internal interface IAsyncShutdown
{
    Task ShutdownAsync();
}
