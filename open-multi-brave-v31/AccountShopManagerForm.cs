using System.Text.Json;

namespace OpenMultiBraveLauncherV3;

internal sealed class AccountShopManagerForm : Form
{
    private readonly List<AccountConfig> _accounts;
    private readonly ListBox _accountList = new();
    private readonly ListBox _shopList = new();
    private readonly TextBox _emailTextBox = new();
    private readonly TextBox _workbookTextBox = new();
    private readonly TextBox _shopNameTextBox = new();
    private readonly TextBox _sheetTextBox = new();
    private bool _suppressChanges;

    public AccountShopManagerForm(
        IReadOnlyList<AccountConfig> accounts,
        string activeAccountId,
        string activeShopId,
        string braveExe,
        string sourceUserData)
    {
        _accounts = Clone(accounts);
        if (_accounts.Count == 0)
            _accounts.Add(AccountConfig.CreateDefault());

        ActiveAccountId = activeAccountId;
        ActiveShopId = activeShopId;

        Text = "Account / shop";
        StartPosition = FormStartPosition.CenterParent;
        Width = 920;
        Height = 560;
        MinimumSize = new Size(780, 460);

        Controls.Add(CreateLayout());
        Load += (_, _) => RefreshLists();
    }

    public IReadOnlyList<AccountConfig> Accounts => _accounts;
    public string ActiveAccountId { get; private set; }
    public string ActiveShopId { get; private set; }

    private Control CreateLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 2,
            Padding = new Padding(10),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(CreateAccountPanel(), 0, 0);
        root.Controls.Add(CreateShopPanel(), 1, 0);
        root.Controls.Add(CreateDetailPanel(), 2, 0);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
        };
        var ok = new Button { Text = "OK", Width = 86, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Huy", Width = 86, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => SaveCurrent();
        actions.Controls.Add(ok);
        actions.Controls.Add(cancel);
        root.SetColumnSpan(actions, 3);
        root.Controls.Add(actions, 0, 1);
        AcceptButton = ok;
        CancelButton = cancel;

        return root;
    }

    private Control CreateAccountPanel()
    {
        var panel = CreatePanel("Account");
        _accountList.Dock = DockStyle.Fill;
        _accountList.SelectedIndexChanged += (_, _) => SelectAccount();

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Bottom };
        var add = new Button { Text = "Them", Width = 78 };
        var remove = new Button { Text = "Xoa", Width = 78 };
        add.Click += (_, _) => AddAccount();
        remove.Click += (_, _) => RemoveAccount();
        buttons.Controls.Add(add);
        buttons.Controls.Add(remove);

        panel.Controls.Add(_accountList);
        panel.Controls.Add(buttons);
        return panel;
    }

    private Control CreateShopPanel()
    {
        var panel = CreatePanel("Shop");
        _shopList.Dock = DockStyle.Fill;
        _shopList.SelectedIndexChanged += (_, _) => SelectShop();

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Bottom };
        var add = new Button { Text = "Them", Width = 78 };
        var remove = new Button { Text = "Xoa", Width = 78 };
        add.Click += (_, _) => AddShop();
        remove.Click += (_, _) => RemoveShop();
        buttons.Controls.Add(add);
        buttons.Controls.Add(remove);

        panel.Controls.Add(_shopList);
        panel.Controls.Add(buttons);
        return panel;
    }

    private Control CreateDetailPanel()
    {
        var group = CreatePanel("Chi tiet");
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 3,
            Padding = new Padding(8),
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        AddTextRow(table, 0, "Email", _emailTextBox, null);
        var browse = new Button { Text = "...", Width = 34 };
        browse.Click += (_, _) => BrowseWorkbook();
        AddTextRow(table, 1, "Workbook", _workbookTextBox, browse);
        AddTextRow(table, 2, "Ten shop", _shopNameTextBox, null);
        AddTextRow(table, 3, "Sheet Shopee", _sheetTextBox, null);

        foreach (var box in new[] { _emailTextBox, _workbookTextBox, _shopNameTextBox, _sheetTextBox })
            box.TextChanged += (_, _) => SaveCurrent();

        group.Controls.Add(table);
        return group;
    }

    private static GroupBox CreatePanel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        Padding = new Padding(8),
    };

    private static void AddTextRow(TableLayoutPanel table, int row, string label, TextBox box, Control? button)
    {
        box.Dock = DockStyle.Fill;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 0, row);
        table.Controls.Add(box, 1, row);
        if (button is null)
            table.SetColumnSpan(box, 2);
        else
            table.Controls.Add(button, 2, row);
    }

    private void RefreshLists()
    {
        _suppressChanges = true;
        try
        {
            _accountList.Items.Clear();
            foreach (var account in _accounts)
                _accountList.Items.Add(new SelectorItem(account.Id, account.DisplayName));

            var accountIndex = Math.Max(0, _accounts.FindIndex(a => a.Id == ActiveAccountId));
            if (_accountList.Items.Count > 0)
                _accountList.SelectedIndex = accountIndex;
            RefreshShopList();
            LoadCurrent();
        }
        finally
        {
            _suppressChanges = false;
        }
    }

    private void RefreshShopList()
    {
        _shopList.Items.Clear();
        var account = CurrentAccount;
        if (account is null)
            return;

        foreach (var shop in account.Shops)
            _shopList.Items.Add(new SelectorItem(shop.Id, shop.DisplayName));

        var shopIndex = Math.Max(0, account.Shops.FindIndex(s => s.Id == ActiveShopId));
        if (_shopList.Items.Count > 0)
            _shopList.SelectedIndex = shopIndex;
    }

    private AccountConfig? CurrentAccount =>
        _accountList.SelectedItem is SelectorItem item
            ? _accounts.FirstOrDefault(a => a.Id == item.Id)
            : null;

    private ShopConfig? CurrentShop =>
        CurrentAccount is { } account && _shopList.SelectedItem is SelectorItem item
            ? account.Shops.FirstOrDefault(s => s.Id == item.Id)
            : null;

    private void SelectAccount()
    {
        if (_suppressChanges)
            return;
        SaveCurrent();
        if (CurrentAccount is { } account)
        {
            ActiveAccountId = account.Id;
            ActiveShopId = account.Shops.FirstOrDefault()?.Id ?? "";
        }
        _suppressChanges = true;
        try
        {
            RefreshShopList();
            LoadCurrent();
        }
        finally
        {
            _suppressChanges = false;
        }
    }

    private void SelectShop()
    {
        if (_suppressChanges)
            return;
        SaveCurrent();
        if (CurrentShop is { } shop)
            ActiveShopId = shop.Id;
        LoadCurrent();
    }

    private void LoadCurrent()
    {
        _suppressChanges = true;
        try
        {
            var account = CurrentAccount;
            var shop = CurrentShop;
            _emailTextBox.Text = account?.Email ?? "";
            _workbookTextBox.Text = account?.WorkbookPath ?? "";
            _shopNameTextBox.Text = shop?.Name ?? "";
            _sheetTextBox.Text = shop?.ShopeeDataSheet ?? "";
        }
        finally
        {
            _suppressChanges = false;
        }
    }

    private void SaveCurrent()
    {
        if (_suppressChanges)
            return;

        if (CurrentAccount is { } account)
        {
            account.Email = _emailTextBox.Text.Trim();
            account.WorkbookPath = _workbookTextBox.Text.Trim();
            ActiveAccountId = account.Id;
        }

        if (CurrentShop is { } shop)
        {
            shop.Name = _shopNameTextBox.Text.Trim();
            shop.ShopeeDataSheet = _sheetTextBox.Text.Trim();
            ActiveShopId = shop.Id;
        }
    }

    private void AddAccount()
    {
        SaveCurrent();
        var account = AccountConfig.CreateDefault();
        account.Email = $"account{_accounts.Count + 1}@test.com";
        _accounts.Add(account);
        ActiveAccountId = account.Id;
        ActiveShopId = account.Shops[0].Id;
        RefreshLists();
    }

    private void RemoveAccount()
    {
        if (CurrentAccount is not { } account || _accounts.Count <= 1)
            return;
        _accounts.Remove(account);
        ActiveAccountId = _accounts[0].Id;
        ActiveShopId = _accounts[0].Shops[0].Id;
        RefreshLists();
    }

    private void AddShop()
    {
        SaveCurrent();
        if (CurrentAccount is not { } account)
            return;
        var shop = ShopConfig.CreateDefault();
        shop.Name = $"Shop {account.Shops.Count + 1}";
        account.Shops.Add(shop);
        ActiveShopId = shop.Id;
        RefreshLists();
    }

    private void RemoveShop()
    {
        if (CurrentAccount is not { } account || CurrentShop is not { } shop || account.Shops.Count <= 1)
            return;
        account.Shops.Remove(shop);
        ActiveShopId = account.Shops[0].Id;
        RefreshLists();
    }

    private void BrowseWorkbook()
    {
        using var ofd = new OpenFileDialog
        {
            Filter = "Excel files (*.xlsx;*.xlsm)|*.xlsx;*.xlsm|All files (*.*)|*.*",
            Title = "Chon workbook",
        };
        if (ofd.ShowDialog(this) == DialogResult.OK)
            _workbookTextBox.Text = ofd.FileName;
    }

    private static List<AccountConfig> Clone(IReadOnlyList<AccountConfig> source)
    {
        var json = JsonSerializer.Serialize(source);
        return JsonSerializer.Deserialize<List<AccountConfig>>(json) ?? [];
    }

    private sealed record SelectorItem(string Id, string Text)
    {
        public override string ToString() => Text;
    }
}
