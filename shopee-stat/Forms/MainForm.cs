namespace ShopeeStatApp.Forms;

public sealed class MainForm : Form
{
    private sealed record KeywordComboItem(string Keyword, bool Used)
    {
        public override string ToString() => Used ? $"✓ {Keyword}" : Keyword;
    }

    // Services
    private readonly AppSettingsService _appSettings = new();
    private readonly SearchTaskStore _taskStore = new();
    private readonly EdgeManager _edge;
    private WebSocketServer? _ws;
    private SearchOrchestrator? _orchestrator;
    private CdpInputController? _cdpInput;
    private CancellationTokenSource? _searchCts;
    private long _currentTaskId;
    private long _lastFailedTaskId;

    // Tab 1 - Search
    private readonly ComboBox _keywordBox;
    private readonly TextBox _locationBox;
    private readonly ComboBox _accountCombo;
    private readonly Button _startBtn;
    private readonly Button _autoBtn;
    private readonly Button _resumeFailedBtn;
    private readonly Button _pauseBtn;
    private readonly Button _stopBtn;
    private readonly Button _exportBtn;
    private readonly Button _exportAllBtn;
    private readonly Button _clearKeywordSearchBtn;
    private readonly Button _openTestBtn;
    private readonly ToolTip _toolTip = new();
    private bool _isPaused;
    private readonly Label _statusLabel;
    private readonly Label _resultCountLabel;
    private readonly Spinner _searchSpinner;
    private readonly DataGridView _grid;
    private readonly TextBox _logBox;
    private readonly Label _wsStatusLabel;

    // Tab 1 - parallel auto-run
    private NumericUpDown? _lanesBox;
    private TabControl? _laneTabs;
    // Tab động theo từ khóa: 1 tab/keyword đang chạy, keyed theo laneId của worker đang giữ nó.
    private readonly Dictionary<int, LaneUi> _laneUi = [];
    private AutoRunCoordinator? _coordinator;
    // Từ khóa đang được lane crawl ngay lúc này (để hiện trạng thái "Đang tìm kiếm").
    private readonly HashSet<string> _activeKeywords = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _excelLock = new();
    private const string KeywordsExportDir = @"D:\shopee-stat\keywords";

    private sealed record LaneUi(int LaneId, TabPage Page, DataGridView Grid, Label Status, ResultCounter Counter);

    private sealed class ResultCounter { public int Count; }

    // Tab - Tìm theo file (chạy song song: mỗi file = 1 lane = 1 account)
    private readonly TextBox _fileBox;
    private readonly Button _chooseFileBtn;
    private readonly Button _clearFileListBtn;
    private readonly NumericUpDown _fileMinPriceBox;
    private readonly NumericUpDown _fileMinSoldFromBox;
    private readonly NumericUpDown _fileMinSoldToBox;
    private readonly ComboBox _fileCategoryCombo;
    private readonly Button _fileApplyFilterBtn;
    private readonly NumericUpDown _fileProcessBox;
    private const string AllCategoriesItem = "(Tất cả danh mục)";
    private readonly Button _fileRunBtn;
    private readonly Button _fileStopBtn;
    private readonly Button _fileExportBtn;
    private readonly Button _fileExportAllBtn;
    private readonly Button _fileRescanBtn;
    private readonly Button _clearFileSearchBtn;
    private readonly Button _fileUpdateCatBtn;
    private readonly TabControl _fileLaneTabs;
    private readonly TextBox _fileLogBox;
    private readonly Spinner _fileSpinner;
    private readonly Label _fileStatusLabel;
    private readonly List<string> _filePaths = [];
    private readonly List<FileLaneUi> _fileLaneUis = [];
    private FileRunCoordinator? _fileCoordinator;
    private ScannedShopStore? _fileShopStore;   // shared store for the current run (used by Rescan)
    private bool _fileRunning;
    private const string ShopExportDir = @"D:\shopee-stat\shops";

    // Tab - Danh mục (từ điển danh mục, tự upsert khi quét shop)
    private readonly DataGridView _categoriesGrid;
    private readonly DataGridView _categoryProductsGrid;
    private readonly Button _refreshCategoriesBtn;
    private readonly Button _updateCategoriesBtn;
    private readonly Button _updateFileCategoriesBtn;
    private readonly Spinner _categoriesSpinner;
    private readonly Label _categoriesCountLabel;
    private bool _updatingCategories;
    // Cập nhật danh mục bằng AI: file danh mục tham chiếu + OpenAI key.
    private const string CategoryDocxPath = @"D:\Projects\shopee-27052026\shopee-cat.docx";
    private const string OpenAiKeyPath = @"D:\Projects\shopee-27052026\openai.key";

    private sealed class FileLaneUi
    {
        public required TabPage Page;
        public required DataGridView LinksGrid;
        public required DataGridView ProductsGrid;
        public required Label Status;
        public string FilePath = "";
        // Shop mà lưới sản phẩm bên phải ĐANG hiển thị (live crawl hoặc link người dùng chọn xem).
        public long ShownShopId;
    }

    // Tab 2 - Accounts (2 sub-tab: "Bình thường" + "Lỗi")
    private readonly TabControl _accountInnerTabs;
    private readonly DataGridView _accountsGrid;       // tab "Bình thường"
    private readonly DataGridView _errorAccountsGrid;  // tab "Lỗi" (dính verify/captcha)
    private readonly Button _importBtn;
    private readonly Button _addAccountBtn;
    private readonly Button _editAccountBtn;
    private readonly Button _deleteAccountBtn;
    private readonly Button _markErrorBtn;
    private readonly Button _recoverBtn;
    private readonly Button _solveCaptchaBtn;
    private readonly Button _exportAccountBtn;

    // Tab 3 - Keywords
    private readonly DataGridView _keywordsGrid;
    private readonly Button _importKeywordBtn;
    private readonly Button _addKeywordBtn;
    private readonly Button _editKeywordBtn;
    private readonly Button _deleteKeywordBtn;
    private readonly Button _markUnusedKeywordBtn;
    private readonly Button _selectAllKeywordsBtn;

    // Tab 4 - Tasks
    private readonly DataGridView _tasksGrid;
    private readonly Button _resumeTaskBtn;
    private readonly Button _researchTaskBtn;
    private readonly Button _exportTaskBtn;
    private readonly Button _refreshTasksBtn;

    public MainForm()
    {
        _appSettings.Load();
        _edge = new EdgeManager(_appSettings);

        Text = "Shopee Stat";
        StartPosition = FormStartPosition.CenterScreen;
        var workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        ClientSize = new Size(
            Math.Min(1800, workArea.Width * 3 / 4),
            Math.Min(1150, workArea.Height * 3 / 4));
        MinimumSize = new Size(1100, 720);

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            DrawMode = TabDrawMode.OwnerDrawFixed,
            SizeMode = TabSizeMode.Fixed,
            // Multiline: khi nhiều tab vượt bề rộng cửa sổ (DPI cao), tab xuống hàng 2 thay vì bị ẩn
            // sau mũi tên cuộn — tránh "mất" tab cuối (vd tab "Danh mục").
            Multiline = true,
            ItemSize = new Size(LogicalToDeviceUnits(160), LogicalToDeviceUnits(40)),
        };
        var tabFont = new Font("Segoe UI", 10.5f);
        var tabFontBold = new Font("Segoe UI", 10.5f, FontStyle.Bold);
        var tabAccent = Color.FromArgb(0, 120, 215);
        tabs.DrawItem += (s, e) =>
        {
            var tc = (TabControl)s!;
            var rect = tc.GetTabRect(e.Index);
            var selected = e.Index == tc.SelectedIndex;
            using (var back = new SolidBrush(selected ? Color.White : Color.FromArgb(240, 240, 240)))
                e.Graphics.FillRectangle(back, rect);
            if (selected)
            {
                using var bar = new SolidBrush(tabAccent);
                e.Graphics.FillRectangle(bar, rect.X + 2, rect.Bottom - LogicalToDeviceUnits(3), rect.Width - 4, LogicalToDeviceUnits(3));
            }
            TextRenderer.DrawText(e.Graphics, tc.TabPages[e.Index].Text,
                selected ? tabFontBold : tabFont, rect,
                selected ? tabAccent : Color.FromArgb(80, 80, 80),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        };
        tabs.SelectedIndexChanged += (_, _) => tabs.Invalidate();

        // Tab 1: Tìm kiếm
        (_keywordBox, _locationBox,
         _accountCombo, _startBtn, _autoBtn, _resumeFailedBtn, _pauseBtn, _stopBtn, _exportBtn, _exportAllBtn, _clearKeywordSearchBtn, _openTestBtn,
         _statusLabel, _resultCountLabel, _searchSpinner, _grid, _logBox, _wsStatusLabel) = BuildSearchTab(out var tab1);
        tabs.TabPages.Add(tab1);

        // Tab: Tìm theo file
        (_fileBox, _chooseFileBtn, _clearFileListBtn, _fileMinPriceBox, _fileMinSoldFromBox, _fileMinSoldToBox, _fileCategoryCombo, _fileApplyFilterBtn, _fileProcessBox,
         _fileRunBtn, _fileStopBtn, _fileExportBtn, _fileExportAllBtn, _fileRescanBtn, _fileUpdateCatBtn, _clearFileSearchBtn, _fileLaneTabs, _fileLogBox, _fileSpinner, _fileStatusLabel) = BuildFileTab(out var tabFile);
        tabs.TabPages.Add(tabFile);

        // Tab 2: Tài khoản
        (_accountInnerTabs, _accountsGrid, _errorAccountsGrid, _importBtn, _addAccountBtn, _editAccountBtn, _deleteAccountBtn, _markErrorBtn, _recoverBtn, _solveCaptchaBtn, _exportAccountBtn) =
            BuildAccountsTab(out var tab2);
        tabs.TabPages.Add(tab2);

        (_keywordsGrid, _importKeywordBtn, _addKeywordBtn, _editKeywordBtn, _deleteKeywordBtn, _markUnusedKeywordBtn, _selectAllKeywordsBtn) =
            BuildKeywordsTab(out var tabKeywords);
        tabs.TabPages.Add(tabKeywords);

        (_tasksGrid, _resumeTaskBtn, _researchTaskBtn, _exportTaskBtn, _refreshTasksBtn) =
            BuildTasksTab(out var tabTasks);
        tabs.TabPages.Add(tabTasks);

        (_categoriesGrid, _categoryProductsGrid, _refreshCategoriesBtn, _updateCategoriesBtn, _updateFileCategoriesBtn, _categoriesSpinner, _categoriesCountLabel) = BuildCategoriesTab(out var tabCategories);
        tabs.TabPages.Add(tabCategories);

        Controls.Add(tabs);
        FormClosing += OnFormClosing;

        RefreshAccountCombo();
        RefreshAccountsGrid();
        RefreshKeywordCombo();
        RefreshKeywordsGrid();
        RefreshTasksGrid();
        RefreshCategoriesGrid();
        RefreshFileCategoryCombo();
        _fileProcessBox.Value = Math.Clamp(_appSettings.Settings.LastFileProcessCount, 1, 999);

        BuildParallelControls();

        _exportBtn.Enabled = false;
        _resumeFailedBtn.Enabled = false;
        _pauseBtn.Enabled = false;
        _stopBtn.Enabled = false;
    }

    // Build helpers

    private static (ComboBox keyword, TextBox location,
        ComboBox account, Button start, Button auto, Button resumeFailed, Button pause, Button stop, Button export, Button exportAll, Button clearSearch, Button openTest,
        Label status, Label resultCount, Spinner spinner, DataGridView grid, TextBox logBox, Label wsStatus)
        BuildSearchTab(out TabPage page)
    {
        page = new TabPage("Tìm với từ khóa");

        var outer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 7,
            ColumnCount = 1,
            Padding = new Padding(8),
        };
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 0: search params
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 1: account + ws status
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 2: action buttons
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 3: progress
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 4: result count
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // 5: grid
        outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 130)); // 6: log
        page.Controls.Add(outer);

        // Row 0: search params
        var paramPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 4, 0, 4),
        };

        var keyword = new ComboBox
        {
            Width = 200,
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        var location = LabeledBox("", 120, "Hà Nội");

        paramPanel.Controls.Add(MakeLabel("Từ khóa:"));
        paramPanel.Controls.Add(keyword);
        paramPanel.Controls.Add(MakeLabel("  Khu vực:"));
        paramPanel.Controls.Add(location);
        outer.Controls.Add(paramPanel, 0, 0);

        // Row 1: account selector + ws status
        var accountPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 0, 0, 4),
        };
        var accountCombo = new ComboBox
        {
            Width = 220,
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        var wsStatus = new Label { Text = "●  Chưa kết nối", ForeColor = Color.Gray, AutoSize = true, Anchor = AnchorStyles.Left };

        accountPanel.Controls.Add(MakeLabel("Tài khoản:"));
        accountPanel.Controls.Add(accountCombo);
        accountPanel.Controls.Add(new Label { Width = 20 });
        accountPanel.Controls.Add(wsStatus);
        outer.Controls.Add(accountPanel, 0, 1);

        // Row 2: action buttons (icon-only, default style)
        var actPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 0, 0, 4),
        };
        var startBtn = new Button { Text = "▶ Bắt đầu", Width = 100, Height = 30 };
        var autoBtn = new Button { Text = "⏩ Tự động", Width = 105, Height = 30 };
        var resumeFailedBtn = new Button { Text = "↻ Resume lỗi", Width = 115, Height = 30 };
        var pauseBtn = new Button { Text = "⏸ Tạm dừng", Width = 110, Height = 30 };
        var stopBtn = new Button { Text = "■ Dừng", Width = 85, Height = 30 };
        var exportBtn = new Button { Text = "⬇ Xuất Excel", Width = 115, Height = 30 };
        var exportAllBtn = new Button { Text = "⬇⬇ Xuất tất cả (gộp)", Width = 165, Height = 30 };
        var clearSearchBtn = new Button { Text = "Xóa dữ liệu tìm kiếm", Width = 165, Height = 30, ForeColor = Color.FromArgb(180, 40, 40) };
        var openTestBtn = new Button { Text = "🌐 Mở test", Width = 105, Height = 30 };

        actPanel.Controls.Add(startBtn);
        actPanel.Controls.Add(autoBtn);
        actPanel.Controls.Add(resumeFailedBtn);
        actPanel.Controls.Add(pauseBtn);
        actPanel.Controls.Add(stopBtn);
        actPanel.Controls.Add(exportBtn);
        actPanel.Controls.Add(exportAllBtn);
        actPanel.Controls.Add(clearSearchBtn);
        actPanel.Controls.Add(new Label { Width = 16 });
        actPanel.Controls.Add(openTestBtn);
        outer.Controls.Add(actPanel, 0, 2);

        // Row 3: spinner + status
        var progressRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 1,
            ColumnCount = 2,
            AutoSize = true,
        };
        progressRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        progressRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var spinner = new Spinner { Anchor = AnchorStyles.Left, Margin = new Padding(0, 2, 6, 2) };
        var statusLabel = new Label { Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
        progressRow.Controls.Add(spinner, 0, 0);
        progressRow.Controls.Add(statusLabel, 1, 0);
        outer.Controls.Add(progressRow, 0, 3);

        var resultCountLabel = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Height = 22,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "Đã lấy về 0 sản phẩm",
            ForeColor = Color.FromArgb(40, 90, 150),
        };
        outer.Controls.Add(resultCountLabel, 0, 4);

        // Row 5: DataGridView
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            // Fill (not None) so columns expand to fill the whole grid width when the window
            // is widened — otherwise the fixed-width columns leave the data packed on the left.
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            RowHeadersWidth = 30,
        };
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Link", HeaderText = "Link", FillWeight = 260, MinimumWidth = 160 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Tên sản phẩm", FillWeight = 280, MinimumWidth = 180 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Price", HeaderText = "Giá (VND)", FillWeight = 90, MinimumWidth = 80 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Sold", HeaderText = "Bán/tháng", FillWeight = 80, MinimumWidth = 70 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Rating", HeaderText = "Rating", FillWeight = 65, MinimumWidth = 55 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Cat", HeaderText = "Danh mục", FillWeight = 140, MinimumWidth = 100 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Location", HeaderText = "Khu vực", FillWeight = 100, MinimumWidth = 80 });
        outer.Controls.Add(grid, 0, 5);

        var logBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = false,
            Font = new Font(FontFamily.GenericMonospace, 9f),
            BackColor = Color.FromArgb(18, 18, 18),
            ForeColor = Color.WhiteSmoke,
        };
        outer.Controls.Add(logBox, 0, 6);

        return (keyword, location,
                accountCombo, startBtn, autoBtn, resumeFailedBtn, pauseBtn, stopBtn, exportBtn, exportAllBtn, clearSearchBtn, openTestBtn,
                statusLabel, resultCountLabel, spinner, grid, logBox, wsStatus);
    }

    private static (TextBox fileBox, Button chooseBtn, Button clearList, NumericUpDown minPrice, NumericUpDown minSoldFrom, NumericUpDown minSoldTo, ComboBox fileCategory, Button applyFilter, NumericUpDown processCount,
        Button run, Button stop, Button export, Button exportAll, Button rescan, Button updateCat, Button clearSearch, TabControl laneTabs, TextBox logBox, Spinner spinner, Label status)
        BuildFileTab(out TabPage page)
    {
        page = new TabPage("Tìm theo file");

        var outer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 5,
            ColumnCount = 1,
            Padding = new Padding(8),
        };
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 0: file picker + min price + info
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 1: action buttons
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 2: file status line
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // 3: per-file lane tabs
        outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 140)); // 4: log
        page.Controls.Add(outer);

        // Row 0: gói 2 hàng — hàng chọn file + Process, hàng bộ lọc — để không bị tràn/rớt dòng.
        var topPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(0, 4, 0, 2) };

        // Hàng 0a: chọn file + Process
        var fileRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 0, 0, 2) };
        var fileBox = new TextBox { Width = 320, ReadOnly = true, PlaceholderText = "Chọn nhiều file .xlsx chứa link sản phẩm..." };
        var chooseBtn = new Button { Text = "Chọn file...", Width = 100, Height = 26 };
        var clearListBtn = new Button { Text = "🗑 Xóa danh sách", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 0, 8, 0), Height = 26, ForeColor = Color.FromArgb(180, 40, 40) };
        var (_, processCount) = LabeledNumeric("Process:", 1, 1, 999, 1);
        fileRow.Controls.Add(MakeLabel("File:"));
        fileRow.Controls.Add(fileBox);
        fileRow.Controls.Add(chooseBtn);
        fileRow.Controls.Add(clearListBtn);
        fileRow.Controls.Add(new Label { Width = 24 });
        fileRow.Controls.Add(MakeLabel("Process:"));
        fileRow.Controls.Add(processCount);

        // Hàng 0b: bộ lọc (chỉ để hiển thị + xuất Excel) + nút Áp dụng
        var filterRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
        // Mặc định 0 = KHÔNG lọc (lấy toàn bộ). Người dùng tự đặt rồi bấm "Áp dụng".
        var (_, minPrice) = LabeledNumeric("Giá tối thiểu:", 0, 0, 100_000_000, 10_000);
        // Khoảng "bán/tháng": 0 = không giới hạn. Chỉ dùng để LỌC khi hiển thị + xuất Excel
        // (crawl vẫn lưu CSDL toàn bộ sản phẩm).
        var (_, minSoldFrom) = LabeledNumeric("Bán/tháng từ:", 0, 0, 999_999, 10);
        var (_, minSoldTo) = LabeledNumeric("đến:", 0, 0, 999_999, 10);
        // Lọc theo danh mục (áp cho cả bảng hiển thị + xuất Excel). Để "(Tất cả danh mục)" = không lọc.
        var fileCategory = new ComboBox { Width = 220, DropDownStyle = ComboBoxStyle.DropDownList };
        fileCategory.Items.Add(AllCategoriesItem);
        fileCategory.SelectedIndex = 0;
        var applyFilter = new Button { Text = "Áp dụng", Width = 90, Height = 26, Margin = new Padding(8, 0, 0, 0) };
        filterRow.Controls.Add(MakeLabel("Lọc (hiển thị/xuất):"));
        filterRow.Controls.Add(MakeLabel("  Giá min (VND):"));
        filterRow.Controls.Add(minPrice);
        filterRow.Controls.Add(MakeLabel("  Bán/tháng từ:"));
        filterRow.Controls.Add(minSoldFrom);
        filterRow.Controls.Add(MakeLabel("  đến:"));
        filterRow.Controls.Add(minSoldTo);
        filterRow.Controls.Add(MakeLabel("  Danh mục:"));
        filterRow.Controls.Add(fileCategory);
        filterRow.Controls.Add(applyFilter);
        filterRow.Controls.Add(MakeLabel("  (0 = không giới hạn)"));

        topPanel.Controls.Add(fileRow);
        topPanel.Controls.Add(filterRow);
        outer.Controls.Add(topPanel, 0, 0);

        // Row 1: action buttons + info song song
        var actPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 0, 0, 4) };
        var run = new Button { Text = "▶ Chạy (song song)", Width = 150, Height = 30 };
        var stop = new Button { Text = "■ Dừng", Width = 85, Height = 30 };
        var export = new Button { Text = "⬇ Xuất link đang chọn", Width = 175, Height = 30 };
        var exportAll = new Button { Text = "⬇⬇ Xuất tất cả", Width = 130, Height = 30 };
        var rescan = new Button { Text = "↻ Quét lại link đang chọn", Width = 190, Height = 30 };
        var updateCat = new Button { Text = "🤖 Cập nhật danh mục (AI)", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 0, 8, 0), Height = 30 };
        var clearSearch = new Button { Text = "Xóa dữ liệu tìm kiếm", Width = 165, Height = 30, ForeColor = Color.FromArgb(180, 40, 40) };
        actPanel.Controls.Add(run);
        actPanel.Controls.Add(stop);
        actPanel.Controls.Add(new Label { Width = 16 });
        actPanel.Controls.Add(export);
        actPanel.Controls.Add(exportAll);
        actPanel.Controls.Add(rescan);
        actPanel.Controls.Add(updateCat);
        actPanel.Controls.Add(clearSearch);
        var spinner = new Spinner { Anchor = AnchorStyles.Left, Margin = new Padding(12, 0, 0, 0) };
        actPanel.Controls.Add(spinner);
        var info = MakeLabel("  (chạy tối đa theo ô Process · mỗi process dùng 1 tài khoản)");
        info.ForeColor = Color.Gray;
        actPanel.Controls.Add(info);
        outer.Controls.Add(actPanel, 0, 1);

        // Row 2: file-tab status line (riêng của tab này — KHÔNG dùng chung _statusLabel của tab từ khóa)
        var status = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Height = 22,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(40, 90, 150),
        };
        outer.Controls.Add(status, 0, 2);

        // Row 3: per-file lane tabs (1 tab/file: danh sách link + sản phẩm shop hiện tại)
        var laneTabs = new TabControl { Dock = DockStyle.Fill };
        outer.Controls.Add(laneTabs, 0, 3);

        // Row 4: log
        var logBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = false,
            Font = new Font(FontFamily.GenericMonospace, 9f),
            BackColor = Color.FromArgb(18, 18, 18),
            ForeColor = Color.WhiteSmoke,
        };
        outer.Controls.Add(logBox, 0, 4);

        return (fileBox, chooseBtn, clearListBtn, minPrice, minSoldFrom, minSoldTo, fileCategory, applyFilter, processCount, run, stop, export, exportAll, rescan, updateCat, clearSearch, laneTabs, logBox, spinner, status);
    }

    // One links grid (Dòng/Link/Trạng thái) for a file lane.
    private static DataGridView NewFileLinksGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            RowHeadersWidth = 30,
        };
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Row", HeaderText = "Dòng", FillWeight = 12, MinimumWidth = 50 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Link", HeaderText = "Link sản phẩm", FillWeight = 48, MinimumWidth = 160 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Shop", HeaderText = "Shop", FillWeight = 20, MinimumWidth = 90 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "Trạng thái", FillWeight = 30, MinimumWidth = 110 });
        return grid;
    }

    // One products grid for a file lane (sản phẩm của shop đang quét).
    private static DataGridView NewFileProductsGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            RowHeadersWidth = 30,
        };
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Link", HeaderText = "Link", FillWeight = 34, MinimumWidth = 140 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Tên sản phẩm", FillWeight = 32, MinimumWidth = 150 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Category", HeaderText = "Danh mục", FillWeight = 18, MinimumWidth = 100 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Price", HeaderText = "Giá", FillWeight = 10, MinimumWidth = 70 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Sold", HeaderText = "Bán/tháng", FillWeight = 10, MinimumWidth = 70 });
        return grid;
    }

    private static (TabControl innerTabs, DataGridView grid, DataGridView errorGrid, Button importBtn, Button addBtn, Button editBtn, Button deleteBtn, Button markErrorBtn, Button recoverBtn, Button solveCaptchaBtn, Button exportAccountBtn)
        BuildAccountsTab(out TabPage page)
    {
        page = new TabPage("Tài khoản");
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            Padding = new Padding(8),
        };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.Controls.Add(panel);

        var btnRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 0, 0, 4) };
        var importBtn = new Button { Text = "Import...", Width = 90, Height = 28 };
        var addBtn = new Button { Text = "+ Thêm", Width = 80, Height = 28 };
        var editBtn = new Button { Text = "Sửa", Width = 70, Height = 28 };
        var deleteBtn = new Button { Text = "Xóa", Width = 70, Height = 28, ForeColor = Color.FromArgb(180, 40, 40) };
        var markErrorBtn = new Button { Text = "Đánh dấu lỗi →", Width = 120, Height = 28, ForeColor = Color.FromArgb(180, 40, 40) };
        var solveCaptchaBtn = new Button { Text = "Mở giải captcha", Width = 130, Height = 28, ForeColor = Color.FromArgb(0, 90, 160) };
        var recoverBtn = new Button { Text = "← Khôi phục", Width = 110, Height = 28, ForeColor = Color.FromArgb(0, 120, 60) };
        var exportAccountBtn = new Button { Text = "Export account", Width = 120, Height = 28, ForeColor = Color.FromArgb(0, 100, 50) };
        btnRow.Controls.Add(importBtn);
        btnRow.Controls.Add(addBtn);
        btnRow.Controls.Add(editBtn);
        btnRow.Controls.Add(deleteBtn);
        btnRow.Controls.Add(markErrorBtn);
        btnRow.Controls.Add(solveCaptchaBtn);
        btnRow.Controls.Add(recoverBtn);
        btnRow.Controls.Add(exportAccountBtn);
        panel.Controls.Add(btnRow, 0, 0);

        static DataGridView NewGrid()
        {
            var g = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            };
            g.Columns.Add(new DataGridViewTextBoxColumn { Name = "Username", HeaderText = "Username", FillWeight = 25 });
            g.Columns.Add(new DataGridViewTextBoxColumn { Name = "Proxy", HeaderText = "Proxy", FillWeight = 30 });
            g.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "Trạng thái", FillWeight = 20 });
            g.Columns.Add(new DataGridViewTextBoxColumn { Name = "Profile", HeaderText = "Profile path", FillWeight = 25 });
            return g;
        }

        var grid = NewGrid();
        var errorGrid = NewGrid();
        // Tab "Lỗi": cột "Trạng thái" hiển thị lý do + thời điểm bị chặn thay cho ✓/⚠.
        errorGrid.Columns["Status"].HeaderText = "Lý do / thời điểm";

        var innerTabs = new TabControl { Dock = DockStyle.Fill };
        var normalPage = new TabPage("Bình thường");
        var errorPage = new TabPage("Lỗi");
        normalPage.Controls.Add(grid);
        errorPage.Controls.Add(errorGrid);
        innerTabs.TabPages.Add(normalPage);
        innerTabs.TabPages.Add(errorPage);
        panel.Controls.Add(innerTabs, 0, 1);

        return (innerTabs, grid, errorGrid, importBtn, addBtn, editBtn, deleteBtn, markErrorBtn, recoverBtn, solveCaptchaBtn, exportAccountBtn);
    }

    private static (DataGridView grid, Button importBtn, Button addBtn, Button editBtn, Button deleteBtn, Button markUnusedBtn, Button selectAllBtn)
        BuildKeywordsTab(out TabPage page)
    {
        page = new TabPage("Từ khóa");
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            Padding = new Padding(8),
        };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.Controls.Add(panel);

        var btnRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 0, 0, 4) };
        var importBtn = new Button { Text = "Import...", Width = 90, Height = 28 };
        var addBtn = new Button { Text = "+ Thêm", Width = 80, Height = 28 };
        var editBtn = new Button { Text = "Sửa", Width = 70, Height = 28 };
        var deleteBtn = new Button { Text = "Xóa", Width = 70, Height = 28, ForeColor = Color.FromArgb(180, 40, 40) };
        var markUnusedBtn = new Button { Text = "Đánh dấu chưa dùng", Width = 150, Height = 28 };
        var selectAllBtn = new Button { Text = "Chọn tất cả", Width = 95, Height = 28 };
        btnRow.Controls.Add(importBtn);
        btnRow.Controls.Add(addBtn);
        btnRow.Controls.Add(editBtn);
        btnRow.Controls.Add(deleteBtn);
        btnRow.Controls.Add(markUnusedBtn);
        btnRow.Controls.Add(selectAllBtn);
        panel.Controls.Add(btnRow, 0, 0);

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = true, // cho phép chọn nhiều dòng để xóa / reset hàng loạt
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        };
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Keyword", HeaderText = "Từ khóa", FillWeight = 80 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Used", HeaderText = "Trạng thái", FillWeight = 20 });
        panel.Controls.Add(grid, 0, 1);

        return (grid, importBtn, addBtn, editBtn, deleteBtn, markUnusedBtn, selectAllBtn);
    }

    private static (DataGridView grid, Button resumeBtn, Button researchBtn, Button exportBtn, Button refreshBtn)
        BuildTasksTab(out TabPage page)
    {
        page = new TabPage("Task");
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            Padding = new Padding(8),
        };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.Controls.Add(panel);

        var btnRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 0, 0, 4),
        };
        var resumeBtn = new Button { Text = "Resume", Width = 90, Height = 28 };
        var researchBtn = new Button { Text = "Research", Width = 90, Height = 28 };
        var exportBtn = new Button { Text = "Xuất Excel", Width = 100, Height = 28 };
        var refreshBtn = new Button { Text = "Refresh", Width = 80, Height = 28 };
        btnRow.Controls.Add(resumeBtn);
        btnRow.Controls.Add(researchBtn);
        btnRow.Controls.Add(exportBtn);
        btnRow.Controls.Add(refreshBtn);
        panel.Controls.Add(btnRow, 0, 0);

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        };
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Id", HeaderText = "ID", FillWeight = 8 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Keyword", HeaderText = "Từ khóa", FillWeight = 22 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Account", HeaderText = "Tài khoản", FillWeight = 18 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "Trạng thái", FillWeight = 13 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Checkpoint", HeaderText = "Checkpoint", FillWeight = 19 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Count", HeaderText = "Sản phẩm", FillWeight = 10 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Updated", HeaderText = "Cập nhật", FillWeight = 16 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Error", HeaderText = "Lỗi", FillWeight = 24 });
        panel.Controls.Add(grid, 0, 1);

        return (grid, resumeBtn, researchBtn, exportBtn, refreshBtn);
    }

    // Tab "Danh mục": trái = từ điển danh mục (tự upsert khi quét shop); phải = sản phẩm của danh mục đang chọn.
    private static (DataGridView grid, DataGridView productsGrid, Button refresh, Button updateAi, Button updateFile, Spinner spinner, Label count) BuildCategoriesTab(out TabPage page)
    {
        page = new TabPage("Danh mục");
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            Padding = new Padding(8),
        };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.Controls.Add(panel);

        var btnRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 0, 0, 4),
        };
        var refreshBtn = new Button { Text = "↻ Làm mới", Width = 100, Height = 28 };
        var updateAiBtn = new Button { Text = "🤖 Cập nhật danh mục CSDL (AI)", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 0, 8, 0), Height = 28, Margin = new Padding(8, 0, 0, 0) };
        var updateFileBtn = new Button { Text = "📄 Cập nhật danh mục cho file Excel (AI)", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 0, 8, 0), Height = 28, Margin = new Padding(8, 0, 0, 0) };
        var spinner = new Spinner { Anchor = AnchorStyles.Left, Margin = new Padding(12, 4, 0, 0) };
        var countLabel = MakeLabel("");
        countLabel.ForeColor = Color.Gray;
        countLabel.Margin = new Padding(8, 6, 0, 0);
        btnRow.Controls.Add(refreshBtn);
        btnRow.Controls.Add(updateAiBtn);
        btnRow.Controls.Add(updateFileBtn);
        btnRow.Controls.Add(spinner);
        btnRow.Controls.Add(countLabel);
        panel.Controls.Add(btnRow, 0, 0);

        // 2 grid cạnh nhau: trái danh mục, phải sản phẩm theo danh mục.
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        };
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Danh mục", FillWeight = 50, MinimumWidth = 120 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Products", HeaderText = "Số SP", FillWeight = 18 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Shops", HeaderText = "Số shop", FillWeight = 16 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Last", HeaderText = "Lần cuối", FillWeight = 16 });
        split.Panel1.Controls.Add(grid);

        var productsGrid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        };
        productsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Link", HeaderText = "Link", FillWeight = 30, MinimumWidth = 120 });
        productsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Tên sản phẩm", FillWeight = 34, MinimumWidth = 150 });
        productsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Shop", HeaderText = "Shop", FillWeight = 16, MinimumWidth = 90 });
        productsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Price", HeaderText = "Giá", FillWeight = 10, MinimumWidth = 70 });
        productsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Sold", HeaderText = "Bán/tháng", FillWeight = 10, MinimumWidth = 70 });
        split.Panel2.Controls.Add(productsGrid);

        panel.Controls.Add(split, 0, 1);
        // Đặt vạch chia ~42% sau khi split có kích thước thật (tránh lỗi SplitterDistance lúc chưa layout).
        split.HandleCreated += (_, _) =>
        {
            try { split.SplitterDistance = Math.Max(200, (int)(split.Width * 0.42)); } catch { }
        };

        return (grid, productsGrid, refreshBtn, updateAiBtn, updateFileBtn, spinner, countLabel);
    }

    private void RefreshCategoriesGrid()
    {
        List<SearchTaskStore.CategoryRow> rows;
        try { rows = _taskStore.GetCategories(); }
        catch (Exception ex) { _categoriesCountLabel.Text = "Lỗi đọc danh mục: " + ex.Message; return; }

        _categoriesGrid.Rows.Clear();
        foreach (var c in rows)
        {
            _categoriesGrid.Rows.Add(
                c.Name,
                c.ProductCount.ToString("N0"),
                c.ShopCount.ToString("N0"),
                FormatDateString(c.FirstSeen),
                FormatDateString(c.LastSeen));
        }
        _categoriesCountLabel.Text = $"{rows.Count} danh mục";
        LoadCategoryProductsForSelected();
    }

    // Nạp sản phẩm của danh mục đang chọn (grid trái) sang grid phải.
    private void LoadCategoryProductsForSelected()
    {
        _categoryProductsGrid.Rows.Clear();
        var row = _categoriesGrid.CurrentRow
            ?? (_categoriesGrid.SelectedRows.Count > 0 ? _categoriesGrid.SelectedRows[0] : null);
        if (row is null) return;
        var name = Convert.ToString(row.Cells["Name"].Value) ?? "";
        if (string.IsNullOrWhiteSpace(name)) return;

        List<ProductResult> products;
        try { products = _taskStore.GetShopProductsByCategory(name); }
        catch { return; }
        foreach (var p in products)
            _categoryProductsGrid.Rows.Add(p.Link, p.Name, p.ShopName, p.PriceVnd.ToString("N0"), p.MonthlySold.ToString());
    }

    // Cập nhật danh mục cho sản phẩm shop bằng OpenAI: đọc danh mục lá từ shopee-cat.docx, phân loại
    // theo TÊN sản phẩm (gpt-4.1-mini), ghi vào CSDL. Chỉ xử lý sản phẩm CHƯA có danh mục.
    private async void OnUpdateCategoriesClick(object? sender, EventArgs e)
    {
        if (_updatingCategories) return;

        if (!File.Exists(CategoryDocxPath))
        {
            MessageBox.Show($"Không thấy file danh mục tham chiếu:\n{CategoryDocxPath}", "Cập nhật danh mục (AI)", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!File.Exists(OpenAiKeyPath))
        {
            MessageBox.Show($"Không thấy file OpenAI key:\n{OpenAiKeyPath}", "Cập nhật danh mục (AI)", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        List<ShopeeCategoryReference.LeafCategory> leaves;
        try { leaves = ShopeeCategoryReference.LoadLeafCategories(CategoryDocxPath); }
        catch (Exception ex)
        {
            MessageBox.Show("Đọc file danh mục lỗi: " + ex.Message, "Cập nhật danh mục (AI)", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (leaves.Count == 0)
        {
            MessageBox.Show("Không đọc được danh mục nào từ file .docx.", "Cập nhật danh mục (AI)", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string apiKey;
        try { apiKey = CategoryAiUpdater.ReadKey(OpenAiKeyPath); }
        catch (Exception ex)
        {
            MessageBox.Show("Đọc OpenAI key lỗi: " + ex.Message, "Cập nhật danh mục (AI)", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            MessageBox.Show("File OpenAI key rỗng.", "Cập nhật danh mục (AI)", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var products = _taskStore.GetAllShopProductsForCategory();
        if (products.Count == 0)
        {
            MessageBox.Show("Chưa có sản phẩm nào trong CSDL để cập nhật danh mục.", "Cập nhật danh mục (AI)", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (MessageBox.Show(
                $"Phân loại lại danh mục bằng AI cho TẤT CẢ {products.Count} sản phẩm (GHI ĐÈ danh mục hiện có)\n(dùng {leaves.Count} danh mục lá, model gpt-4.1-mini).\nViệc này gọi OpenAI và có thể mất vài phút. Tiếp tục?",
                "Cập nhật danh mục (AI)", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        _updatingCategories = true;
        _updateCategoriesBtn.Enabled = false;
        _fileUpdateCatBtn.Enabled = false;
        _updateFileCategoriesBtn.Enabled = false;
        _refreshCategoriesBtn.Enabled = false;
        var paths = leaves.Select(l => l.Path).ToList();
        var updater = new CategoryAiUpdater(apiKey, "gpt-4.1-mini");
        const int batchSize = 40;
        var totalUpdated = 0;
        try
        {
            for (var start = 0; start < products.Count; start += batchSize)
            {
                var slice = products.GetRange(start, Math.Min(batchSize, products.Count - start));
                var names = slice.Select(p => p.Name).ToList();
                _categoriesCountLabel.Text = $"Đang cập nhật danh mục (AI)… {start}/{products.Count}";

                int[] idx;
                try { idx = await updater.ClassifyAsync(names, paths, CancellationToken.None); }
                catch (Exception ex)
                {
                    MessageBox.Show($"Gọi OpenAI lỗi (đã cập nhật {totalUpdated} sản phẩm):\n{ex.Message}",
                        "Cập nhật danh mục (AI)", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    break;
                }

                var batchUpdates = new List<(long, long, string)>();
                for (var k = 0; k < slice.Count; k++)
                {
                    var ci = idx[k];
                    if (ci >= 0 && ci < paths.Count)
                        batchUpdates.Add((slice[k].ShopId, slice[k].ItemId, paths[ci]));
                }
                if (batchUpdates.Count > 0)
                {
                    _taskStore.SetShopProductCategories(batchUpdates); // ghi từng lô → không mất tiến độ nếu lỗi
                    totalUpdated += batchUpdates.Count;
                }
            }

            _taskStore.PruneUnusedCategories(); // dọn danh mục rác cũ không còn sản phẩm
            RefreshCategoriesGrid();
            RefreshFileCategoryCombo();
            ReloadAllLaneProducts();
            MessageBox.Show($"Đã cập nhật danh mục cho {totalUpdated}/{products.Count} sản phẩm.",
                "Cập nhật danh mục (AI)", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        finally
        {
            _updatingCategories = false;
            _updateCategoriesBtn.Enabled = true;
            _fileUpdateCatBtn.Enabled = true;
            _updateFileCategoriesBtn.Enabled = true;
            _refreshCategoriesBtn.Enabled = true;
            RefreshCategoriesGrid();
        }
    }

    // Cập nhật danh mục cho 1 FILE EXCEL (không đụng CSDL): chọn file → AI phân loại theo tên sp →
    // ghi danh mục vào cột "Danh mục" rồi lưu lại chính file đó. Dành cho file lớn (~chục nghìn dòng).
    private async void OnUpdateFileCategoriesClick(object? sender, EventArgs e)
    {
        if (_updatingCategories) return;

        if (!File.Exists(CategoryDocxPath))
        {
            MessageBox.Show($"Không thấy file danh mục tham chiếu:\n{CategoryDocxPath}", "Cập nhật danh mục cho file", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!File.Exists(OpenAiKeyPath))
        {
            MessageBox.Show($"Không thấy file OpenAI key:\n{OpenAiKeyPath}", "Cập nhật danh mục cho file", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        List<ShopeeCategoryReference.LeafCategory> leaves;
        string apiKey;
        try
        {
            leaves = ShopeeCategoryReference.LoadLeafCategories(CategoryDocxPath);
            apiKey = CategoryAiUpdater.ReadKey(OpenAiKeyPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Đọc danh mục/key lỗi: " + ex.Message, "Cập nhật danh mục cho file", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (leaves.Count == 0 || string.IsNullOrWhiteSpace(apiKey))
        {
            MessageBox.Show("Danh mục tham chiếu trống hoặc key rỗng.", "Cập nhật danh mục cho file", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string path;
        using (var dlg = new OpenFileDialog { Title = "Chọn file Excel cần cập nhật danh mục", Filter = "Excel (*.xlsx)|*.xlsx" })
        {
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            path = dlg.FileName;
        }

        // Bật báo "đang thực hiện" ngay từ lúc đọc file (file lớn có thể mất vài giây).
        void StopBusy() { _categoriesSpinner.Spinning = false; _categoriesCountLabel.ForeColor = Color.Gray; }
        _categoriesSpinner.Spinning = true;
        _categoriesCountLabel.ForeColor = Color.FromArgb(40, 90, 150);
        _categoriesCountLabel.Text = $"Đang đọc file: {Path.GetFileName(path)} …";

        ExcelCategoryFile file;
        try { file = await Task.Run(() => new ExcelCategoryFile(path)); }
        catch (IOException)
        {
            StopBusy();
            MessageBox.Show("Không mở được file (có thể đang mở trong Excel). Hãy đóng file rồi thử lại.", "Cập nhật danh mục cho file", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        catch (Exception ex)
        {
            StopBusy();
            MessageBox.Show("Đọc file Excel lỗi: " + ex.Message, "Cập nhật danh mục cho file", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var total = file.Names.Count;
        if (total == 0)
        {
            file.Dispose();
            StopBusy();
            _categoriesCountLabel.Text = "";
            MessageBox.Show("Không tìm thấy dòng sản phẩm nào (cần cột 'Tên sp') trong file.", "Cập nhật danh mục cho file", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _categoriesCountLabel.Text = $"File có {total} dòng — chờ xác nhận…";
        if (MessageBox.Show(
                $"Phân loại danh mục bằng AI cho {total} dòng trong file:\n{Path.GetFileName(path)}\n(model gpt-4.1-mini), rồi ghi lại chính file này. Tiếp tục?",
                "Cập nhật danh mục cho file", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            file.Dispose();
            StopBusy();
            _categoriesCountLabel.Text = "";
            return;
        }

        _updatingCategories = true;
        _updateCategoriesBtn.Enabled = false;
        _fileUpdateCatBtn.Enabled = false;
        _updateFileCategoriesBtn.Enabled = false;
        _refreshCategoriesBtn.Enabled = false;
        try
        {
            var paths = leaves.Select(l => l.Path).ToList();
            var updater = new CategoryAiUpdater(apiKey, "gpt-4.1-mini");
            var cats = await updater.ClassifyAllAsync(
                file.Names, paths, batchSize: 50, maxParallel: 2,
                onProgress: d => BeginInvoke(() => _categoriesCountLabel.Text = $"Đang phân loại (AI)… {d}/{total}"),
                CancellationToken.None);

            _categoriesCountLabel.Text = "Đang ghi lại file…";
            var written = await Task.Run(() => file.ApplyAndSave(cats));
            _categoriesCountLabel.Text = $"Đã ghi {written}/{total} dòng vào file.";

            if (MessageBox.Show($"Đã cập nhật danh mục cho {written}/{total} dòng và lưu lại:\n{path}\n\nMở file?",
                    "Cập nhật danh mục cho file", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("Lỗi cập nhật danh mục cho file: " + ex.Message, "Cập nhật danh mục cho file", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            file.Dispose();
            _updatingCategories = false;
            _updateCategoriesBtn.Enabled = true;
            _fileUpdateCatBtn.Enabled = true;
            _updateFileCategoriesBtn.Enabled = true;
            _refreshCategoriesBtn.Enabled = true;
        }
    }

    // Nạp danh sách danh mục vào combo lọc của tab "Tìm theo file" (giữ lựa chọn hiện tại nếu còn).
    private void RefreshFileCategoryCombo()
    {
        var prev = _fileCategoryCombo.SelectedItem as string;
        List<SearchTaskStore.CategoryRow> rows;
        try { rows = _taskStore.GetCategories(); }
        catch { return; }

        _fileCategoryCombo.BeginUpdate();
        _fileCategoryCombo.Items.Clear();
        _fileCategoryCombo.Items.Add(AllCategoriesItem);
        foreach (var c in rows)
            if (!string.IsNullOrWhiteSpace(c.Name))
                _fileCategoryCombo.Items.Add(c.Name);
        var idx = prev is null ? 0 : _fileCategoryCombo.Items.IndexOf(prev);
        _fileCategoryCombo.SelectedIndex = idx >= 0 ? idx : 0;
        _fileCategoryCombo.EndUpdate();
    }

    // ISO date string → "yyyy-MM-dd HH:mm" cho dễ đọc; giữ nguyên nếu parse lỗi.
    private static string FormatDateString(string iso) =>
        DateTime.TryParse(iso, out var dt) ? dt.ToString("yyyy-MM-dd HH:mm") : iso;

    private static Label MakeLabel(string text) =>
        // Anchor = Left (không Top/Bottom) để FlowLayoutPanel canh giữa label theo chiều cao hàng
        new() { Text = text, AutoSize = true, TextAlign = ContentAlignment.MiddleLeft, Anchor = AnchorStyles.Left };

    private static TextBox LabeledBox(string _, int width, string placeholder) => new()
    {
        Width = width,
        PlaceholderText = placeholder,
        Text = placeholder,
    };

    private static (Label, NumericUpDown) LabeledNumeric(string label, decimal value, decimal min, decimal max, decimal increment)
    {
        var lbl = new Label { Text = label, AutoSize = true };
        var num = new NumericUpDown { Minimum = min, Maximum = max, Value = value, Increment = increment, Width = 110, ThousandsSeparator = true };
        return (lbl, num);
    }

    // ── Wire-up ───────────────────────────────────────────────────────────────

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        _startBtn.Click += OnStartClick;
        _autoBtn.Click += OnAutoClick;
        _resumeFailedBtn.Click += OnResumeFailedClick;
        _pauseBtn.Click += OnPauseClick;
        _stopBtn.Click += OnStopClick;
        _exportBtn.Click += OnExportClick;
        _exportAllBtn.Click += OnExportAllKeywordsClick;
        _clearKeywordSearchBtn.Click += OnClearKeywordSearchClick;
        _openTestBtn.Click += OnOpenShopeeTestClick;

        _chooseFileBtn.Click += OnChooseFileClick;
        _clearFileListBtn.Click += OnClearFileListClick;
        // Giữ số Process qua các lần chạy / mở lại app.
        _fileProcessBox.ValueChanged += (_, _) =>
        {
            _appSettings.Settings.LastFileProcessCount = (int)_fileProcessBox.Value;
            _appSettings.SaveSettings();
        };
        _fileRunBtn.Click += OnRunFileClick;
        _fileStopBtn.Click += OnFileStopClick;
        _fileExportBtn.Click += OnFileExportClick;
        _fileExportAllBtn.Click += OnFileExportAllClick;
        _refreshCategoriesBtn.Click += (_, _) => { RefreshCategoriesGrid(); RefreshFileCategoryCombo(); };
        _updateCategoriesBtn.Click += OnUpdateCategoriesClick;
        _fileUpdateCatBtn.Click += OnUpdateCategoriesClick;
        _updateFileCategoriesBtn.Click += OnUpdateFileCategoriesClick;
        _categoriesGrid.SelectionChanged += (_, _) => LoadCategoryProductsForSelected();
        // Làm mới danh sách danh mục khi mở dropdown.
        _fileCategoryCombo.DropDown += (_, _) => RefreshFileCategoryCombo();
        // Nút "Áp dụng": lọc lại ngay kết quả đang hiển thị theo min giá/min bán/danh mục hiện tại.
        _fileApplyFilterBtn.Click += (_, _) => { RefreshFileCategoryCombo(); ReloadAllLaneProducts(); };
        _fileRescanBtn.Click += OnFileRescanClick;
        _clearFileSearchBtn.Click += OnClearFileSearchClick;
        _fileStopBtn.Enabled = false;

        _toolTip.SetToolTip(_startBtn, "Bắt đầu tìm kiếm");
        _toolTip.SetToolTip(_autoBtn, "Chạy tự động (tất cả từ khóa)");
        _toolTip.SetToolTip(_resumeFailedBtn, "Resume task lỗi gần nhất (mở lại Edge)");
        _toolTip.SetToolTip(_pauseBtn, "Tạm dừng script (không tắt Edge); bấm lại để tiếp tục");
        _toolTip.SetToolTip(_stopBtn, "Dừng hẳn: tắt Edge và kết thúc phiên");
        _toolTip.SetToolTip(_exportBtn, "Xuất Excel");
        _toolTip.SetToolTip(_clearKeywordSearchBtn, "Xóa toàn bộ lịch sử task và kết quả tìm theo từ khóa");
        _toolTip.SetToolTip(_clearFileSearchBtn, "Xóa toàn bộ lịch sử quét shop theo file");
        _toolTip.SetToolTip(_openTestBtn, "Mở Edge với profile tài khoản để test (không tìm kiếm)");

        _importBtn.Click += OnImportClick;
        _addAccountBtn.Click += OnAddAccountClick;
        _editAccountBtn.Click += OnEditAccountClick;
        _deleteAccountBtn.Click += OnDeleteAccountClick;
        _markErrorBtn.Click += OnMarkAccountErrorClick;
        _recoverBtn.Click += OnRecoverAccountClick;
        _solveCaptchaBtn.Click += OnSolveCaptchaClick;
        _toolTip.SetToolTip(_solveCaptchaBtn, "Mở Edge với chính profile tài khoản lỗi để tự giải verify/captcha, xong bấm \"← Khôi phục\"");
        _exportAccountBtn.Click += OnExportAccountClick;
        _accountsGrid.CellDoubleClick += (_, _) => OnEditAccountClick(null, EventArgs.Empty);
        _errorAccountsGrid.CellDoubleClick += (_, _) => OnEditAccountClick(null, EventArgs.Empty);
        _importKeywordBtn.Click += OnImportKeywordsClick;
        _addKeywordBtn.Click += OnAddKeywordClick;
        _editKeywordBtn.Click += OnEditKeywordClick;
        _deleteKeywordBtn.Click += OnDeleteKeywordClick;
        _markUnusedKeywordBtn.Click += OnMarkKeywordUnusedClick;
        _selectAllKeywordsBtn.Click += (_, _) => { _keywordsGrid.Focus(); _keywordsGrid.SelectAll(); };
        _keywordsGrid.CellDoubleClick += (_, _) => OnEditKeywordClick(null, EventArgs.Empty);
        _resumeTaskBtn.Click += OnResumeTaskClick;
        _researchTaskBtn.Click += OnResearchTaskClick;
        _exportTaskBtn.Click += OnExportTaskClick;
        _refreshTasksBtn.Click += (_, _) => RefreshTasksGrid();
        _tasksGrid.CellDoubleClick += (_, _) => OnResumeTaskClick(null, EventArgs.Empty);
        _grid.KeyDown += OnResultsGridKeyDown;
        _grid.CellMouseDown += OnResultsGridCellMouseDown;

        StyleLaneTabs(_fileLaneTabs, StopFileLaneByTab, ReconnectFileLaneByTab); // "✕" + "⟳" trên tab file

        CheckForResumableRun();
    }

    // "✕" on a keyword lane tab: stop that lane's current keyword (after confirm). The tab is then
    // removed via LaneKeywordReleased; the worker continues to the next keyword if any remain.
    private void StopKeywordLaneByTab(int tabIndex)
    {
        if (_coordinator is null || _laneTabs is null) return;
        if (tabIndex < 0 || tabIndex >= _laneTabs.TabPages.Count) return;
        var page = _laneTabs.TabPages[tabIndex];
        var lane = _laneUi.Values.FirstOrDefault(u => u.Page == page);
        if (lane is null) return;
        if (MessageBox.Show($"Dừng tìm từ khóa \"{page.Text}\"? Sẽ để \"chưa kết thúc\".",
                "Dừng lane", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _coordinator.StopLane(lane.LaneId);
    }

    // "⟳" trên tab từ khóa: kết nối lại lane này (relaunch + tiếp tục từ checkpoint, cùng account).
    private void ReconnectKeywordLaneByTab(int tabIndex)
    {
        if (_coordinator is null || _laneTabs is null) return;
        if (tabIndex < 0 || tabIndex >= _laneTabs.TabPages.Count) return;
        var page = _laneTabs.TabPages[tabIndex];
        var lane = _laneUi.Values.FirstOrDefault(u => u.Page == page);
        if (lane is null) return;
        lane.Status.Text = "⟳ Đang kết nối lại...";
        SetLaneTabDot(lane.LaneId, Color.Orange);
        _coordinator.RestartLane(lane.LaneId);
    }

    // "✕" on a file tab. Đang chạy → dừng file đó (chưa kết thúc). Không chạy → đóng (gỡ) tab file đó.
    private void StopFileLaneByTab(int tabIndex)
    {
        if (tabIndex < 0 || tabIndex >= _fileLaneTabs.TabPages.Count) return;
        var page = _fileLaneTabs.TabPages[tabIndex];
        var lane = _fileLaneUis.FirstOrDefault(u => u.Page == page);
        if (lane is null) return;
        var fileName = Path.GetFileName(lane.FilePath);

        if (_fileRunning) // đang chạy (cờ tắt ngay khi bấm Dừng, không phải đợi teardown xong)
        {
            if (MessageBox.Show($"Dừng tìm file \"{fileName}\"? Các link chưa xong sẽ để lại (chưa kết thúc).",
                    "Dừng file", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            var laneId = _laneToFile.FirstOrDefault(kv =>
                string.Equals(kv.Value, lane.FilePath, StringComparison.OrdinalIgnoreCase)).Key;
            if (laneId > 0) _fileCoordinator?.StopLane(laneId);   // đang chạy file này
            else _fileCoordinator?.SkipFile(lane.FilePath);        // còn trong hàng đợi
            lane.Status.Text = "■ Đã dừng file này (chưa kết thúc).";
            return;
        }

        // Không chạy → gỡ tab file này khỏi danh sách (không xóa file gốc/dữ liệu).
        if (MessageBox.Show($"Bỏ file \"{fileName}\" khỏi danh sách?",
                "Đóng tab", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _filePaths.RemoveAll(p => string.Equals(p, lane.FilePath, StringComparison.OrdinalIgnoreCase));
        _fileLaneUis.Remove(lane);
        _fileLaneTabs.TabPages.Remove(page);
        page.Dispose();
        UpdateFileBoxText();
        PersistFilePaths();
    }

    // "⟳" trên tab file: kết nối lại lane đang chạy file này (relaunch + thử lại link hiện tại, cùng account).
    private void ReconnectFileLaneByTab(int tabIndex)
    {
        if (_fileCoordinator is null || tabIndex < 0 || tabIndex >= _fileLaneTabs.TabPages.Count) return;
        var page = _fileLaneTabs.TabPages[tabIndex];
        var lane = _fileLaneUis.FirstOrDefault(u => u.Page == page);
        if (lane is null) return;
        var laneId = _laneToFile.FirstOrDefault(kv =>
            string.Equals(kv.Value, lane.FilePath, StringComparison.OrdinalIgnoreCase)).Key;
        if (laneId <= 0) return; // file chưa chạy trên lane nào → không có gì để kết nối lại
        lane.Status.Text = "⟳ Đang kết nối lại...";
        _fileCoordinator.RestartLane(laneId);
    }

    // On startup: re-load last file selection (only files with pending links) and, if a previous run
    // was left unfinished, nudge the user to continue — without auto-starting anything.
    private void CheckForResumableRun()
    {
        int filePending;
        try { filePending = RestoreFilesFromLastSession(); }
        catch { filePending = 0; }

        // Từ khóa "chưa kết thúc" = vừa có task dở (Running/Failed/Stopped) trong CSDL, vừa thuộc danh
        // sách hiện tại, vừa CHƯA hoàn thành. (Không tính từ khóa chưa chạy bao giờ; không bị task cũ
        // tồn đọng của từ khóa đã hoàn thành làm nhiễu — đó là lý do trước đây báo "0 từ khóa".)
        var resumableKeywords = 0;
        try
        {
            var resumable = _taskStore.GetResumableKeywords();
            resumableKeywords = _appSettings.Settings.Keywords.Count(k =>
                !string.IsNullOrWhiteSpace(k) && resumable.Contains(k) && !IsKeywordUsed(k));
        }
        catch { }

        var parts = new List<string>();
        if (resumableKeywords > 0)
        {
            parts.Add($"{resumableKeywords} từ khóa chưa kết thúc");
            SetStatus("↻ Còn từ khóa chưa kết thúc — bấm \"Tự động\" để tiếp tục.");
        }
        if (filePending > 0)
            parts.Add($"{_filePaths.Count} file còn {filePending} link");

        if (parts.Count == 0) return; // không có gì còn dở → không nhắc

        MessageBox.Show(
            "Phát hiện lượt chạy trước còn dở:\n• " + string.Join("\n• ", parts) +
            "\n\nMở tab và bấm \"Tự động\" (từ khóa) hoặc \"Chạy\" (file) để tiếp tục từ chỗ đã dừng.",
            "Tiếp tục lượt trước", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private async void OnStartClick(object? sender, EventArgs e)
    {
        if (_accountCombo.SelectedItem is not InstanceConfig account)
        {
            MessageBox.Show("Chọn tài khoản trước.", "Shopee Stat", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var selectedKeyword = GetSelectedKeyword();
        if (string.IsNullOrWhiteSpace(selectedKeyword))
        {
            MessageBox.Show("Chọn từ khóa trước.", "Shopee Stat", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        _exportBtn.Enabled = false;
        ShowLaneTabs(false); // chế độ chạy đơn dùng grid chung
        _grid.Rows.Clear();
        UpdateResultCountLabel();
        SetSearchUiRunning(true);

        try
        {
            var outcome = await RunSearchSessionAsync(account, selectedKeyword, autoMode: false, ct);
            if (outcome != SearchRunOutcome.Completed)
            {
                _edge.Kill();
                _ = DisposeCdpInputAsync();
                _ws?.Dispose();
                _ws = null;
                ResetButtons();
                _exportBtn.Enabled = GetExportResults().Count > 0;
            }
        }
        catch (OperationCanceledException)
        {
            ResetButtons();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Lỗi khởi động", MessageBoxButtons.OK, MessageBoxIcon.Error);
            ResetButtons();
        }
    }

    private async void OnAutoClick(object? sender, EventArgs e)
    {
        // Pool = account KHÔNG ở tab "Lỗi", xoay vòng bắt đầu từ account sau account dùng lần trước.
        var accounts = BuildRunAccounts();
        var allKeywords = _appSettings.Settings.Keywords
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .ToList();

        if (accounts.Count == 0)
        {
            MessageBox.Show("Không còn tài khoản khả dụng (tất cả đang ở tab \"Lỗi\"). Khôi phục bớt tài khoản rồi chạy lại.",
                "Shopee Stat", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (allKeywords.Count == 0)
        {
            MessageBox.Show("Chưa có từ khóa để chạy tự động.", "Shopee Stat", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Bỏ qua từ khóa đã HOÀN THÀNH (UsedKeywords) → bấm Tự động = chạy tiếp phần còn lại / lượt dở.
        var keywords = allKeywords.Where(k => !IsKeywordUsed(k)).ToList();
        if (keywords.Count == 0)
        {
            MessageBox.Show("Tất cả từ khóa đã hoàn thành. Dùng \"Đánh dấu chưa dùng\" (tab Từ khóa) để chạy lại.",
                "Shopee Stat", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Worker pool = min(số luồng, số account, số từ khóa còn lại). Tab tạo động theo từng từ
        // khóa đang chạy: xong 1 từ khóa → đóng tab; còn từ khóa & còn slot luồng → mở tab mới.
        var requested = (int)(_lanesBox?.Value ?? _appSettings.Settings.MaxParallelLanes);
        var laneCount = Math.Max(1, Math.Min(requested, Math.Min(accounts.Count, keywords.Count)));
        _appSettings.Settings.MaxParallelLanes = requested;
        _appSettings.SaveSettings();

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        _exportBtn.Enabled = false;
        ShowLaneTabs(true);
        ResetLaneTabs(); // tab tạo động theo từng từ khóa đang chạy
        SetSearchUiRunning(true);
        _pauseBtn.Enabled = false; // pause không áp dụng cho chế độ song song
        SetStatus($"Auto song song: {laneCount} lane, {keywords.Count} từ khóa, {accounts.Count} account.");

        var coordinator = new AutoRunCoordinator(
            _appSettings, _taskStore, accounts, keywords, laneCount,
            _locationBox.Text.Trim());
        _coordinator = coordinator;

        coordinator.LaneStatus += (lane, msg) => BeginInvoke(() =>
        {
            SetLaneStatus(lane, msg);
            AppendLog($"[L{lane}] {msg}");
        });
        coordinator.LaneAssigned += (lane, keyword, accountName, isFirstAttempt) => BeginInvoke(() =>
        {
            _activeKeywords.Add(keyword);
            OnLaneAssigned(lane, keyword, accountName, isFirstAttempt);
            RefreshKeywordsGrid();
        });
        // Keyword rời lane (xong / skip / bỏ) → đóng tab đó + hết "đang tìm kiếm".
        coordinator.LaneKeywordReleased += (lane, keyword) => BeginInvoke(() =>
        {
            _activeKeywords.Remove(keyword);
            OnLaneKeywordReleased(lane);
            RefreshKeywordsGrid();
        });
        // Mark a keyword "đã hoàn thành" only when its crawl actually completes (not when assigned),
        // so an interrupted keyword is NOT skipped on the next run.
        coordinator.LaneKeywordCompleted += keyword => BeginInvoke(() => MarkKeywordUsed(keyword));
        coordinator.LaneProduct += (lane, product) => BeginInvoke(() => AddLaneProductRow(lane, product));
        coordinator.LaneConnection += (lane, connected) => BeginInvoke(() =>
            SetLaneTabDot(lane, connected ? Color.LimeGreen : Color.Orange));
        coordinator.LaneFinished += lane => BeginInvoke(() => OnLaneFinished(lane));
        coordinator.TasksChanged += () => BeginInvoke(RefreshTasksGrid);
        coordinator.AccountsChanged += () => BeginInvoke(RefreshAccountsGrid);
        coordinator.AccountUsed += id => BeginInvoke(() => SetLastUsedAccount(id));
        coordinator.AccountErrored += (id, reason) => BeginInvoke(() => MarkAccountErroredFromRun(id, reason));
        coordinator.SaveExcel = (keyword, results) => Task.Run(() => SaveKeywordExcel(keyword, results));

        try
        {
            await coordinator.RunAsync(ct);
            SetStatus("Auto hoàn thành: đã hết từ khóa.");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Auto đã dừng.");
        }
        catch (Exception ex)
        {
            SetStatus("Auto lỗi: " + ex.Message);
        }
        finally
        {
            _coordinator = null;
            _activeKeywords.Clear();
            RefreshKeywordsGrid();
            ResetButtons();
        }
    }

    private async Task<SearchRunOutcome> RunSearchSessionAsync(
        InstanceConfig account,
        string selectedKeyword,
        bool autoMode,
        CancellationToken ct,
        long taskId = 0,
        bool resumeExistingTask = false,
        bool researchExistingTask = false,
        SearchConfig? presetConfig = null)
    {
        // presetConfig != null ? run with a caller-built config (e.g. shop-from-link mode),
        // not the keyword tab's UI fields, and don't treat the label as a keyword.
        // (Keyword is marked "đã hoàn thành" only when the crawl Completes — see SearchCompleted below.)
        SetStatus("Đang khởi động...");

        var port = _appSettings.Settings.WsPort;

        var profileDir = _appSettings.GetProfileDir(account);
        string? proxy;
        try
        {
            proxy = await _edge.ResolveProxyAsync(account);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
            if (!autoMode)
                MessageBox.Show(ex.Message, "Proxy lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return SearchRunOutcome.Error;
        }

        _edge.Launch(account, profileDir, proxy, port);
        SetStatus("Edge đang khởi động...");
        await _edge.CleanupRestoredTabsAsync(port, ct);

        if (account.OpenWithShopeeAccount)
        {
            SetStatus("Đang đăng nhập Shopee...");
            var loginSvc = new ShopeeLoginService(_appSettings);
            var ok = await loginSvc.EnsureLoggedInAsync(account, _edge.CdpPort, SetStatus, ct);

            if (!ok)
            {
                SetStatus("Đăng nhập thất bại. Chuyển lượt sau.");
                RefreshAccountsGrid();
                return SearchRunOutcome.Error;
            }

            RefreshAccountsGrid();
        }

        var searchConfig = presetConfig ?? new SearchConfig
        {
            Keyword = selectedKeyword,
            RegionFilterText = _locationBox.Text.Trim(),
            MinPriceVnd = 0,
            MinMonthlySold = 0,
            CheckVariantStock = false,
            ResumeCategoryIndex = 1,
        };

        if (taskId > 0)
        {
            var task = _taskStore.GetTask(taskId);
            if (task is not null)
            {
                searchConfig.Keyword = task.Keyword;
                searchConfig.RegionFilterText = task.RegionFilterText;
                searchConfig.CheckVariantStock = task.CheckVariantStock;
                searchConfig.ResumeCategoryIndex = resumeExistingTask
                    ? Math.Max(1, task.ResumeCategoryIndex)
                    : 1;
                selectedKeyword = task.Keyword;
            }
        }

        if (taskId <= 0)
        {
            taskId = _taskStore.CreateTask(searchConfig, account);
        }
        else if (researchExistingTask)
        {
            _taskStore.ResetTask(taskId, searchConfig, account);
        }
        else
        {
            _taskStore.UpdateStatus(taskId, "Running");
        }

        _currentTaskId = taskId;
        RefreshTasksGrid();

        _ws?.Dispose();
        _ws = new WebSocketServer(port);

        var completion = new TaskCompletionSource<SearchRunOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = ct.Register(() => completion.TrySetResult(SearchRunOutcome.Cancelled));

        _orchestrator = new SearchOrchestrator(_ws, _appSettings);
        _orchestrator.PrepareSearch(searchConfig);
        _orchestrator.ProgressChanged += msg => Invoke(() => SetStatus(msg));
        _orchestrator.ProductFound += product => Invoke(() => AddProductRow(product));
        _orchestrator.ProductPersisted += product =>
        {
            _taskStore.SaveProduct(taskId, product);
            Invoke(() => RefreshTasksGrid());
        };
        _orchestrator.CheckpointChanged += (categoryIndex, categoryName, page) =>
        {
            _taskStore.UpdateCheckpoint(taskId, categoryIndex, categoryName, page);
            Invoke(() => RefreshTasksGrid());
        };
        _orchestrator.CaptchaDetected += () => Invoke(() =>
        {
            if (autoMode)
            {
                _taskStore.UpdateStatus(taskId, "Failed", "Verify/captcha");
                _lastFailedTaskId = taskId;
                _resumeFailedBtn.Enabled = true;
                RefreshTasksGrid();
                SetStatus("Phát hiện verify/captcha.");
                completion.TrySetResult(SearchRunOutcome.CaptchaOrVerify);
            }
            else
            {
                _taskStore.UpdateStatus(taskId, "WaitingCaptcha", "Đang chờ giải captcha.");
                RefreshTasksGrid();
                ShowCaptchaAlert();
            }
        });
        _orchestrator.NetworkErrorDetected += msg => Invoke(() =>
        {
            SetStatus("Lỗi mạng/proxy: " + msg);
            _taskStore.UpdateStatus(taskId, "Failed", msg);
            _lastFailedTaskId = taskId;
            _resumeFailedBtn.Enabled = true;
            RefreshTasksGrid();
            if (!autoMode)
            {
                ResetButtons();
                _exportBtn.Enabled = GetExportResults().Count > 0;
            }
            completion.TrySetResult(SearchRunOutcome.NetworkError);
        });
        _orchestrator.SearchCompleted += () => Invoke(() =>
        {
            _taskStore.UpdateStatus(taskId, "Completed");
            if (presetConfig is null) MarkKeywordUsed(selectedKeyword); // hoàn thành → đánh dấu đã dùng
            RefreshTasksGrid();
            if (!autoMode) OnSearchCompleted();
            completion.TrySetResult(SearchRunOutcome.Completed);
        });
        _orchestrator.ErrorOccurred += msg => Invoke(() =>
        {
            SetStatus($"Lỗi: {msg}");
            _taskStore.UpdateStatus(taskId, "Failed", msg);
            _lastFailedTaskId = taskId;
            _resumeFailedBtn.Enabled = true;
            RefreshTasksGrid();
            if (!autoMode) ResetButtons();
            completion.TrySetResult(SearchRunOutcome.Error);
        });

        _ws.Connected += () => Invoke(() =>
        {
            _wsStatusLabel.Text = "●  Extension kết nối";
            _wsStatusLabel.ForeColor = Color.Green;
            SetStatus("Extension kết nối. Chờ extension sẵn sàng...");
        });
        _ws.Disconnected += () => Invoke(() =>
        {
            _wsStatusLabel.Text = "●  Mất kết nối";
            _wsStatusLabel.ForeColor = Color.Red;
        });

        _ws.Start();
        _wsStatusLabel.Text = "●  Chờ extension...";
        _wsStatusLabel.ForeColor = Color.Orange;

        // Attach trusted-input controller to the search tab. Connect now that the
        // tab cleanup + login have run; the login service has released its own CDP
        // session by this point. Failure is non-fatal — the extension falls back to
        // synthetic events when a gesture is nacked.
        _ = DisposeCdpInputAsync();
        _cdpInput = new CdpInputController(_ws, _edge.CdpPort);
        _cdpInput.Log += msg => Invoke(() => SetStatus(msg));
        _ = _cdpInput.StartAsync(ct);

        SetStatus("Edge đang chạy. Chờ extension kết nối...");
        var outcome = await completion.Task;
        if (outcome == SearchRunOutcome.Cancelled)
            throw new OperationCanceledException(ct);

        return outcome;
    }

    // Saves one keyword's products to D:\shopee-stat\keywords as "<keyword>-<datetime>.xlsx".
    // Runs off the UI thread (called concurrently by lanes); the lock guards the unique-name
    // probe so two lanes finishing the same second can't pick the same file name.
    private void SaveKeywordExcel(string keyword, IReadOnlyList<ProductResult> results)
    {
        if (results.Count == 0) return;

        try
        {
            Directory.CreateDirectory(KeywordsExportDir);
            string path;
            lock (_excelLock)
            {
                var baseName = $"{SafeFilePart(keyword)}-{DateTime.Now:yyyyMMdd_HHmmss}";
                var fileName = baseName + ".xlsx";
                var n = 2;
                while (File.Exists(Path.Combine(KeywordsExportDir, fileName)))
                    fileName = $"{baseName}-{n++}.xlsx";
                path = ExcelExporter.Export(results, KeywordsExportDir, fileName);
            }
            BeginInvoke(() => SetStatus($"Đã lưu Excel: {path}"));
        }
        catch (Exception ex)
        {
            BeginInvoke(() => SetStatus("Lỗi lưu Excel: " + ex.Message));
        }
    }

    // -- Parallel lane UI --------------------------------------------------------

    // Creates the "số luồng" selector and the (initially hidden) per-lane tab control,
    // sharing the grid cell with the single-run grid.
    private void BuildParallelControls()
    {
        if (_accountCombo.Parent is { } accountPanel)
        {
            accountPanel.Controls.Add(new Label { Width = 24 });
            accountPanel.Controls.Add(MakeLabel("Số luồng:"));
            _lanesBox = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 10,
                Width = 55,
                Value = Math.Clamp(_appSettings.Settings.MaxParallelLanes, 1, 10),
            };
            _toolTip.SetToolTip(_lanesBox, "Số cửa sổ Edge/account chạy song song khi bấm Tự động (= số account).");
            accountPanel.Controls.Add(_lanesBox);
        }

        if (_grid.Parent is TableLayoutPanel cell)
        {
            var pos = cell.GetCellPosition(_grid);
            _laneTabs = new TabControl { Dock = DockStyle.Fill, Visible = false };
            StyleLaneTabs(_laneTabs, StopKeywordLaneByTab, ReconnectKeywordLaneByTab);
            cell.Controls.Add(_laneTabs, pos.Column, pos.Row);
        }
    }

    private const int LaneTabCloseW = 18; // bề rộng vùng "✕" bên phải mỗi tab lane

    // Owner-draws a lane TabControl with large, high-contrast tabs + "⟳" (kết nối lại) và "✕" per tab.
    // Selected lane = solid accent fill + white bold text; others light grey.
    // Clicking "✕" → onCloseTab(index); clicking "⟳" → onReconnectTab(index).
    private void StyleLaneTabs(TabControl tabs, Action<int> onCloseTab, Action<int> onReconnectTab)
    {
        tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
        tabs.SizeMode = TabSizeMode.Fixed;
        tabs.ItemSize = new Size(240, 36);
        var font = new Font("Segoe UI", 9.5f);
        var fontBold = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        var fontClose = new Font("Segoe UI", 8f, FontStyle.Regular);
        var accent = Color.FromArgb(0, 120, 215);
        tabs.DrawItem += (s, e) =>
        {
            var tc = (TabControl)s!;
            if (e.Index < 0 || e.Index >= tc.TabPages.Count) return;
            var rect = tc.GetTabRect(e.Index);
            var selected = e.Index == tc.SelectedIndex;
            using (var back = new SolidBrush(selected ? accent : Color.FromArgb(232, 236, 240)))
                e.Graphics.FillRectangle(back, rect);
            // Thin separator so adjacent unselected tabs are distinguishable.
            using (var sep = new Pen(Color.FromArgb(208, 212, 218)))
                e.Graphics.DrawRectangle(sep, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);

            // "✕" close + "⟳" reconnect hit-areas on the far right (handled in MouseDown).
            var closeRect = new Rectangle(rect.Right - LaneTabCloseW, rect.Y, LaneTabCloseW, rect.Height);
            TextRenderer.DrawText(e.Graphics, "✕", fontClose, closeRect,
                selected ? Color.White : Color.FromArgb(150, 70, 70),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            var reconnectRect = new Rectangle(rect.Right - 2 * LaneTabCloseW, rect.Y, LaneTabCloseW, rect.Height);
            TextRenderer.DrawText(e.Graphics, "⟳", fontClose, reconnectRect,
                selected ? Color.White : Color.FromArgb(0, 110, 70),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            // The product count is encoded as a trailing "[N]" in the tab text. Draw it as a
            // right-aligned badge that is NEVER truncated, then the title (left, with ellipsis).
            var text = tc.TabPages[e.Index].Text ?? "";
            var main = text;
            var badge = "";
            var lb = text.LastIndexOf('[');
            if (lb >= 0 && text.EndsWith("]"))
            {
                badge = text[lb..];
                main = text[..lb].TrimEnd();
            }

            var fg = selected ? Color.White : Color.FromArgb(55, 55, 55);
            var badgeColor = selected ? Color.White : Color.FromArgb(0, 120, 215);
            var content = Rectangle.Inflate(rect, -8, 0);
            content = new Rectangle(content.X, content.Y, content.Width - 2 * LaneTabCloseW, content.Height); // chừa chỗ cho ⟳ và ✕

            if (badge.Length > 0)
            {
                var bw = TextRenderer.MeasureText(e.Graphics, badge, fontBold).Width;
                var badgeRect = new Rectangle(content.Right - bw, content.Y, bw, content.Height);
                TextRenderer.DrawText(e.Graphics, badge, fontBold, badgeRect, badgeColor,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                content = new Rectangle(content.X, content.Y, content.Width - bw - 6, content.Height);
            }

            // Connection dot (TabPage.Tag = Color): chờ kết nối (cam) · đã kết nối (xanh) · xong (xám).
            var mainLeft = content.X;
            if (tc.TabPages[e.Index].Tag is Color dot)
            {
                const int d = 9;
                var prev = e.Graphics.SmoothingMode;
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var db = new SolidBrush(dot))
                    e.Graphics.FillEllipse(db, content.X, content.Y + (content.Height - d) / 2, d, d);
                e.Graphics.SmoothingMode = prev;
                mainLeft = content.X + d + 6;
            }
            var mainRect = new Rectangle(mainLeft, content.Y, Math.Max(0, content.Right - mainLeft), content.Height);

            TextRenderer.DrawText(e.Graphics, main, selected ? fontBold : font, mainRect, fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        };
        tabs.SelectedIndexChanged += (_, _) => tabs.Invalidate();
        tabs.MouseDown += (s, e) =>
        {
            var tc = (TabControl)s!;
            for (var i = 0; i < tc.TabPages.Count; i++)
            {
                var r = tc.GetTabRect(i);
                var x = new Rectangle(r.Right - LaneTabCloseW, r.Y, LaneTabCloseW, r.Height);
                if (x.Contains(e.Location)) { onCloseTab(i); return; }
                var rc = new Rectangle(r.Right - 2 * LaneTabCloseW, r.Y, LaneTabCloseW, r.Height);
                if (rc.Contains(e.Location)) { onReconnectTab(i); return; }
            }
        };
    }

    private void ShowLaneTabs(bool show)
    {
        if (_laneTabs is null) return;
        _laneTabs.Visible = show;
        _grid.Visible = !show;
        // The "Đã lấy về N sản phẩm" count only makes sense for the single grid; in parallel mode
        // each lane tab shows its own count, so hide the (always-0) global label there.
        _resultCountLabel.Visible = !show;
        // The global WS dot is only driven by the single-run flow - meaningless in parallel mode
        // (it would sit at "Chưa kết nối"). Each lane tab shows its own connection dot instead.
        _wsStatusLabel.Visible = !show;
    }

    // Sets a lane tab's connection dot (stored in TabPage.Tag, drawn by StyleLaneTabs) and repaints.
    private void SetLaneTabDot(int laneId, Color color)
    {
        if (Lane(laneId) is not { } lane) return;
        lane.Page.Tag = color;
        _laneTabs?.Invalidate();
    }

    // Clears all lane tabs at the start of an auto run (tabs are created on demand per keyword).
    private void ResetLaneTabs()
    {
        if (_laneTabs is null) return;
        _laneTabs.TabPages.Clear();
        _laneUi.Clear();
    }

    // Creates (or returns the existing) tab for the keyword currently on this lane. One tab per
    // running keyword: created when a lane picks up a keyword, removed when that keyword finishes.
    private LaneUi GetOrCreateLaneTab(int laneId)
    {
        if (_laneUi.TryGetValue(laneId, out var existing)) return existing;

        var page = new TabPage($"Lane {laneId}") { Tag = Color.Orange };
        var tlp = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        tlp.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        tlp.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var status = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Height = 24,
            TextAlign = ContentAlignment.MiddleLeft,
        Text = "Chờ...",
            ForeColor = Color.FromArgb(40, 90, 150),
        };
        var grid = NewResultGrid();
        tlp.Controls.Add(status, 0, 0);
        tlp.Controls.Add(grid, 0, 1);
        page.Controls.Add(tlp);
        _laneTabs!.TabPages.Add(page);
        var ui = new LaneUi(laneId, page, grid, status, new ResultCounter());
        _laneUi[laneId] = ui;
        return ui;
    }

    // A lane finished its keyword (done/skip/giveup): close that tab. The worker will create a new
    // tab when it picks up the next keyword (if any remain and a process slot is free).
    private void OnLaneKeywordReleased(int laneId)
    {
        if (!_laneUi.TryGetValue(laneId, out var ui)) return;
        _laneUi.Remove(laneId);
        try { _laneTabs?.TabPages.Remove(ui.Page); ui.Page.Dispose(); } catch { }
    }

    private static DataGridView NewResultGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            // Fill so the lane grid's columns expand with the (now wider) window instead of
            // staying fixed-width and leaving the data packed on the left.
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            RowHeadersWidth = 30,
        };
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Link", HeaderText = "Link", FillWeight = 240, MinimumWidth = 150 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Tên sản phẩm", FillWeight = 260, MinimumWidth = 170 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Price", HeaderText = "Giá (VND)", FillWeight = 90, MinimumWidth = 80 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Sold", HeaderText = "Bán/tháng", FillWeight = 80, MinimumWidth = 70 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Rating", HeaderText = "Rating", FillWeight = 60, MinimumWidth = 55 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Cat", HeaderText = "Danh mục", FillWeight = 130, MinimumWidth = 100 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Location", HeaderText = "Khu vực", FillWeight = 100, MinimumWidth = 80 });
        return grid;
    }

    private LaneUi? Lane(int laneId) => _laneUi.TryGetValue(laneId, out var ui) ? ui : null;

    private void SetLaneStatus(int laneId, string msg)
    {
        if (Lane(laneId) is { } lane)
            lane.Status.Text = msg;
    }

    // A lane picks up a keyword. firstAttempt=true = a NEW keyword -> (re)create the tab fresh.
    // Retry (firstAttempt=false, account swap on the SAME keyword) -> reuse the tab, keep its rows.
    private void OnLaneAssigned(int laneId, string keyword, string accountName, bool isFirstAttempt)
    {
        var lane = GetOrCreateLaneTab(laneId);
        if (isFirstAttempt)
        {
            lane.Grid.Rows.Clear();
            lane.Counter.Count = 0;
        }
        lane.Page.Text = $"{keyword} ({accountName})";
        lane.Status.Text = isFirstAttempt
            ? $"Đang tìm \"{keyword}\" — {accountName}"
            : $"Thử lại \"{keyword}\" — {accountName} (giữ {lane.Counter.Count} sp đã có)";
        SetLaneTabDot(laneId, Color.Orange); // (re)launching this lane -> chờ kết nối
    }

    private void OnLaneFinished(int laneId)
    {
        // Worker exited (no more keywords). Its keyword tab was already removed on release — nothing to do.
        OnLaneKeywordReleased(laneId);
    }

    private void AddLaneProductRow(int laneId, ProductResult p)
    {
        if (Lane(laneId) is not { } lane) return;

        foreach (DataGridViewRow row in lane.Grid.Rows)
        {
            if (row.IsNewRow) continue;
            if (!string.Equals(Convert.ToString(row.Cells[0].Value), p.Link, StringComparison.OrdinalIgnoreCase))
                continue;

            row.Cells[1].Value = p.Name;
            row.Cells[2].Value = p.PriceVnd.ToString("N0");
            row.Cells[3].Value = p.MonthlySold.ToString();
            row.Cells[4].Value = p.Rating.ToString("0.0");
            row.Cells[5].Value = p.Category;
            row.Cells[6].Value = p.ShopLocation;
            return;
        }

        lane.Grid.Rows.Add(p.Link, p.Name,
            p.PriceVnd.ToString("N0"), p.MonthlySold.ToString(),
            p.Rating.ToString("0.0"), p.Category, p.ShopLocation);
        lane.Counter.Count = lane.Grid.Rows.Count;
        var title = lane.Page.Text;
        var bar = title.IndexOf("  [", StringComparison.Ordinal);
        if (bar >= 0) title = title[..bar];
        lane.Page.Text = $"{title}  [{lane.Counter.Count}]";
    }

    private static string SafeFilePart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Trim()
            .Select(ch => invalid.Contains(ch) ? '-' : ch)
            .ToArray();
        var cleaned = new string(chars);
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+", "-");
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, "-{2,}", "-").Trim('-');
        return string.IsNullOrWhiteSpace(cleaned) ? "unknown" : cleaned;
    }

    // Prompt for the Excel output folder. Defaults to the last-used folder (persisted),
    // not the Desktop. Returns null if the user cancels.
    private string? PromptExportFolder()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Chọn thư mục lưu file Excel",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };
        var last = _appSettings.Settings.OutputDirectory;
        if (!string.IsNullOrWhiteSpace(last) && Directory.Exists(last))
            dlg.SelectedPath = last;
        else
            dlg.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        if (dlg.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dlg.SelectedPath))
            return null;

        _appSettings.Settings.OutputDirectory = dlg.SelectedPath;
        _appSettings.SaveSettings();
        return dlg.SelectedPath;
    }

    // Build "Stat - <từ khóa> - <datetime>.xlsx". Strips only filesystem-invalid chars,
    // keeping spaces so the name stays readable.
    private static string StatExportFileName(string keyword)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string((keyword ?? "").Trim()
            .Select(ch => invalid.Contains(ch) ? ' ' : ch).ToArray());
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"\s+", " ").Trim();
        if (clean.Length == 0) clean = "khong-tu-khoa";
        return $"Stat - {clean} - {DateTime.Now:yyyy-MM-dd_HHmmss}.xlsx";
    }

    // Test-only: open Edge with the selected account's profile + proxy + extension,
    // exactly like a real run's launch step, but start no WebSocket server and no search.
    private async void OnOpenShopeeTestClick(object? sender, EventArgs e)
    {
        if (_accountCombo.SelectedItem is not InstanceConfig account)
        {
            MessageBox.Show("Chọn tài khoản trước.", "Shopee Stat", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            SetStatus($"Mở Edge test với profile \"{account.DisplayName}\"...");
            var port = _appSettings.Settings.WsPort;
            var profileDir = _appSettings.GetProfileDir(account);
            var proxy = await _edge.ResolveProxyAsync(account);
            _edge.Launch(account, profileDir, proxy, port);
            SetStatus($"Đã mở Edge (profile \"{account.DisplayName}\"). Chỉ để test — không chạy tìm kiếm.");
        }
        catch (Exception ex)
        {
            SetStatus("Mở test lỗi: " + ex.Message);
            MessageBox.Show(ex.Message, "Mở Edge test", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // Tìm theo file

    private void OnClearKeywordSearchClick(object? sender, EventArgs e)
    {
        if (IsKeywordSearchActive())
        {
            MessageBox.Show("Đang tìm kiếm dở trước khi xóa dữ liệu.", "Shopee Stat",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (MessageBox.Show(
                "Xóa toàn bộ lịch sử tìm kiếm từ khóa?\n\n"
                + "• Task và sản phẩm đã lưu (CSDL)\n"
                + "• Trạng thái \"đã hoàn thành\" của từ khóa\n"
                + "• Bảng kết quả và log trên tab này",
                "Xác nhận", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        _taskStore.ClearKeywordSearchHistory();
        _appSettings.Settings.UsedKeywords.Clear();
        _appSettings.SaveSettings();

        _grid.Rows.Clear();
        _logBox.Clear();
        _currentTaskId = 0;
        _lastFailedTaskId = 0;
        _activeKeywords.Clear();
        ResetLaneTabs();
        UpdateResultCountLabel();
        _exportBtn.Enabled = false;
        _resumeFailedBtn.Enabled = false;

        RefreshTasksGrid();
        RefreshKeywordsGrid();
        RefreshKeywordCombo();
        SetStatus("Đã xóa lịch sử tìm kiếm từ khóa.");
    }

    private void OnClearFileSearchClick(object? sender, EventArgs e)
    {
        if (_fileRunning)
        {
            MessageBox.Show("Đang chạy dở trước khi xóa dữ liệu.", "Tìm theo file",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (MessageBox.Show(
                "Xóa toàn bộ lịch sử tìm kiếm theo file?\n\n"
                + "• Sản phẩm shop đã quét (CSDL)\n"
                + "• Danh sách shop đã quét (scanned-shops.tsv)\n"
                + "• Task tìm theo link\n"
                + "• Trạng thái link trên file Excel đang mở",
                "Xác nhận", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        _taskStore.ClearFileSearchHistory();
        new ScannedShopStore(ShopExportDir).ClearAll();
        _fileShopStore = null;

        foreach (var path in _filePaths)
        {
            try { new LinkFileStore(path).ClearAllStatuses(); }
            catch { /* best effort */ }
        }

        foreach (var lane in _fileLaneUis)
        {
            lane.ProductsGrid.Rows.Clear();
            lane.ShownShopId = 0;
            foreach (DataGridViewRow row in lane.LinksGrid.Rows)
            {
                row.Cells["Status"].Value = "";
                row.Cells["Shop"].Value = ShopTextFromLink(Convert.ToString(row.Cells["Link"].Value) ?? "");
                row.DefaultCellStyle.ForeColor = Color.Black;
            }
        }

        _fileLogBox.Clear();
        RefreshTasksGrid();
        SetFileStatus("Đã xóa lịch sử tìm kiếm theo file.");
    }

    private bool IsKeywordSearchActive() => _coordinator is not null || _stopBtn.Enabled;

    private void OnChooseFileClick(object? sender, EventArgs e)
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Chọn nhiều file Excel chứa link sản phẩm",
            Filter = "Excel (*.xlsx)|*.xlsx",
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        _filePaths.Clear();
        _filePaths.AddRange(dlg.FileNames);
        UpdateFileBoxText();
        PersistFilePaths();

        try
        {
            BuildFileLaneTabs();
            var totalPending = _fileLaneUis.Sum(l => CountPending(l.LinksGrid));
            SetFileStatus($"Đã nạp {_filePaths.Count} file. Tổng link chưa xử lý: {totalPending}.");
        }
        catch (Exception ex)
        {
            MessageBox.Show("Không đọc được file: " + ex.Message, "Tìm theo file", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // Xóa toàn bộ danh sách file đã chọn (ô File) và đóng các tab file tương ứng. KHÔNG đụng CSDL
    // đã quét (đó là nút "Xóa dữ liệu tìm kiếm"). Không cho xóa khi đang chạy.
    private void OnClearFileListClick(object? sender, EventArgs e)
    {
        if (_fileRunning)
        {
            MessageBox.Show("Đang chạy — dừng trước khi xóa danh sách file.", "Tìm theo file", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_filePaths.Count == 0)
        {
            SetFileStatus("Danh sách file đang trống.");
            return;
        }
        if (MessageBox.Show($"Xóa {_filePaths.Count} file khỏi danh sách và đóng các tab tương ứng?\n(Dữ liệu đã quét trong CSDL vẫn giữ.)",
                "Xóa danh sách file", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        _filePaths.Clear();
        _laneToFile.Clear();
        UpdateFileBoxText();
        BuildFileLaneTabs();   // _filePaths trống → xóa sạch tab file
        PersistFilePaths();    // không tự nạp lại lần mở app sau
        SetFileStatus("Đã xóa danh sách file.");
    }

    private void UpdateFileBoxText() =>
        _fileBox.Text = _filePaths.Count == 0 ? ""
            : _filePaths.Count == 1 ? Path.GetFileName(_filePaths[0])
            : $"{_filePaths.Count} file: " + string.Join(", ", _filePaths.Select(Path.GetFileName));

    private void PersistFilePaths()
    {
        _appSettings.Settings.LastFilePaths = _filePaths.ToList();
        _appSettings.SaveSettings();
    }

    // On startup: re-load only remembered files that still exist AND still have pending links
    // (fully-Processed or deleted files are dropped). Their per-link statuses come from the .xlsx,
    // so pressing "Chạy" resumes the remaining work. Returns total pending links restored.
    private int RestoreFilesFromLastSession()
    {
        var kept = new List<string>();
        var totalPending = 0;
        foreach (var path in _appSettings.Settings.LastFilePaths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
            try
            {
                var pending = new LinkFileStore(path).Load().Count(r => !r.IsDone);
                if (pending == 0) continue; // file đã xong hết → bỏ qua
                kept.Add(path);
                totalPending += pending;
            }
            catch { /* file lỗi -> bỏ qua */ }
        }

        if (!kept.SequenceEqual(_appSettings.Settings.LastFilePaths))
        {
            _appSettings.Settings.LastFilePaths = kept;
            _appSettings.SaveSettings();
        }
        if (kept.Count == 0) return 0;

        _filePaths.Clear();
        _filePaths.AddRange(kept);
        UpdateFileBoxText();
        BuildFileLaneTabs();
        SetFileStatus($"Đã nạp lại {kept.Count} file — còn {totalPending} link chưa xử lý. Bấm \"Chạy\" để tiếp tục.");
        return totalPending;
    }

    // Builds one tab per chosen file (links grid + products grid), loading each file's links.
    private void BuildFileLaneTabs()
    {
        _fileLaneTabs.TabPages.Clear();
        _fileLaneUis.Clear();

        foreach (var path in _filePaths)
        {
            var page = new TabPage(Path.GetFileName(path));
            var tlp = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
            tlp.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tlp.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var status = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                Height = 24,
                TextAlign = ContentAlignment.MiddleLeft,
            Text = "Chờ...",
                ForeColor = Color.FromArgb(40, 90, 150),
            };

            var split = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 2 };
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            split.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            split.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            split.Controls.Add(new Label { Text = "Danh sách link", AutoSize = true, Font = new Font(Control.DefaultFont, FontStyle.Bold) }, 0, 0);
            split.Controls.Add(new Label { Text = "Sản phẩm shop đang quét", AutoSize = true, Font = new Font(Control.DefaultFont, FontStyle.Bold) }, 1, 0);

            var linksGrid = NewFileLinksGrid();
            var productsGrid = NewFileProductsGrid();
            split.Controls.Add(linksGrid, 0, 1);
            split.Controls.Add(productsGrid, 1, 1);

            tlp.Controls.Add(status, 0, 0);
            tlp.Controls.Add(split, 0, 1);
            page.Controls.Add(tlp);
            _fileLaneTabs.TabPages.Add(page);

            // Double-click a link -> export that shop.
            linksGrid.CellDoubleClick += (_, _) => OnFileExportClick(null, EventArgs.Empty);

            var ui = new FileLaneUi { Page = page, LinksGrid = linksGrid, ProductsGrid = productsGrid, Status = status, FilePath = path };
            _fileLaneUis.Add(ui);
            // Chọn 1 link (khi KHÔNG chạy) → hiện luôn sản phẩm shop đó đã quét (đọc từ SQLite),
            // nên mở lại file đã tìm là thấy kết quả ngay, không cần bấm Chạy.
            linksGrid.SelectionChanged += (_, _) => ShowShopProductsForSelectedLink(ui);

            try { LoadRowsIntoGrid(linksGrid, new LinkFileStore(path).Load()); }
            catch (Exception ex) { status.Text = "Lỗi đọc file: " + ex.Message; }
        }
    }

    private static int CountPending(DataGridView grid)
    {
        var n = 0;
        foreach (DataGridViewRow row in grid.Rows)
        {
            if (row.IsNewRow) continue;
            var status = Convert.ToString(row.Cells["Status"].Value) ?? "";
            if (!string.Equals(status, LinkFileStore.Processed, StringComparison.OrdinalIgnoreCase)) n++;
        }
        return n;
    }

    private static void LoadRowsIntoGrid(DataGridView grid, IReadOnlyList<LinkFileStore.LinkRow> rows)
    {
        grid.Rows.Clear();
        foreach (var r in rows)
        {
            var idx = grid.Rows.Add(r.RowNumber, r.Link, ShopTextFromLink(r.Link), r.Status);
            grid.Rows[idx].DefaultCellStyle.ForeColor = StatusColor(r.Status, r.IsDone);
        }
    }

    private static string ShopTextFromLink(string link)
    {
        var (shopId, _) = ParseProductIdsFromLink(link);
        return shopId > 0 ? $"shop-{shopId}" : "";
    }

    private static Color StatusColor(string status, bool isDone)
    {
        if (isDone || string.Equals(status, LinkFileStore.Processed, StringComparison.OrdinalIgnoreCase))
            return Color.ForestGreen;
        if (string.Equals(status, LinkFileStore.Processing, StringComparison.OrdinalIgnoreCase))
            return Color.RoyalBlue;
        if (status.StartsWith("Lỗi", StringComparison.OrdinalIgnoreCase))
            return Color.Firebrick;
        if (status.StartsWith("Trùng", StringComparison.OrdinalIgnoreCase))
            return Color.Gray;
        return Color.Black;
    }

    // The file tab whose worker is laneId right now (tracked via LaneAssignedFile), or null.
    private FileLaneUi? FileLaneByPath(string filePath) =>
        _fileLaneUis.FirstOrDefault(l => string.Equals(l.FilePath, filePath, StringComparison.OrdinalIgnoreCase));

    private FileLaneUi? FileLaneForLane(int laneId) =>
        _laneToFile.TryGetValue(laneId, out var path) ? FileLaneByPath(path) : null;

    private readonly Dictionary<int, string> _laneToFile = [];

    private FileLaneUi? ActiveFileLane()
    {
        if (_fileLaneTabs.SelectedTab is not { } tab) return null;
        return _fileLaneUis.FirstOrDefault(l => l.Page == tab);
    }

    private void SetFileLaneLinkStatus(int laneId, int rowNumber, string status, string shopName = "")
    {
        if (FileLaneForLane(laneId) is not { } lane) return;
        foreach (DataGridViewRow row in lane.LinksGrid.Rows)
        {
            if (row.IsNewRow || Convert.ToInt64(row.Cells["Row"].Value) != rowNumber) continue;
            if (!string.IsNullOrWhiteSpace(shopName))
                row.Cells["Shop"].Value = shopName.Trim();
            row.Cells["Status"].Value = status;
            row.DefaultCellStyle.ForeColor = StatusColor(status, false);
            if (string.Equals(status, LinkFileStore.Processing, StringComparison.OrdinalIgnoreCase))
            {
                // Shop mới đang quét trên lane này → lưới phải hiện live shop đó.
                var (sid, _) = ParseProductIdsFromLink(Convert.ToString(row.Cells["Link"].Value) ?? "");
                lane.ShownShopId = sid;
                lane.ProductsGrid.Rows.Clear();
            }
            return;
        }
    }

    // Load the selected link's shop products from SQLite into the lane's products grid — works
    // any time (kể cả đang chạy): click 1 link là xem được sp shop đó (đã quét). Live crawl chỉ ghi
    // vào lưới khi sp đúng shop đang hiển thị (ShownShopId) nên không trộn lẫn.
    private void ShowShopProductsForSelectedLink(FileLaneUi lane)
    {
        var grid = lane.LinksGrid;
        var row = grid.CurrentRow ?? (grid.SelectedRows.Count > 0 ? grid.SelectedRows[0] : null);
        if (row is null) return;
        var (shopId, _) = ParseProductIdsFromLink(Convert.ToString(row.Cells["Link"].Value) ?? "");
        lane.ShownShopId = shopId;
        lane.ProductsGrid.Rows.Clear();
        if (shopId <= 0) return;
        foreach (var p in _taskStore.GetShopProducts(shopId).Where(PassesFileFilter))
            lane.ProductsGrid.Rows.Add(p.Link, p.Name, p.Category, p.PriceVnd.ToString("N0"), p.MonthlySold.ToString());
    }

    // Nạp lại bảng sản phẩm của TẤT CẢ lane đang hiển thị theo filter hiện tại (nút "Áp dụng").
    private void ReloadAllLaneProducts()
    {
        foreach (var lane in _fileLaneUis)
        {
            if (lane.ShownShopId <= 0) continue;
            lane.ProductsGrid.Rows.Clear();
            foreach (var p in _taskStore.GetShopProducts(lane.ShownShopId).Where(PassesFileFilter))
                lane.ProductsGrid.Rows.Add(p.Link, p.Name, p.Category, p.PriceVnd.ToString("N0"), p.MonthlySold.ToString());
        }
    }

    // Filter dùng cho HIỂN THỊ + XUẤT EXCEL (0 = không giới hạn). Crawl vẫn lưu CSDL toàn bộ.
    // Giá: giữ item chưa parse được giá (price=0). Bán/tháng: khoảng [từ, đến], 0 = bỏ chặn đầu đó.
    private bool PassesFileFilter(ProductResult p)
    {
        var minPrice = (long)_fileMinPriceBox.Value;
        var soldFrom = (int)_fileMinSoldFromBox.Value;
        var soldTo   = (int)_fileMinSoldToBox.Value;
        if (minPrice > 0 && p.PriceVnd > 0 && p.PriceVnd < minPrice) return false;
        if (soldFrom > 0 && p.MonthlySold < soldFrom) return false;
        if (soldTo > 0 && p.MonthlySold > soldTo) return false;
        // Danh mục: "(Tất cả danh mục)" = không lọc; ngược lại chỉ giữ đúng danh mục đang chọn.
        var cat = SelectedFileCategory();
        if (cat is not null && !string.Equals(p.Category?.Trim(), cat, StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    /// <summary>Danh mục đang chọn ở tab "Tìm theo file", hoặc null nếu chọn "(Tất cả danh mục)".</summary>
    private string? SelectedFileCategory()
    {
        var sel = _fileCategoryCombo.SelectedItem as string;
        return string.IsNullOrEmpty(sel) || sel == AllCategoriesItem ? null : sel;
    }

    private void AddFileLaneProductRow(int laneId, ProductResult p)
    {
        if (FileLaneForLane(laneId) is not { } lane) return;
        // Chỉ ghi sp của shop ĐANG hiển thị (tránh trộn khi user click xem link khác lúc đang chạy).
        if (lane.ShownShopId != 0 && p.ShopId != lane.ShownShopId) return;
        if (!PassesFileFilter(p)) return; // lọc hiển thị (CSDL vẫn lưu đủ)
        foreach (DataGridViewRow row in lane.ProductsGrid.Rows)
        {
            if (row.IsNewRow) continue;
            if (!string.Equals(Convert.ToString(row.Cells[0].Value), p.Link, StringComparison.OrdinalIgnoreCase))
                continue;
            row.Cells[1].Value = p.Name;
            row.Cells[2].Value = p.Category;
            row.Cells[3].Value = p.PriceVnd.ToString("N0");
            row.Cells[4].Value = p.MonthlySold.ToString();
            return;
        }
        lane.ProductsGrid.Rows.Add(p.Link, p.Name, p.Category, p.PriceVnd.ToString("N0"), p.MonthlySold.ToString());
    }

    // A lane (worker) picks up a file: remember the mapping and refresh that file's tab.
    private void OnFileLaneAssigned(int laneId, string filePath, string accountName, IReadOnlyList<LinkFileStore.LinkRow> rows)
    {
        _laneToFile[laneId] = filePath;
        if (FileLaneByPath(filePath) is not { } lane) return;
        lane.Page.Text = $"{Path.GetFileName(filePath)} · {accountName}";
        lane.Status.Text = $"Tài khoản \"{accountName}\" — {rows.Count(r => !r.IsDone)} link cần xử lý";
        LoadRowsIntoGrid(lane.LinksGrid, rows);
    }

    private void OnFileLaneFileFinished(int laneId, string filePath)
    {
        RefreshCategoriesGrid(); // danh mục được upsert dần khi quét — cập nhật tab "Danh mục"
        RefreshFileCategoryCombo();
        _laneToFile.Remove(laneId);
        if (FileLaneByPath(filePath) is not { } lane) return;
        _fileLaneUis.Remove(lane);
        _fileLaneTabs.TabPages.Remove(lane.Page);
        lane.Page.Dispose();
    }

    // Export the products of the selected link's shop (active file tab). Data comes from SQLite,
    // so it works even after the run finished.
    private void OnFileExportClick(object? sender, EventArgs e)
    {
        if (ActiveFileLane() is not { } lane)
        {
            MessageBox.Show("Chọn file và 1 link để xuất.", "Tìm theo file", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var grid = lane.LinksGrid;
        if (grid.SelectedRows.Count == 0 && grid.CurrentRow is null)
        {
            MessageBox.Show("Chọn 1 link trong danh sách để xuất.", "Tìm theo file", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var row = grid.SelectedRows.Count > 0 ? grid.SelectedRows[0] : grid.CurrentRow!;
        var link = Convert.ToString(row.Cells["Link"].Value) ?? "";
        var (shopId, _) = ParseProductIdsFromLink(link);
        if (shopId <= 0)
        {
            MessageBox.Show("Không nhận diện được shop từ link này.", "Tìm theo file", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var allProducts = _taskStore.GetShopProducts(shopId);
        if (allProducts.Count == 0)
        {
            MessageBox.Show("Shop này chưa có dữ liệu đã quét trong CSDL. Hãy chạy quét link này trước.",
                "Tìm theo file", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        // Lọc theo min bán (từ–đến) + min giá trước khi xuất (CSDL vẫn giữ toàn bộ).
        var products = allProducts.Where(PassesFileFilter).ToList();
        if (products.Count == 0)
        {
            MessageBox.Show($"Shop có {allProducts.Count} sản phẩm nhưng không có sản phẩm nào khớp filter (min bán từ–đến / min giá).",
                "Tìm theo file", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            Directory.CreateDirectory(ShopExportDir);
            var shopName = products.Select(p => p.ShopName).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
            var shopPart = string.IsNullOrWhiteSpace(shopName) ? $"shop-{shopId}" : SanitizeKeepSpaces(shopName);
            var keywordPart = SanitizeKeepSpaces(Path.GetFileNameWithoutExtension(lane.FilePath));
            var fileName = $"{keywordPart}-{shopPart}-{DateTime.Now:yyyy-MM-dd_HHmmss}.xlsx";
            var path = ExcelExporter.Export(products, ShopExportDir, fileName);
            if (MessageBox.Show($"Đã lưu {products.Count} sản phẩm:\n{path}\n\nMở file?", "Xuất Excel",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Lỗi xuất file", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // Export ONE combined file of ALL file-mode products ever scanned (read from SQLite, dedup by
    // itemId) — independent of which files are currently loaded; works any time.
    private void OnFileExportAllClick(object? sender, EventArgs e)
    {
        List<ProductResult> all;
        try { all = _taskStore.GetAllShopProducts(); }
        catch (Exception ex)
        {
            MessageBox.Show("Đọc dữ liệu lỗi: " + ex.Message, "Xuất tất cả", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (all.Count == 0)
        {
            MessageBox.Show("Chưa có sản phẩm nào (từ tìm theo file) trong CSDL để xuất.", "Xuất tất cả", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        // Lọc theo min bán (từ–đến) + min giá trước khi gộp xuất (CSDL vẫn giữ toàn bộ).
        var totalBeforeFilter = all.Count;
        all = all.Where(PassesFileFilter).ToList();
        if (all.Count == 0)
        {
            MessageBox.Show($"Có {totalBeforeFilter} sản phẩm trong CSDL nhưng không sản phẩm nào khớp filter (min bán từ–đến / min giá).",
                "Xuất tất cả", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            Directory.CreateDirectory(ShopExportDir);
            var shopCount = all.Select(p => p.ShopId).Distinct().Count();
            string path;
            lock (_excelLock)
            {
                var baseName = $"tonghop-file-{shopCount}shop-{all.Count}sp-{DateTime.Now:yyyy-MM-dd_HHmmss}";
                var fileName = baseName + ".xlsx";
                var n = 2;
                while (File.Exists(Path.Combine(ShopExportDir, fileName)))
                    fileName = $"{baseName}-{n++}.xlsx";
                path = ExcelExporter.Export(all, ShopExportDir, fileName);
            }
            if (MessageBox.Show($"Đã gộp {all.Count} sản phẩm (đã loại trùng itemId) từ {shopCount} shop của tất cả file:\n{path}\n\nMở file?",
                    "Xuất tất cả", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Lỗi xuất file", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // Mark the selected link's shop for re-scanning: remove it from the scanned-shops set
    // and clear its row status so the next "Chạy" will scan it again.
    private void OnFileRescanClick(object? sender, EventArgs e)
    {
        if (_fileRunning)
        {
            MessageBox.Show("Đang chạy — dừng trước khi đặt quét lại.", "Tìm theo file", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (ActiveFileLane() is not { } lane)
        {
            MessageBox.Show("Chọn file và 1 link để quét lại.", "Tìm theo file", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var grid = lane.LinksGrid;
        if (grid.SelectedRows.Count == 0 && grid.CurrentRow is null)
        {
            MessageBox.Show("Chọn 1 link để quét lại.", "Tìm theo file", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var row = grid.SelectedRows.Count > 0 ? grid.SelectedRows[0] : grid.CurrentRow!;
        var link = Convert.ToString(row.Cells["Link"].Value) ?? "";
        var rowNumber = long.TryParse(Convert.ToString(row.Cells["Row"].Value), out var rn) ? rn : 0;
        var (shopId, _) = ParseProductIdsFromLink(link);
        if (shopId <= 0)
        {
            MessageBox.Show("Không nhận diện được shop từ link này.", "Tìm theo file", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        new ScannedShopStore(ShopExportDir).Remove(shopId);

        if (File.Exists(lane.FilePath) && rowNumber > 0)
        {
            try
            {
                var store = new LinkFileStore(lane.FilePath);
                store.Load(); // sets StatusColumn
                store.MarkStatus((int)rowNumber, "");
            }
            catch (Exception ex) { SetFileStatus("Reset trạng thái file lỗi: " + ex.Message); }
        }

        row.Cells["Status"].Value = "";
        row.Cells["Shop"].Value = ShopTextFromLink(link);
        row.DefaultCellStyle.ForeColor = Color.Black;
        SetFileStatus($"Đã đặt quét lại shop {shopId}. Bấm \"Chạy\" để quét lại (dữ liệu cũ sẽ được cập nhật).");
    }

    private async void OnRunFileClick(object? sender, EventArgs e)
    {
        if (_filePaths.Count == 0)
        {
            MessageBox.Show("Chọn ít nhất 1 file .xlsx trước.", "Tìm theo file", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var accounts = BuildRunAccounts();
        if (accounts.Count == 0)
        {
            MessageBox.Show("Không còn tài khoản khả dụng (tất cả đang ở tab \"Lỗi\"). Khôi phục bớt tài khoản rồi chạy lại.",
                "Tìm theo file", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        PersistFilePaths(); // nhớ để mở app lần sau tự nạp lại + resume
        // Rebuild tabs so every chosen file is visible; active worker count comes from Process.
        BuildFileLaneTabs();
        _laneToFile.Clear();
        var requestedProcesses = (int)_fileProcessBox.Value;
        var laneCount = Math.Min(Math.Min(requestedProcesses, accounts.Count), _filePaths.Count);

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        SetFileUiRunning(true);
        var shopStore = new ScannedShopStore(ShopExportDir);
        _fileShopStore = shopStore;
        SetFileStatus($"Chạy song song: {laneCount} process, {_filePaths.Count} file, {accounts.Count} account.");

        var coordinator = new FileRunCoordinator(
            _appSettings, _taskStore, accounts, _filePaths.ToList(), laneCount, shopStore);
        _fileCoordinator = coordinator;

        coordinator.LaneStatus += (lane, msg) => BeginInvoke(() =>
        {
            if (FileLaneForLane(lane) is { } ui) ui.Status.Text = msg;
            // File-lane logs go to the file tab's own log box only — not the keyword tab's.
            AppendToLogBox(_fileLogBox, $"[{DateTime.Now:HH:mm:ss}] [F{lane}] {msg}{Environment.NewLine}");
        });
        coordinator.LaneAssignedFile += (lane, filePath, accountName, rows) =>
            BeginInvoke(() => OnFileLaneAssigned(lane, filePath, accountName, rows));
        coordinator.LaneLinkStatus += (lane, rowNumber, status, shopName) =>
            BeginInvoke(() => SetFileLaneLinkStatus(lane, rowNumber, status, shopName));
        coordinator.LaneProduct += (lane, product) => BeginInvoke(() => AddFileLaneProductRow(lane, product));
        coordinator.LaneFileFinished += (lane, filePath) => BeginInvoke(() => OnFileLaneFileFinished(lane, filePath));
        coordinator.LaneFinished += lane => BeginInvoke(() => { if (FileLaneForLane(lane) is { } ui) ui.Status.Text = "✓ " + ui.Status.Text; });
        coordinator.AccountsChanged += () => BeginInvoke(RefreshAccountsGrid);
        coordinator.AccountUsed += id => BeginInvoke(() => SetLastUsedAccount(id));
        coordinator.AccountErrored += (id, reason) => BeginInvoke(() => MarkAccountErroredFromRun(id, reason));
        coordinator.SaveShopExcel = (fileKeyword, shopName, results) => Task.Run(() => SaveShopExcelFile(fileKeyword, shopName, results));

        try
        {
            await coordinator.RunAsync(ct);
            SetFileStatus("Tìm theo file: hoàn thành tất cả file.");
        }
        catch (OperationCanceledException)
        {
            SetFileStatus("Tìm theo file: đã dừng.");
        }
        catch (Exception ex)
        {
            SetFileStatus("Tìm theo file lỗi: " + ex.Message);
        }
        finally
        {
            _fileCoordinator = null;
            SetFileUiRunning(false);
        }
    }

    // Saves one shop's products as {keyword}-{shop}-{datetime}.xlsx into D:\shopee-stat\shops. Thread-safe:
    // several lanes may finish a shop at the same time, so guard the filename probe with _excelLock.
    // Tên: {keyword}-{shop}-{datetime}.xlsx — keyword = tên file nguồn (bỏ đuôi).
    private void SaveShopExcelFile(string fileKeyword, string shopName, IReadOnlyList<ProductResult> results)
    {
        if (results.Count == 0) return;
        try
        {
            Directory.CreateDirectory(ShopExportDir);
            string path;
            lock (_excelLock)
            {
                var baseName = $"{SanitizeKeepSpaces(fileKeyword)}-{SanitizeKeepSpaces(shopName)}-{DateTime.Now:yyyy-MM-dd_HHmmss}";
                var fileName = baseName + ".xlsx";
                var n = 2;
                while (File.Exists(Path.Combine(ShopExportDir, fileName)))
                    fileName = $"{baseName}-{n++}.xlsx";
                path = ExcelExporter.Export(results, ShopExportDir, fileName);
            }
            BeginInvoke(() => SetFileStatus($"File: đã lưu {path}"));
        }
        catch (Exception ex)
        {
            BeginInvoke(() => SetFileStatus("File: lỗi lưu Excel: " + ex.Message));
        }
    }

    private static string SanitizeKeepSpaces(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string((value ?? "").Trim().Select(ch => invalid.Contains(ch) ? ' ' : ch).ToArray());
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"\s+", " ").Trim();
        return clean.Length == 0 ? "unknown-shop" : clean;
    }

    // Status for the "Tìm theo file" tab — writes to its OWN label + its own log box, never the
    // keyword tab's _statusLabel (which is why file messages used to bleed into "Tìm với từ khóa").
    private void SetFileStatus(string msg)
    {
        _fileStatusLabel.Text = msg;
        AppendToLogBox(_fileLogBox, $"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}");
        try
        {
            var logPath = Path.Combine(AppContext.BaseDirectory, "shopee-stat.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [FILE] {msg}{Environment.NewLine}");
        }
        catch { }
    }

    private void OnFileStopClick(object? sender, EventArgs e)
    {
        _searchCts?.Cancel();
        _fileCoordinator?.KillAllBrowsers();
        SetFileUiRunning(false);      // _fileRunning=false ngay → có thể bấm ✕ đóng tab liền
        _fileSpinner.Spinning = true; // vẫn quay để báo "đang dừng", tắt khi teardown xong (finally)
        SetFileStatus("⏳ Đang dừng… chờ tắt các tiến trình Edge (có thể mất vài giây). Có thể bấm ✕ để bỏ tab.");
    }

    private void SetFileUiRunning(bool isRunning)
    {
        _fileRunning = isRunning;
        _fileRunBtn.Enabled = !isRunning;
        _chooseFileBtn.Enabled = !isRunning;
        _clearFileListBtn.Enabled = !isRunning;
        _fileRescanBtn.Enabled = !isRunning;
        _clearFileSearchBtn.Enabled = !isRunning;
        _fileProcessBox.Enabled = !isRunning;
        _fileStopBtn.Enabled = isRunning;
        _fileSpinner.Spinning = isRunning;
    }

    private void OnStopClick(object? sender, EventArgs e)
    {
        _searchCts?.Cancel();
        // Parallel auto-run: stop all lanes promptly.
        _coordinator?.KillAllBrowsers();
        // Single-run mode resources.
        _ = _orchestrator?.StopAsync();
        if (_currentTaskId > 0)
        {
            _taskStore.UpdateStatus(_currentTaskId, "Stopped", "Người dùng dừng.");
            RefreshTasksGrid();
        }
        _edge.Kill();
        _ = DisposeCdpInputAsync();
        _ws?.Dispose();
        _ws = null;
        ResetButtons();
        if (_laneTabs is null || !_laneTabs.Visible)
            _exportBtn.Enabled = GetExportResults().Count > 0;
        SetStatus("Đã dừng.");
    }

    private void OnExportClick(object? sender, EventArgs e)
    {
        var results = GetExportResults();
        if (results.Count == 0)
        {
            MessageBox.Show("Không có dữ liệu để xuất.", "Shopee Stat", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var dir = PromptExportFolder();
        if (dir is null) return;

        var fileName = StatExportFileName(GetSelectedKeyword());

        try
        {
            var path = ExcelExporter.Export(results, dir, fileName);
            if (MessageBox.Show($"Đã lưu:\n{path}\n\nMở file?", "Xuất xong",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Lỗi xuất file", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // Xuất 1 file TỔNG HỢP cho tất cả từ khóa: gộp toàn bộ sản phẩm keyword từ SQLite, loại trùng
    // theo itemId (shopId có thể trùng giữa các từ khóa, itemId thì không).
    private void OnExportAllKeywordsClick(object? sender, EventArgs e)
    {
        List<ProductResult> all;
        try { all = _taskStore.GetAllKeywordProducts(); }
        catch (Exception ex)
        {
            MessageBox.Show("Đọc dữ liệu lỗi: " + ex.Message, "Xuất tất cả", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (all.Count == 0)
        {
            MessageBox.Show("Chưa có sản phẩm nào trong CSDL để xuất.", "Xuất tất cả", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            Directory.CreateDirectory(KeywordsExportDir);
            string path;
            lock (_excelLock)
            {
                var baseName = $"tonghop-tatca-{all.Count}sp-{DateTime.Now:yyyyMMdd_HHmmss}";
                var fileName = baseName + ".xlsx";
                var n = 2;
                while (File.Exists(Path.Combine(KeywordsExportDir, fileName)))
                    fileName = $"{baseName}-{n++}.xlsx";
                path = ExcelExporter.Export(all, KeywordsExportDir, fileName);
            }
            if (MessageBox.Show($"Đã gộp {all.Count} sản phẩm (đã loại trùng itemId) từ tất cả từ khóa:\n{path}\n\nMở file?",
                    "Xuất tất cả", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Lỗi xuất file", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ── Account CRUD ──────────────────────────────────────────────────────────

    private async void OnResumeFailedClick(object? sender, EventArgs e)
    {
        if (_lastFailedTaskId <= 0)
        {
            MessageBox.Show("Chưa có task lỗi để resume.", "Shopee Stat", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        await ResumeTaskAsync(_lastFailedTaskId);
    }

    private async void OnResumeTaskClick(object? sender, EventArgs e)
    {
        var taskId = GetSelectedTaskId();
        if (taskId <= 0) return;
        await ResumeTaskAsync(taskId);
    }

    private async void OnResearchTaskClick(object? sender, EventArgs e)
    {
        var taskId = GetSelectedTaskId();
        if (taskId <= 0) return;

        var task = _taskStore.GetTask(taskId);
        if (task is null) return;

        if (MessageBox.Show($"Research lại task #{taskId} từ đầu?\nDữ liệu cũ của task sẽ bị xóa.",
                "Research task", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        await RunTaskFromRecordAsync(task, resume: false, research: true);
    }

    private void OnExportTaskClick(object? sender, EventArgs e)
    {
        var taskId = GetSelectedTaskId();
        if (taskId <= 0) return;

        var results = _taskStore.GetProducts(taskId);
        if (results.Count == 0)
        {
            MessageBox.Show("Task này chưa có dữ liệu để xuất.", "Shopee Stat", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var task = _taskStore.GetTask(taskId);
        var dir = PromptExportFolder();
        if (dir is null) return;

        var fileName = StatExportFileName(task?.Keyword ?? "");

        try
        {
            var path = ExcelExporter.Export(results, dir, fileName);
            if (MessageBox.Show($"Đã lưu:\n{path}\n\nMở file?", "Xuất task",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Lỗi xuất file", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task ResumeTaskAsync(long taskId)
    {
        var task = _taskStore.GetTask(taskId);
        if (task is null)
        {
            MessageBox.Show("Không tìm thấy task.", "Shopee Stat", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        await RunTaskFromRecordAsync(task, resume: true, research: false);
    }

    private async Task RunTaskFromRecordAsync(SearchTaskRecord task, bool resume, bool research)
    {
        var account = _accountCombo.SelectedItem as InstanceConfig
            ?? FindAccountForTask(task)
            ?? _appSettings.Settings.Instances.FirstOrDefault();

        if (account is null)
        {
            MessageBox.Show("Chưa có tài khoản để chạy task.", "Shopee Stat", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        _grid.Rows.Clear();
        if (!research)
            LoadTaskProductsIntoGrid(task.Id);
        UpdateResultCountLabel();
        _exportBtn.Enabled = _grid.Rows.Count > 0;

        SelectKeywordInCombo(task.Keyword);
        _locationBox.Text = task.RegionFilterText;

        SetSearchUiRunning(true);
        try
        {
            var mode = research ? "research" : "resume";
            SetStatus($"Task #{task.Id}: {mode} bằng tài khoản \"{account.DisplayName}\".");
            var outcome = await RunSearchSessionAsync(
                account,
                task.Keyword,
                autoMode: false,
                ct,
                task.Id,
                resumeExistingTask: resume,
                researchExistingTask: research);

            if (outcome != SearchRunOutcome.Completed)
            {
                _edge.Kill();
                _ = DisposeCdpInputAsync();
                _ws?.Dispose();
                _ws = null;
                ResetButtons();
                _exportBtn.Enabled = GetExportResults().Count > 0;
            }
        }
        catch (OperationCanceledException)
        {
            ResetButtons();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Lỗi task", MessageBoxButtons.OK, MessageBoxIcon.Error);
            ResetButtons();
        }
    }

    private void OnImportClick(object? sender, EventArgs e)
    {
        using var form = new ImportManagerForm();
        if (form.ShowDialog(this) != DialogResult.OK) return;

        var accounts = SplitImportLines(form.ShopeeAccountsText, shopee: true);
        var proxies = SplitImportLines(form.ProxyKeysText, shopee: false);

        if (accounts.Count == 0)
        {
            if (proxies.Count == 0) return;
            if (_appSettings.Settings.Instances.Count == 0)
            {
                MessageBox.Show("Chưa có tài khoản để gán proxy.", "Shopee Stat",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            for (var i = 0; i < _appSettings.Settings.Instances.Count; i++)
            {
                var cfg = _appSettings.Settings.Instances[i];
                cfg.KiotProxyKey = proxies[i % proxies.Count];
                cfg.ManualProxy = "";
            }

            _appSettings.SaveSettings();
            RefreshAccountCombo();
            RefreshAccountsGrid();
            SetStatus($"Đã dàn lại {proxies.Count} proxy cho {_appSettings.Settings.Instances.Count} tài khoản.");
            return;
        }

        for (var i = 0; i < accounts.Count; i++)
        {
            var cfg = new InstanceConfig
            {
                ShopeeAccountLogin = accounts[i],
                KiotProxyKey = proxies.Count > 0 ? proxies[i % proxies.Count] : "",
                CreateNewProfileOnNextStart = true,
                OpenWithShopeeAccount = true, // always need login on first use
            };
            cfg.EnsureProfileRelativePath();
            _appSettings.Settings.Instances.Add(cfg);
        }

        _appSettings.SaveSettings();
        RefreshAccountCombo();
        RefreshAccountsGrid();
    }

    private void OnAddAccountClick(object? sender, EventArgs e)
    {
        using var form = new AccountEditForm();
        if (form.ShowDialog(this) != DialogResult.OK) return;

        var cfg = form.Result;
        cfg.EnsureProfileRelativePath();
        _appSettings.Settings.Instances.Add(cfg);
        _appSettings.SaveSettings();
        RefreshAccountCombo();
        RefreshAccountsGrid();
    }

    private void OnEditAccountClick(object? sender, EventArgs e)
    {
        var grid = ActiveAccountsGrid();
        if (grid.SelectedRows.Count == 0 || grid.SelectedRows[0].Tag is not InstanceConfig config) return;

        using var form = new AccountEditForm(config);
        if (form.ShowDialog(this) != DialogResult.OK) return;

        // AccountEditForm mutates config in-place via Result (same object reference)
        config.EnsureProfileRelativePath();
        _appSettings.SaveSettings();
        RefreshAccountCombo();
        RefreshAccountsGrid();
    }

    private void OnDeleteAccountClick(object? sender, EventArgs e)
    {
        var selected = SelectedAccounts(ActiveAccountsGrid());
        if (selected.Count == 0) return;

        var prompt = selected.Count == 1
            ? $"Xóa tài khoản \"{selected[0].DisplayName}\"?"
            : $"Xóa {selected.Count} tài khoản đã chọn?";
        if (MessageBox.Show(prompt, "Xác nhận", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        foreach (var acc in selected)
            _appSettings.Settings.Instances.Remove(acc);

        _appSettings.SaveSettings();
        RefreshAccountCombo();
        RefreshAccountsGrid();
    }

    // "Đánh dấu lỗi →": chuyển account đang chọn (tab Bình thường) sang tab Lỗi để khỏi dùng tiếp.
    private void OnMarkAccountErrorClick(object? sender, EventArgs e)
    {
        var selected = SelectedAccounts(_accountsGrid);
        if (selected.Count == 0)
        {
            MessageBox.Show("Chọn tài khoản ở tab \"Bình thường\" để đánh dấu lỗi.",
                "Shopee Stat", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        foreach (var acc in selected)
        {
            acc.IsError = true;
            if (string.IsNullOrWhiteSpace(acc.ErrorReason)) acc.ErrorReason = "Đánh dấu thủ công";
            acc.ErrorAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }
        _appSettings.SaveSettings();
        RefreshAccountCombo();
        RefreshAccountsGrid();
    }

    // "← Khôi phục": đưa account đang chọn (tab Lỗi) về lại tab Bình thường để dùng tiếp.
    private void OnExportAccountClick(object? sender, EventArgs e)
    {
        var normalAccounts = _appSettings.Settings.Instances
            .Where(a => !a.IsError)
            .ToList();
        using var form = new ExportAccountForm(normalAccounts);
        if (form.ShowDialog(this) == DialogResult.OK)
            SetStatus($"Đã export {normalAccounts.Count} tài khoản bình thường.");
    }

    private void OnRecoverAccountClick(object? sender, EventArgs e)
    {
        var selected = SelectedAccounts(_errorAccountsGrid);
        if (selected.Count == 0)
        {
            MessageBox.Show("Chọn tài khoản ở tab \"Lỗi\" để khôi phục.",
                "Shopee Stat", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        foreach (var acc in selected)
        {
            acc.IsError = false;
            acc.ErrorReason = "";
            acc.ErrorAt = "";
        }
        _appSettings.SaveSettings();
        RefreshAccountCombo();
        RefreshAccountsGrid();
    }

    // "Mở giải captcha": mở Edge với CHÍNH profile của tài khoản lỗi (đang chọn ở tab Lỗi) tới
    // shopee.vn để user tự giải verify/captcha. Account lỗi đã bị loại khỏi pool nên không đụng các
    // lane đang chạy. Giải xong → bấm "← Khôi phục" để đưa account về lại tab Bình thường.
    private async void OnSolveCaptchaClick(object? sender, EventArgs e)
    {
        var selected = SelectedAccounts(_errorAccountsGrid);
        if (selected.Count == 0)
        {
            MessageBox.Show("Chọn tài khoản ở tab \"Lỗi\" để mở giải captcha.",
                "Shopee Stat", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (selected.Count > 1)
        {
            MessageBox.Show("Mỗi lần chỉ mở 1 tài khoản để giải captcha. Chọn 1 tài khoản.",
                "Shopee Stat", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var account = selected[0];
        try
        {
            SetStatus($"Mở Edge giải captcha cho \"{account.DisplayName}\"...");
            var port = _appSettings.Settings.WsPort;
            var profileDir = _appSettings.GetProfileDir(account);
            var proxy = await _edge.ResolveProxyAsync(account);
            _edge.Launch(account, profileDir, proxy, port);
            SetStatus("Edge đang khởi động...");
            await _edge.CleanupRestoredTabsAsync(port);

            // Nếu profile chưa đăng nhập thì đăng nhập luôn để user check tài khoản ngay
            // (EnsureLoggedInAsync tự kiểm tra "đã login chưa" → chỉ login khi chưa, không re-login).
            // Login lỗi cũng không chặn: cứ để cửa sổ mở cho user tự xử lý.
            try
            {
                SetStatus($"Đang kiểm tra/đăng nhập tài khoản \"{account.DisplayName}\"...");
                var loginSvc = new ShopeeLoginService(_appSettings);
                await loginSvc.EnsureLoggedInAsync(account, _edge.CdpPort, SetStatus);
                RefreshAccountsGrid();
            }
            catch (Exception loginEx)
            {
                SetStatus("Đăng nhập tự động lỗi (vẫn mở Edge để giải tay): " + loginEx.Message);
            }

            SetStatus($"Đã mở Edge (profile \"{account.DisplayName}\"). Tự giải verify/captcha trên trang Shopee, xong bấm \"← Khôi phục\".");
            MessageBox.Show(
                $"Đã mở Edge với tài khoản \"{account.DisplayName}\".\n\n" +
                "1. Tài khoản đã được đăng nhập sẵn (nếu chưa login) — kiểm tra tài khoản tại đây.\n" +
                "2. Giải verify/captcha trên trang Shopee vừa mở.\n" +
                "3. Khi xong, quay lại đây bấm \"← Khôi phục\" để dùng lại tài khoản này.",
                "Giải captcha", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            SetStatus("Mở giải captcha lỗi: " + ex.Message);
            MessageBox.Show(ex.Message, "Mở giải captcha", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>Cập nhật con trỏ "account dùng sau cùng" (gọi từ coordinator, đã marshal về UI thread).</summary>
    private void SetLastUsedAccount(string accountId)
    {
        if (string.IsNullOrEmpty(accountId)) return;
        if (string.Equals(_appSettings.Settings.LastUsedAccountId, accountId, StringComparison.OrdinalIgnoreCase)) return;
        _appSettings.Settings.LastUsedAccountId = accountId;
        _appSettings.SaveSettings();
    }

    /// <summary>Coordinator báo account dính verify/captcha → chuyển sang tab Lỗi (đã marshal về UI thread).</summary>
    private void MarkAccountErroredFromRun(string accountId, string reason)
    {
        var acc = _appSettings.Settings.Instances.FirstOrDefault(a =>
            string.Equals(a.Id, accountId, StringComparison.OrdinalIgnoreCase));
        if (acc is null || acc.IsError) return;
        acc.IsError = true;
        acc.ErrorReason = string.IsNullOrWhiteSpace(reason) ? "Verify/captcha" : reason;
        acc.ErrorAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        _appSettings.SaveSettings();
        RefreshAccountCombo();
        RefreshAccountsGrid();
    }

    /// <summary>
    /// Account pool cho một lượt chạy song song: bỏ account đang ở tab "Lỗi", và xoay danh sách để
    /// bắt đầu từ account NGAY SAU account dùng sau cùng (round-robin bền qua các lần dừng/chạy lại).
    /// </summary>
    private List<InstanceConfig> BuildRunAccounts()
    {
        var pool = _appSettings.Settings.Instances.Where(a => !a.IsError).ToList();
        if (pool.Count <= 1) return pool;

        var lastId = _appSettings.Settings.LastUsedAccountId;
        if (string.IsNullOrEmpty(lastId)) return pool;

        var idx = pool.FindIndex(a => string.Equals(a.Id, lastId, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return pool; // account dùng lần trước đã bị xóa/đánh lỗi → giữ nguyên thứ tự

        var start = (idx + 1) % pool.Count;
        return pool.Skip(start).Concat(pool.Take(start)).ToList();
    }

    // ── Config ────────────────────────────────────────────────────────────────

    private void OnImportKeywordsClick(object? sender, EventArgs e)
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Import từ khóa từ file .txt",
            Filter = "Text (*.txt)|*.txt|All files (*.*)|*.*",
            Multiselect = false,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        string text;
        try
        {
            text = File.ReadAllText(dlg.FileName, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Không đọc được file: " + ex.Message, "Import từ khóa", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var seenInFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uniqueFromFile = new List<string>();
        var dupInFile = 0;
        foreach (var line in ParseKeywordLines(text))
        {
            if (!seenInFile.Add(line))
            {
                dupInFile++;
                continue;
            }
            uniqueFromFile.Add(line);
        }

        if (uniqueFromFile.Count == 0)
        {
            MessageBox.Show("File không có từ khóa hợp lệ.", "Import từ khóa", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var existing = new HashSet<string>(_appSettings.Settings.Keywords, StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var dupExisting = 0;
        foreach (var keyword in uniqueFromFile)
        {
            if (!existing.Add(keyword))
            {
                dupExisting++;
                continue;
            }
            _appSettings.Settings.Keywords.Add(keyword);
            added++;
        }

        if (added == 0)
        {
            MessageBox.Show(
                $"Không thêm từ khóa mới.\nTrùng trong file: {dupInFile}\nĐã có trong danh sách: {dupExisting}",
                "Import từ khóa", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _appSettings.SaveSettings();
        RefreshKeywordCombo();
        RefreshKeywordsGrid();
        MessageBox.Show(
            $"Đã thêm {added} từ khóa.\nBỏ qua trùng trong file: {dupInFile}\nBỏ qua đã có sẵn: {dupExisting}",
            "Import từ khóa", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void OnAddKeywordClick(object? sender, EventArgs e)
    {
        var keyword = PromptKeyword("Thêm từ khóa", "");
        if (string.IsNullOrWhiteSpace(keyword)) return;

        if (_appSettings.Settings.Keywords.Any(x => string.Equals(x, keyword, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("Từ khóa đã tồn tại.", "Shopee Stat", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _appSettings.Settings.Keywords.Add(keyword);
        _appSettings.SaveSettings();
        RefreshKeywordCombo(keyword);
        RefreshKeywordsGrid();
    }

    private void OnEditKeywordClick(object? sender, EventArgs e)
    {
        if (_keywordsGrid.SelectedRows.Count == 0) return;
        var idx = _keywordsGrid.SelectedRows[0].Index;
        if (idx < 0 || idx >= _appSettings.Settings.Keywords.Count) return;

        var keyword = PromptKeyword("Sửa từ khóa", _appSettings.Settings.Keywords[idx]);
        if (string.IsNullOrWhiteSpace(keyword)) return;

        if (_appSettings.Settings.Keywords.Where((_, i) => i != idx)
            .Any(x => string.Equals(x, keyword, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("Từ khóa đã tồn tại.", "Shopee Stat", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _appSettings.Settings.Keywords[idx] = keyword;
        var oldKeyword = _keywordsGrid.SelectedRows[0].Cells["Keyword"].Value?.ToString() ?? "";
        if (_appSettings.Settings.UsedKeywords.RemoveAll(x => string.Equals(x, oldKeyword, StringComparison.OrdinalIgnoreCase)) > 0)
            _appSettings.Settings.UsedKeywords.Add(keyword);
        _appSettings.SaveSettings();
        RefreshKeywordCombo(keyword);
        RefreshKeywordsGrid();
    }

    private void OnDeleteKeywordClick(object? sender, EventArgs e)
    {
        // Delete ALL selected rows (by keyword value, not index — robust to multi-select).
        var toDelete = _keywordsGrid.SelectedRows.Cast<DataGridViewRow>()
            .Select(r => r.Cells["Keyword"].Value?.ToString() ?? "")
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (toDelete.Count == 0) return;

        var prompt = toDelete.Count == 1
            ? $"Xóa từ khóa \"{toDelete.First()}\"?"
            : $"Xóa {toDelete.Count} từ khóa đã chọn?";
        if (MessageBox.Show(prompt, "Xác nhận", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        _appSettings.Settings.Keywords.RemoveAll(k => toDelete.Contains(k));
        _appSettings.Settings.UsedKeywords.RemoveAll(k => toDelete.Contains(k));
        if (_appSettings.Settings.Keywords.Count == 0)
            _appSettings.Settings.Keywords.Add("giày nữ");

        _appSettings.SaveSettings();
        RefreshKeywordCombo();
        RefreshKeywordsGrid();
    }

    private void OnMarkKeywordUnusedClick(object? sender, EventArgs e)
    {
        if (_keywordsGrid.SelectedRows.Count == 0) return;

        var reset = 0;
        foreach (DataGridViewRow row in _keywordsGrid.SelectedRows)
        {
            var keyword = row.Cells["Keyword"].Value?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(keyword)) continue;

            reset += _appSettings.Settings.UsedKeywords
                .RemoveAll(x => string.Equals(x, keyword, StringComparison.OrdinalIgnoreCase));
        }

        if (reset == 0) return;

        _appSettings.SaveSettings();
        RefreshKeywordCombo(GetSelectedKeyword());
        RefreshKeywordsGrid();
        SetStatus($"Đã đặt lại {reset} từ khóa về chưa dùng.");
    }

    private string? PromptKeyword(string title, string value)
    {
        using var form = new Form
        {
            Text = title,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(420, 120),
        };
        var label = new Label { Text = "Từ khóa:", Left = 12, Top = 18, AutoSize = true };
        var textBox = new TextBox { Left = 80, Top = 14, Width = 320, Text = value };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 230, Top = 65, Width = 80 };
        var cancel = new Button { Text = "Hủy", DialogResult = DialogResult.Cancel, Left = 320, Top = 65, Width = 80 };
        form.Controls.AddRange([label, textBox, ok, cancel]);
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        return form.ShowDialog(this) == DialogResult.OK ? textBox.Text.Trim() : null;
    }

    // ── UI helpers ────────────────────────────────────────────────────────────

    private void ShowCaptchaAlert()
    {
        _searchSpinner.Spinning = false;
        SetStatus("⚠ CAPTCHA! Giải captcha trong Edge rồi nhấn OK để tiếp tục.");
        if (MessageBox.Show(
            "Shopee yêu cầu xác minh captcha.\nGiải captcha trong cửa sổ Edge rồi nhấn OK.",
            "Captcha", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK)
        {
            _searchSpinner.Spinning = true;
            if (_currentTaskId > 0)
                _taskStore.UpdateStatus(_currentTaskId, "Running");
            // Re-issue the search from the latest checkpoint; the extension's
            // startSearch() stops the dead crawl loop and resumes at the saved category.
            _ = _orchestrator?.ResumeFromCheckpointAsync();
        }
        else
        {
            OnStopClick(null, EventArgs.Empty);
        }
    }

    private void OnSearchCompleted()
    {
        _searchSpinner.Spinning = false;
        _exportBtn.Enabled = GetExportResults().Count > 0;
        ResetButtons();
    }

    private void ResetButtons()
    {
        SetSearchUiRunning(false);
    }

    private void SetSearchUiRunning(bool isRunning)
    {
        _startBtn.Enabled = !isRunning;
        _autoBtn.Enabled = !isRunning;
        _resumeFailedBtn.Enabled = !isRunning && _lastFailedTaskId > 0;
        _clearKeywordSearchBtn.Enabled = !isRunning;
        _pauseBtn.Enabled = isRunning;
        _stopBtn.Enabled = isRunning;
        _searchSpinner.Spinning = isRunning;

        if (!isRunning)
        {
            // Leaving the running state clears any pause toggle.
            _isPaused = false;
            _pauseBtn.Text = "⏸ Tạm dừng";
        }
    }

    private void OnPauseClick(object? sender, EventArgs e)
    {
        if (_orchestrator is null) return;

        if (!_isPaused)
        {
            _isPaused = true;
            _pauseBtn.Text = "▶ Tiếp tục";
            _searchSpinner.Spinning = false;
            _ = _orchestrator.PauseAsync();
            SetStatus("Đã tạm dừng (Edge vẫn mở). Bấm Tiếp tục để chạy lại.");
        }
        else
        {
            _isPaused = false;
            _pauseBtn.Text = "⏸ Tạm dừng";
            _searchSpinner.Spinning = true;
            _ = _orchestrator.ResumeAsync();
            SetStatus("Tiếp tục quá trình đang thực hiện...");
        }
    }

    private void SetStatus(string msg)
    {
        _statusLabel.Text = msg;
        AppendLog(msg);
        try
        {
            var logPath = Path.Combine(AppContext.BaseDirectory, "shopee-stat.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}{Environment.NewLine}");
        }
        catch { }
    }

    private void AppendLog(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}";
        AppendToLogBox(_logBox, line);
        AppendToLogBox(_fileLogBox, line);
    }

    private static void AppendToLogBox(TextBox box, string line)
    {
        if (box.IsDisposed) return;
        box.AppendText(line);
        if (box.TextLength > 80_000)
            box.Text = box.Text[^60_000..];
        box.SelectionStart = box.TextLength;
        box.ScrollToCaret();
    }

    private void AddProductRow(ProductResult p)
    {
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.IsNewRow) continue;
            if (!string.Equals(Convert.ToString(row.Cells[0].Value), p.Link, StringComparison.OrdinalIgnoreCase))
                continue;

            row.Cells[1].Value = p.Name;
            row.Cells[2].Value = p.PriceVnd.ToString("N0");
            row.Cells[3].Value = p.MonthlySold.ToString();
            row.Cells[4].Value = p.Rating.ToString("0.0");
            row.Cells[5].Value = p.Category;
            row.Cells[6].Value = p.ShopLocation;
            UpdateResultCountLabel();
            _exportBtn.Enabled = _grid.Rows.Count > 0;
            return;
        }

        _grid.Rows.Add(p.Link, p.Name,
            p.PriceVnd.ToString("N0"), p.MonthlySold.ToString(),
            p.Rating.ToString("0.0"), p.Category, p.ShopLocation);
        UpdateResultCountLabel();
        _exportBtn.Enabled = _grid.Rows.Count > 0;
    }

    private void UpdateResultCountLabel()
    {
        _resultCountLabel.Text = $"Đã lấy về {_grid.Rows.Count} sản phẩm";
    }

    private IReadOnlyList<ProductResult> GetExportResults()
    {
        var results = new List<ProductResult>();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.IsNewRow) continue;

            var link = Convert.ToString(row.Cells["Link"].Value) ?? "";
            var (shopId, itemId) = ParseProductIdsFromLink(link);
            results.Add(new ProductResult
            {
                ShopId = shopId,
                ItemId = itemId,
                Name = Convert.ToString(row.Cells["Name"].Value) ?? "",
                PriceVnd = ParseDecimalCell(row.Cells["Price"].Value),
                MonthlySold = ParseIntCell(row.Cells["Sold"].Value),
                Rating = (double)ParseDecimalCell(row.Cells["Rating"].Value),
                Category = Convert.ToString(row.Cells["Cat"].Value) ?? "",
                ShopLocation = Convert.ToString(row.Cells["Location"].Value) ?? "",
            });
        }

        return results.Count > 0 ? results : _orchestrator?.Results ?? [];
    }

    private static (long ShopId, long ItemId) ParseProductIdsFromLink(string link)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            link ?? "",
            @"/product/(\d+)/(\d+)|-i\.(\d+)\.(\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success) return (0, 0);

        var shopId = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[3].Value;
        var itemId = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[4].Value;
        return (long.TryParse(shopId, out var s) ? s : 0, long.TryParse(itemId, out var i) ? i : 0);
    }

    private static decimal ParseDecimalCell(object? value)
    {
        var text = Convert.ToString(value) ?? "";
        text = text.Replace(",", "").Trim();
        return decimal.TryParse(text, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var number)
            ? number
            : 0;
    }

    private static int ParseIntCell(object? value) =>
        (int)Math.Min(int.MaxValue, ParseDecimalCell(value));

    private void OnResultsGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.C)
        {
            CopySelectedResultLinks();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.Control && e.KeyCode == Keys.V)
        {
            SetStatus("Grid chỉ hỗ trợ copy link, không paste vào bảng.");
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private void OnResultsGridCellMouseDown(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right || e.RowIndex < 0) return;

        _grid.ClearSelection();
        _grid.Rows[e.RowIndex].Selected = true;
        _grid.CurrentCell = _grid.Rows[e.RowIndex].Cells[Math.Max(0, e.ColumnIndex)];

        var menu = new ContextMenuStrip();
        menu.Items.Add("Copy link", null, (_, _) => CopySelectedResultLinks());
        menu.Show(_grid, _grid.PointToClient(Cursor.Position));
    }

    private void CopySelectedResultLinks()
    {
        var links = _grid.SelectedRows
            .Cast<DataGridViewRow>()
            .Select(r => r.Cells["Link"].Value?.ToString() ?? "")
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct()
            .ToList();

        if (links.Count == 0 && _grid.CurrentRow is not null)
        {
            var link = _grid.CurrentRow.Cells["Link"].Value?.ToString() ?? "";
            if (!string.IsNullOrWhiteSpace(link))
                links.Add(link);
        }

        if (links.Count == 0) return;

        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, links));
            SetStatus($"Đã copy {links.Count} link.");
        }
        catch (Exception ex)
        {
            SetStatus("Copy lỗi: " + ex.Message);
        }
    }

    private void RefreshAccountCombo()
    {
        _accountCombo.Items.Clear();
        // Bỏ account đang ở tab "Lỗi" — khôi phục trước rồi mới chọn để chạy đơn.
        foreach (var inst in _appSettings.Settings.Instances.Where(a => !a.IsError))
            _accountCombo.Items.Add(inst);
        _accountCombo.DisplayMember = "DisplayName";
        if (_accountCombo.Items.Count > 0)
            _accountCombo.SelectedIndex = 0;
    }

    private void RefreshKeywordCombo(string? selectKeyword = null)
    {
        if (_appSettings.Settings.Keywords.Count == 0)
            _appSettings.Settings.Keywords.Add("giày nữ");

        var selected = selectKeyword
            ?? GetSelectedKeyword()
            ?? _appSettings.Settings.Keywords.FirstOrDefault();

        _keywordBox.Items.Clear();
        foreach (var keyword in _appSettings.Settings.Keywords
                     .Where(x => !string.IsNullOrWhiteSpace(x))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            _keywordBox.Items.Add(new KeywordComboItem(keyword, IsKeywordUsed(keyword)));
        }

        if (_keywordBox.Items.Count == 0) return;

        var selectedIndex = 0;
        if (!string.IsNullOrWhiteSpace(selected))
        {
            for (var i = 0; i < _keywordBox.Items.Count; i++)
            {
                if (!string.Equals(_keywordBox.Items[i]?.ToString(), selected, StringComparison.OrdinalIgnoreCase))
                    continue;

                selectedIndex = i;
                break;
            }
        }

        _keywordBox.SelectedIndex = selectedIndex;
    }

    private void RefreshKeywordsGrid()
    {
        if (_keywordsGrid.IsDisposed) return;

        // "Chưa kết thúc" = có task còn dở (Running/Failed/Stopped) cho từ khóa.
        HashSet<string> resumable;
        try { resumable = _taskStore.GetResumableKeywords(); }
        catch { resumable = new HashSet<string>(StringComparer.OrdinalIgnoreCase); }

        _keywordsGrid.Rows.Clear();
        foreach (var keyword in _appSettings.Settings.Keywords)
        {
            string status;
            Color color;
            if (_activeKeywords.Contains(keyword)) { status = "Đang tìm kiếm"; color = Color.RoyalBlue; }
            else if (IsKeywordUsed(keyword)) { status = "Đã hoàn thành"; color = Color.ForestGreen; }
            else if (resumable.Contains(keyword)) { status = "Chưa kết thúc"; color = Color.DarkOrange; }
            else { status = ""; color = Color.Black; }

            var rowIndex = _keywordsGrid.Rows.Add(keyword, status);
            _keywordsGrid.Rows[rowIndex].DefaultCellStyle.ForeColor = color;
        }
    }

    private void RefreshTasksGrid()
    {
        if (_tasksGrid.IsDisposed) return;

        var selectedId = GetSelectedTaskId();
        _tasksGrid.Rows.Clear();
        foreach (var task in _taskStore.GetTasks())
        {
            var checkpoint = string.IsNullOrWhiteSpace(task.CurrentCategory)
                ? $"category {Math.Max(1, task.ResumeCategoryIndex)}"
                : $"{task.CurrentCategory} / page {task.CurrentPage}";
            var rowIndex = _tasksGrid.Rows.Add(
                task.Id,
                task.Keyword,
                task.AccountName,
                task.Status,
                checkpoint,
                task.ProductCount,
                task.UpdatedAt == DateTime.MinValue ? "" : task.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                task.LastError);

            var row = _tasksGrid.Rows[rowIndex];
            row.Tag = task.Id;
            row.DefaultCellStyle.ForeColor = task.Status switch
            {
                "Completed" => Color.ForestGreen,
                "Failed" => Color.Firebrick,
                "Stopped" => Color.DarkOrange,
                "Running" => Color.RoyalBlue,
                _ => Color.Black,
            };
        }

        if (selectedId > 0)
        {
            foreach (DataGridViewRow row in _tasksGrid.Rows)
            {
                if (Convert.ToInt64(row.Cells["Id"].Value) != selectedId) continue;
                row.Selected = true;
                _tasksGrid.CurrentCell = row.Cells[0];
                break;
            }
        }
    }

    private long GetSelectedTaskId()
    {
        if (_tasksGrid.SelectedRows.Count == 0) return 0;
        var value = _tasksGrid.SelectedRows[0].Cells["Id"].Value;
        return long.TryParse(Convert.ToString(value), out var id) ? id : 0;
    }

    private InstanceConfig? FindAccountForTask(SearchTaskRecord task) =>
        _appSettings.Settings.Instances.FirstOrDefault(x =>
            string.Equals(x.Id, task.AccountId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(x.DisplayName, task.AccountName, StringComparison.OrdinalIgnoreCase));

    private void LoadTaskProductsIntoGrid(long taskId)
    {
        foreach (var product in _taskStore.GetProducts(taskId))
            AddProductRow(product);
    }

    private void SelectKeywordInCombo(string keyword)
    {
        RefreshKeywordCombo(keyword);
        for (var i = 0; i < _keywordBox.Items.Count; i++)
        {
            if (_keywordBox.Items[i] is KeywordComboItem item &&
                string.Equals(item.Keyword, keyword, StringComparison.OrdinalIgnoreCase))
            {
                _keywordBox.SelectedIndex = i;
                return;
            }
        }
    }

    private string GetSelectedKeyword() =>
        _keywordBox.SelectedItem is KeywordComboItem item
            ? item.Keyword.Trim()
            : (_keywordBox.Text ?? "").TrimStart('✓').Trim();

    private bool IsKeywordUsed(string keyword) =>
        _appSettings.Settings.UsedKeywords.Any(x => string.Equals(x, keyword, StringComparison.OrdinalIgnoreCase));

    private void MarkKeywordUsed(string keyword)
    {
        if (IsKeywordUsed(keyword)) return;

        _appSettings.Settings.UsedKeywords.Add(keyword);
        _appSettings.SaveSettings();
        RefreshKeywordCombo(keyword);
        RefreshKeywordsGrid();
    }

    private void RefreshAccountsGrid()
    {
        if (_accountsGrid.IsDisposed || _errorAccountsGrid.IsDisposed) return;

        _accountsGrid.Rows.Clear();
        _errorAccountsGrid.Rows.Clear();
        foreach (var inst in _appSettings.Settings.Instances)
        {
            if (inst.IsError)
            {
                var reason = string.IsNullOrWhiteSpace(inst.ErrorReason) ? "Verify/captcha" : inst.ErrorReason;
                if (!string.IsNullOrWhiteSpace(inst.ErrorAt)) reason += $" — {inst.ErrorAt}";
                var i = _errorAccountsGrid.Rows.Add(inst.Username, inst.ProxySummary, reason, inst.ProfileRelativePath);
                _errorAccountsGrid.Rows[i].Tag = inst;
                _errorAccountsGrid.Rows[i].Cells["Status"].Style.ForeColor = Color.Firebrick;
            }
            else
            {
                var status = inst.OpenWithShopeeAccount ? "⚠ Cần đăng nhập" : "✓ Sẵn sàng";
                var i = _accountsGrid.Rows.Add(inst.Username, inst.ProxySummary, status, inst.ProfileRelativePath);
                _accountsGrid.Rows[i].Tag = inst;
                _accountsGrid.Rows[i].Cells["Status"].Style.ForeColor =
                    status.StartsWith('⚠') ? Color.OrangeRed : Color.ForestGreen;
            }
        }

        _accountInnerTabs.TabPages[0].Text = $"Bình thường ({_accountsGrid.Rows.Count})";
        _accountInnerTabs.TabPages[1].Text = $"Lỗi ({_errorAccountsGrid.Rows.Count})";
    }

    /// <summary>Grid của sub-tab đang xem (Bình thường / Lỗi).</summary>
    private DataGridView ActiveAccountsGrid() =>
        _accountInnerTabs.SelectedIndex == 1 ? _errorAccountsGrid : _accountsGrid;

    /// <summary>Các account đang được chọn ở grid truyền vào (theo Tag, không theo index).</summary>
    private static List<InstanceConfig> SelectedAccounts(DataGridView grid) =>
        grid.SelectedRows.Cast<DataGridViewRow>()
            .Select(r => r.Tag as InstanceConfig)
            .Where(a => a is not null)
            .Cast<InstanceConfig>()
            .Distinct()
            .ToList();

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        _searchCts?.Cancel();
        _coordinator?.KillAllBrowsers();
        _fileCoordinator?.KillAllBrowsers();
        _edge.Kill();
        _ = DisposeCdpInputAsync();
        _ws?.Dispose();
    }

    // Fire-and-forget: unsubscribes from the WS and closes the CDP session.
    // Best-effort — Edge is torn down independently, so a lingering socket is harmless.
    private Task DisposeCdpInputAsync()
    {
        var controller = _cdpInput;
        _cdpInput = null;
        return controller is null ? Task.CompletedTask : controller.DisposeAsync().AsTask();
    }

    private static List<string> ParseKeywordLines(string text) =>
        (text ?? "")
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

    private static List<string> SplitImportLines(string text, bool shopee)
    {
        var lines = (text ?? "")
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        if (shopee && lines.Count <= 1)
        {
            var matches = System.Text.RegularExpressions.Regex.Matches(
                text ?? "",
                @"[^\s|]+\|[^\s|]+\|\.shopee\.vn=SPC_F=[^\s|]+",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (matches.Count > 0)
                return matches.Select(m => m.Value.Trim()).ToList();
        }

        return lines;
    }
}

