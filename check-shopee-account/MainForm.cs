using System.Collections.Concurrent;
using System.Drawing;
using System.Windows.Forms;

namespace CheckShopeeAccount;

public sealed class MainForm : Form
{
    private static readonly string OkFilePath =
        Path.Combine(AppContext.BaseDirectory, "tk-ok.txt");
    private static readonly string SettingsPath =
        Path.Combine(AppContext.BaseDirectory, "check-settings.json");
    // Profile bền theo tài khoản — layout giống shopee-stat/v31 (<dir>/Default) nên copy được thẳng.
    private static readonly string ProfilesRoot =
        Path.Combine(AppContext.BaseDirectory, "profiles");

    private readonly TextBox _accountsBox;
    private readonly TextBox _proxyListBox;
    private readonly TextBox _logBox;
    private readonly Button _runBtn;
    private readonly Button _stopBtn;
    private readonly NumericUpDown _lanesInput;
    private readonly Label _statusLabel;

    private readonly TabControl _tabs;
    private readonly DataGridView _okGrid;
    private readonly CheckBox _selectAllHeader;
    private readonly Button _copyV31Btn;
    private readonly Button _copyStatBtn;
    private readonly Label _okStatusLabel;

    // Trạng thái proxy theo từng kiotproxy key: IP hiện tại + mốc (epoch ms) được đổi tiếp.
    // Lưu/khôi phục qua settings để chạy lại vẫn tôn trọng thời gian chờ đổi IP.
    private readonly Dictionary<string, (string? ip, long next)> _proxyState = new(StringComparer.Ordinal);
    private ProxyPool? _pool;

    // Tài khoản OK đã copy sang đâu (line tk → tập đích "v31"/"shopee-stat"). Lưu qua settings.
    private readonly Dictionary<string, HashSet<string>> _copied = new(StringComparer.Ordinal);

    private enum CopyTarget { V31, ShopeeStat }

    // Repo root (chứa cả shopee-stat lẫn open-multi-brave-v31) để biết nơi đăng ký account + copy profile.
    private static readonly string RepoRoot = ResolveRepoRoot();
    private static readonly string ShopeeStatDataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShopeeStatApp");

    private CancellationTokenSource? _cts;
    private bool _running;

    public MainForm()
    {
        Text = "Check tài khoản Shopee";
        Width = 1180;
        Height = 900;
        MinimumSize = new Size(900, 680);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5F);

        _tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            DrawMode = TabDrawMode.OwnerDrawFixed,
            SizeMode = TabSizeMode.Fixed,
            ItemSize = new Size(190, 36),
            Font = new Font("Segoe UI", 10F),
        };
        // Tab đang chọn nổi hẳn: nền xanh + chữ trắng đậm; tab còn lại nền xám nhạt.
        _tabs.DrawItem += (_, e) =>
        {
            var rect = _tabs.GetTabRect(e.Index);
            var selected = e.Index == _tabs.SelectedIndex;
            using var back = new SolidBrush(selected ? Color.FromArgb(0, 120, 215) : Color.FromArgb(238, 238, 238));
            e.Graphics.FillRectangle(back, rect);
            using var font = new Font(_tabs.Font, selected ? FontStyle.Bold : FontStyle.Regular);
            TextRenderer.DrawText(e.Graphics, _tabs.TabPages[e.Index].Text, font, rect,
                selected ? Color.White : Color.FromArgb(70, 70, 70),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };

        // ── Tab 1: Check account ────────────────────────────────────────────────
        var checkTab = new TabPage("Check tài khoản");
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(10),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));               // label acc
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 50));            // acc box
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 130));          // proxy list
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));               // buttons
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));               // log label
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 50));            // log box

        root.Controls.Add(new Label
        {
            Text = "Danh sách tài khoản (mỗi dòng 1 tk — user|pass  hoặc  user|pass|.shopee.vn=SPC_F=...):",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 4),
        }, 0, 0);

        _accountsBox = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9.5F),
        };
        root.Controls.Add(_accountsBox, 0, 1);

        var proxyPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0, 8, 0, 4),
        };
        proxyPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        proxyPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        proxyPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        proxyPanel.Controls.Add(new Label
        {
            Text = "Danh sách proxy (mỗi dòng 1 kiotproxy key hoặc proxy host:port). Nên nhiều key để mỗi tk 1 IP mới; key chưa tới giờ đổi sẽ tự chuyển key khác:",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 4),
        }, 0, 0);
        _proxyListBox = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = false,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9.5F),
        };
        proxyPanel.Controls.Add(_proxyListBox, 0, 1);
        root.Controls.Add(proxyPanel, 0, 2);

        var btnRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 4),
        };
        _runBtn = new Button { Text = "▶ Chạy", Width = 120, Height = 32, BackColor = Color.FromArgb(0, 120, 60), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        _stopBtn = new Button { Text = "■ Dừng", Width = 100, Height = 32, Enabled = false };
        var profilesBtn = new Button { Text = "📁 Thư mục profile", Width = 150, Height = 32 };
        var lanesLabel = new Label { Text = "Số luồng:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(14, 9, 4, 0) };
        _lanesInput = new NumericUpDown { Minimum = 1, Maximum = 5, Value = 2, Width = 50, Height = 28, Margin = new Padding(0, 4, 0, 0), TextAlign = HorizontalAlignment.Center };
        _statusLabel = new Label { Text = "Sẵn sàng.", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(14, 8, 0, 0) };
        _runBtn.Click += OnRunClick;
        _stopBtn.Click += (_, _) => _cts?.Cancel();
        profilesBtn.Click += (_, _) => OpenProfilesFolder();
        btnRow.Controls.Add(_runBtn);
        btnRow.Controls.Add(_stopBtn);
        btnRow.Controls.Add(profilesBtn);
        btnRow.Controls.Add(lanesLabel);
        btnRow.Controls.Add(_lanesInput);
        btnRow.Controls.Add(_statusLabel);
        root.Controls.Add(btnRow, 0, 3);

        root.Controls.Add(new Label { Text = "Nhật ký:", AutoSize = true, Margin = new Padding(0, 4, 0, 4) }, 0, 4);
        _logBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(30, 30, 30),
            ForeColor = Color.Gainsboro,
            Font = new Font("Consolas", 9F),
        };
        root.Controls.Add(_logBox, 0, 5);

        checkTab.Controls.Add(root);

        // ── Tab 2: TK OK ─────────────────────────────────────────────────────────
        var okTab = new TabPage("TK OK");
        var okPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(10) };
        okPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        okPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        okPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var okTop = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoSize = true, Margin = new Padding(0, 0, 0, 6) };
        var reloadBtn = new Button { Text = "↻ Tải lại", Width = 90, Height = 32 };
        _copyV31Btn = new Button { Text = "→ Copy sang v31", Height = 32, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 0, 10, 0), BackColor = Color.FromArgb(225, 240, 255), FlatStyle = FlatStyle.Flat };
        _copyStatBtn = new Button { Text = "→ Copy sang shopee-stat", Height = 32, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 0, 10, 0), BackColor = Color.FromArgb(225, 255, 235), FlatStyle = FlatStyle.Flat };
        var openBtn = new Button { Text = "📂 Mở file", Width = 90, Height = 32 };
        reloadBtn.Click += (_, _) => LoadOkGrid();
        _copyV31Btn.Click += async (_, _) => await CopySelectedAsync(CopyTarget.V31);
        _copyStatBtn.Click += async (_, _) => await CopySelectedAsync(CopyTarget.ShopeeStat);
        openBtn.Click += (_, _) => OpenOkFileInExplorer();
        _okStatusLabel = new Label { Text = "", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(14, 8, 0, 0), ForeColor = Color.FromArgb(0, 90, 160) };
        okTop.Controls.Add(reloadBtn);
        okTop.Controls.Add(_copyV31Btn);
        okTop.Controls.Add(_copyStatBtn);
        okTop.Controls.Add(openBtn);
        okTop.Controls.Add(_okStatusLabel);
        okPanel.Controls.Add(okTop, 0, 0);

        _okGrid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.CellSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            EditMode = DataGridViewEditMode.EditOnEnter,
            Font = new Font("Consolas", 9.5F),
            BackgroundColor = Color.White,
        };
        _okGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "sel", HeaderText = "", FillWeight = 6 });
        _okGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "acc", HeaderText = "Tài khoản", FillWeight = 64, ReadOnly = true });
        _okGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "status", HeaderText = "Trạng thái", FillWeight = 30, ReadOnly = true });

        // Checkbox trên header cột chọn → check/uncheck tất cả.
        _selectAllHeader = new CheckBox { Size = new Size(16, 16), BackColor = Color.Transparent, Cursor = Cursors.Default };
        _selectAllHeader.CheckedChanged += (_, _) =>
        {
            _okGrid.EndEdit();
            foreach (DataGridViewRow r in _okGrid.Rows)
                r.Cells["sel"].Value = _selectAllHeader.Checked;
        };
        _okGrid.Controls.Add(_selectAllHeader);
        _okGrid.ColumnWidthChanged += (_, _) => PositionHeaderCheckbox();
        _okGrid.Scroll += (_, _) => PositionHeaderCheckbox();
        _okGrid.SizeChanged += (_, _) => PositionHeaderCheckbox();
        okPanel.Controls.Add(_okGrid, 0, 1);
        okTab.Controls.Add(okPanel);

        _tabs.TabPages.Add(checkTab);
        _tabs.TabPages.Add(okTab);
        _tabs.SelectedIndexChanged += (_, _) =>
        {
            if (_tabs.SelectedTab == okTab) LoadOkGrid();
        };
        Controls.Add(_tabs);

        Load += (_, _) => LoadSettings();
        FormClosing += (_, _) => { _cts?.Cancel(); SaveSettings(); };
    }

    // ── Run ─────────────────────────────────────────────────────────────────────

    private async void OnRunClick(object? sender, EventArgs e)
    {
        if (_running) return;

        var lines = _accountsBox.Lines
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        if (lines.Count == 0)
        {
            MessageBox.Show("Chưa có tài khoản nào để check.", "Check tài khoản",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var proxies = _proxyListBox.Lines
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
        SaveSettings();

        _pool = proxies.Count > 0 ? new ProxyPool(proxies, _proxyState) : null;
        if (_pool is not null) _pool.Log += AppendLog;

        _running = true;
        _cts = new CancellationTokenSource();
        SetRunningUi(true);
        _logBox.Clear();
        if (_pool is null)
            AppendLog("(không có proxy — chạy bằng IP máy)");
        else
            AppendLog($"({proxies.Count} proxy — mỗi tk 1 IP mới, key chưa tới giờ đổi thì chuyển key khác)");

        var laneCount = Math.Max(1, Math.Min((int)_lanesInput.Value, lines.Count));
        var total = lines.Count;
        var queue = new ConcurrentQueue<string>(lines);

        // Trạng thái dùng chung giữa các luồng — khoá riêng cho từng nhóm.
        var remaining = new List<string>(lines);
        var remainingLock = new object();
        var statsLock = new object();
        var fileLock = new object();
        var okCount = 0;
        var failCount = 0;
        var manualCount = 0;
        var processed = 0;

        // Xoá dòng đã xử lý xong khỏi danh sách còn lại + cập nhật ô tài khoản (trên UI thread).
        void ResolveRemaining(string line)
        {
            string[] snapshot;
            lock (remainingLock) { remaining.Remove(line); snapshot = remaining.ToArray(); }
            BeginInvoke(() => _accountsBox.Lines = snapshot);
        }

        AppendLog(laneCount > 1 ? $"▶ Chạy {laneCount} luồng song song." : "▶ Chạy 1 luồng.");

        async Task WorkerAsync(int laneId)
        {
            // Mỗi luồng có checker riêng (state chuột/_rng theo từng phiên, không chia sẻ giữa thread).
            var checker = new ShopeeAccountChecker();
            void LaneLog(string m) => AppendLog($"[L{laneId}]{m}");
            checker.Log += LaneLog;
            try
            {
                while (!_cts!.IsCancellationRequested && queue.TryDequeue(out var line))
                {
                    var shortName = line.Split('|')[0];
                    var n = Interlocked.Increment(ref processed);
                    SetStatus($"Đang chạy {laneCount} luồng… ({n}/{total})");
                    LaneLog($" ({n}/{total}) {shortName}");

                    // Lấy 1 IP mới cho tài khoản này (xoay key khi key hiện tại chưa tới giờ đổi).
                    string? proxy = null;
                    if (_pool is not null)
                    {
                        proxy = await _pool.AcquireFreshAsync(_cts.Token);
                        if (proxy is null)
                        {
                            LaneLog("   ⚠ không lấy được proxy nào → giữ lại tk này");
                            lock (statsLock) manualCount++;
                            continue;
                        }
                    }

                    // Giữ mỗi tk thêm 25–30s bất kể thành công/thất bại. Cùng thời gian login (~15–30s),
                    // mỗi tk tốn ~40–60s; chạy song song nhiều luồng để tăng thông lượng.
                    var hold = Random.Shared.Next(25_000, 30_001);

                    // Profile bền theo tài khoản; tạo mới sạch mỗi lần check để test đúng mật khẩu.
                    var profileDir = Path.Combine(ProfilesRoot, SafeProfileName(shortName));
                    TryDeleteDir(profileDir);

                    CheckResult result;
                    try
                    {
                        result = await checker.CheckAsync(line, proxy, profileDir, hold, _cts.Token);
                    }
                    catch (Exception ex)
                    {
                        result = new CheckResult(CheckOutcome.Error, ex.Message);
                    }

                    switch (result.Outcome)
                    {
                        case CheckOutcome.Success:
                            lock (statsLock) okCount++;
                            lock (fileLock) AppendSuccess(line);
                            ResolveRemaining(line);
                            LaneLog("   ✔ THÀNH CÔNG → đã lưu vào tk-ok.txt");
                            LaneLog("   📁 giữ profile: " + profileDir);
                            break;
                        case CheckOutcome.WrongPassword:
                            lock (statsLock) failCount++;
                            ResolveRemaining(line);
                            LaneLog("   ✘ SAI MẬT KHẨU → đã xoá khỏi danh sách");
                            break;
                        case CheckOutcome.NeedsManual:
                            lock (statsLock) manualCount++;
                            LaneLog("   ⚠ " + result.Message + " → giữ lại trong danh sách");
                            break;
                        default:
                            LaneLog("   ⚠ Lỗi: " + result.Message + " → giữ lại trong danh sách");
                            break;
                    }

                    // Chỉ giữ profile khi login thành công (để copy sang shopee-stat/v31); còn lại xoá.
                    if (result.Outcome != CheckOutcome.Success)
                        TryDeleteDir(profileDir);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { LaneLog("   ⚠ luồng dừng do lỗi: " + ex.Message); }
            finally { checker.Log -= LaneLog; }
        }

        try
        {
            var workers = Enumerable.Range(1, laneCount).Select(WorkerAsync).ToArray();
            await Task.WhenAll(workers);
        }
        finally
        {
            if (_pool is not null)
            {
                _pool.Log -= AppendLog;
                UpdateProxyStateFromPool();
                SaveSettings();
                _pool = null;
            }
            _running = false;
            SetRunningUi(false);
            var done = _cts.IsCancellationRequested ? "Đã dừng." : "Hoàn tất.";
            SetStatus($"{done} OK={okCount}, sai mật khẩu={failCount}, cần tay={manualCount}.");
            AppendLog($"── {done} OK={okCount}, sai mật khẩu={failCount}, cần xử lý tay={manualCount} ──");
            _cts.Dispose();
            _cts = null;
        }
    }

    /// <summary>Đọc lại IP + mốc đổi tiếp của từng key từ pool về _proxyState để lưu settings.</summary>
    private void UpdateProxyStateFromPool()
    {
        if (_pool is null) return;
        foreach (var e in _pool.Entries)
        {
            if (e.IsKey)
                _proxyState[e.Raw] = (e.CurrentIp, e.NextChangeAtMs);
        }
    }

    private void SetRunningUi(bool running)
    {
        _runBtn.Enabled = !running;
        _stopBtn.Enabled = running;
        _lanesInput.Enabled = !running;
        _accountsBox.ReadOnly = running;
        _proxyListBox.ReadOnly = running;
    }

    /// <summary>Tên thư mục profile an toàn từ username (giữ chữ/số/.-_@, còn lại thay '_').</summary>
    private static string SafeProfileName(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return "acc-" + Guid.NewGuid().ToString("N")[..8];
        var sb = new StringBuilder(username.Length);
        foreach (var ch in username.Trim())
            sb.Append(char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' or '@' ? ch : '_');
        var name = sb.ToString().Trim('_', '.');
        return name.Length == 0 ? "acc-" + Guid.NewGuid().ToString("N")[..8] : name;
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }

    private void OpenProfilesFolder()
    {
        try
        {
            Directory.CreateDirectory(ProfilesRoot);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ProfilesRoot}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Mở thư mục profile", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ── File OK ───────────────────────────────────────────────────────────────

    private void AppendSuccess(string line)
    {
        try { File.AppendAllText(OkFilePath, line + Environment.NewLine, Encoding.UTF8); }
        catch (Exception ex) { AppendLog("  (không ghi được tk-ok.txt: " + ex.Message + ")"); }
    }

    private void LoadOkGrid()
    {
        _okGrid.Rows.Clear();
        List<string> lines;
        try
        {
            lines = File.Exists(OkFilePath)
                ? File.ReadAllLines(OkFilePath, Encoding.UTF8)
                    .Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().ToList()
                : [];
        }
        catch (Exception ex)
        {
            MessageBox.Show("Lỗi đọc tk-ok.txt: " + ex.Message, "TK OK", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        foreach (var line in lines)
        {
            var idx = _okGrid.Rows.Add(false, line, StatusText(line));
            _okGrid.Rows[idx].Tag = line;
        }
        PositionHeaderCheckbox();
    }

    private string StatusText(string line)
    {
        if (!_copied.TryGetValue(line, out var set) || set.Count == 0) return "";
        var names = set.Select(t => t == "v31" ? "v31" : "shopee-stat");
        return "đã copy: " + string.Join(", ", names);
    }

    /// <summary>Đặt checkbox "chọn tất cả" vào đúng giữa header của cột chọn.</summary>
    private void PositionHeaderCheckbox()
    {
        if (_okGrid.Columns.Count == 0) return;
        var rect = _okGrid.GetCellDisplayRectangle(0, -1, true); // -1 = ô header
        if (rect.Width <= 0 || rect.Height <= 0) { _selectAllHeader.Visible = false; return; }
        _selectAllHeader.Visible = true;
        _selectAllHeader.Location = new Point(
            rect.Left + (rect.Width - _selectAllHeader.Width) / 2,
            rect.Top + (rect.Height - _selectAllHeader.Height) / 2);
    }

    private async Task CopySelectedAsync(CopyTarget target)
    {
        _okGrid.EndEdit();
        var key = target == CopyTarget.V31 ? "v31" : "shopee-stat";
        var app = target == CopyTarget.V31 ? ExportApp.V31 : ExportApp.ShopeeStat;

        var selected = _okGrid.Rows.Cast<DataGridViewRow>()
            .Where(r => r.Cells["sel"].Value is bool b && b)
            .Select(r => r.Tag as string ?? "")
            .Where(l => l.Length > 0)
            .ToList();

        if (selected.Count == 0)
        {
            MessageBox.Show("Tích chọn ít nhất 1 tài khoản để copy.", "Copy profile",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Mở settings app đích + backup trước khi ghi.
        var registrar = new TargetRegistrar(app, RepoRoot, ShopeeStatDataDir);
        if (!registrar.TryLoad(out var loadErr))
        {
            MessageBox.Show(loadErr, "Copy sang " + key, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Đã copy sang đích này rồi → xác nhận trước khi ghi đè.
        var recopy = selected.Where(l => _copied.TryGetValue(l, out var s) && s.Contains(key)).ToList();
        if (recopy.Count > 0)
        {
            var msg = $"{recopy.Count} tài khoản đã copy sang {key} trước đó. Ghi đè lại?\n\n" +
                      string.Join("\n", recopy.Take(10).Select(l => "• " + l.Split('|')[0])) +
                      (recopy.Count > 10 ? "\n…" : "");
            if (MessageBox.Show(msg, "Xác nhận copy lại", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
        }

        registrar.Backup();
        _copyV31Btn.Enabled = _copyStatBtn.Enabled = false;
        var ok = 0; var missing = 0; var failed = 0;
        try
        {
            for (var i = 0; i < selected.Count; i++)
            {
                var line = selected[i];
                var username = line.Split('|')[0];
                _okStatusLabel.Text = $"Đang copy {i + 1}/{selected.Count} sang {key}: {username}…";
                _okStatusLabel.Refresh();

                var src = Path.Combine(ProfilesRoot, SafeProfileName(username));
                if (!Directory.Exists(Path.Combine(src, "Default")))
                {
                    missing++;
                    SetRowStatus(line, "⚠ chưa có profile (chạy check lại)");
                    continue;
                }

                var slot = registrar.Resolve(line);
                try
                {
                    await Task.Run(() => CopyProfile(src, slot.DestProfile));
                    registrar.Apply(slot, line, username);
                    if (!_copied.TryGetValue(line, out var set)) _copied[line] = set = new HashSet<string>();
                    set.Add(key);
                    SetRowStatus(line, StatusText(line));
                    ok++;
                }
                catch (Exception ex)
                {
                    failed++;
                    SetRowStatus(line, "✘ lỗi: " + ex.Message);
                }
            }

            if (ok > 0)
            {
                _okStatusLabel.Text = "Đang lưu settings…";
                _okStatusLabel.Refresh();
                registrar.Save();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("Lỗi ghi settings: " + ex.Message, "Copy sang " + key,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _copyV31Btn.Enabled = _copyStatBtn.Enabled = true;
            SaveSettings();
        }

        _okStatusLabel.Text = $"Xong: {ok} ok, {missing} thiếu profile, {failed} lỗi (đích: {key}).";

        MessageBox.Show(
            $"Copy sang {key} xong — đã đăng ký account + profile.\n" +
            $"Thành công: {ok}\nThiếu profile: {missing}\nLỗi: {failed}\n\n" +
            $"settings: {registrar.SettingsPath}\nprofiles: {registrar.ProfilesDestBase}",
            "Copy sang " + key, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void SetRowStatus(string line, string status)
    {
        foreach (DataGridViewRow r in _okGrid.Rows)
            if ((string?)r.Tag == line) { r.Cells["status"].Value = status; break; }
    }

    /// <summary>Copy nguyên user-data-dir (gồm Default + Local State) — ghi đè đích nếu đã có.</summary>
    private static void CopyProfile(string src, string dest)
    {
        if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file);
            var targetPath = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(file, targetPath, overwrite: true);
        }
    }

    private static string ResolveRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "shopee-stat")) &&
                Directory.Exists(Path.Combine(dir.FullName, "open-multi-brave-v31")))
                return dir.FullName;
        }
        return AppContext.BaseDirectory;
    }

    private void OpenOkFileInExplorer()
    {
        try
        {
            if (!File.Exists(OkFilePath)) File.WriteAllText(OkFilePath, "", Encoding.UTF8);
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{OkFilePath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Mở file", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ── Settings (nhớ proxy key + danh sách) ─────────────────────────────────────

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath, Encoding.UTF8));
            var root = doc.RootElement;
            if (root.TryGetProperty("proxyList", out var pl)) _proxyListBox.Text = pl.GetString() ?? "";
            else if (root.TryGetProperty("proxyKey", out var pk)) _proxyListBox.Text = pk.GetString() ?? ""; // tương thích bản cũ
            if (root.TryGetProperty("accounts", out var acc)) _accountsBox.Text = acc.GetString() ?? "";
            if (root.TryGetProperty("lanes", out var ln) && ln.TryGetInt32(out var lanes))
                _lanesInput.Value = Math.Max(_lanesInput.Minimum, Math.Min(_lanesInput.Maximum, lanes));

            // Khôi phục IP + mốc đổi tiếp của từng key (giữ lại list cũ, cập nhật dần)
            _proxyState.Clear();
            if (root.TryGetProperty("proxyState", out var ps) && ps.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in ps.EnumerateArray())
                {
                    var raw = item.TryGetProperty("raw", out var r) ? r.GetString() : null;
                    if (string.IsNullOrEmpty(raw)) continue;
                    var ip = item.TryGetProperty("ip", out var ipEl) ? ipEl.GetString() : null;
                    var next = item.TryGetProperty("next", out var nEl) && nEl.TryGetInt64(out var nn) ? nn : 0;
                    _proxyState[raw] = (ip, next);
                }
            }

            // Khôi phục trạng thái đã copy của tk OK
            _copied.Clear();
            if (root.TryGetProperty("copied", out var cp) && cp.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in cp.EnumerateArray())
                {
                    var line = item.TryGetProperty("line", out var l) ? l.GetString() : null;
                    if (string.IsNullOrEmpty(line)) continue;
                    var set = new HashSet<string>();
                    if (item.TryGetProperty("targets", out var ts) && ts.ValueKind == JsonValueKind.Array)
                        foreach (var t in ts.EnumerateArray())
                            if (t.GetString() is { Length: > 0 } tv) set.Add(tv);
                    _copied[line] = set;
                }
            }
        }
        catch { }
    }

    private void SaveSettings()
    {
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                proxyList = _proxyListBox.Text,
                accounts = _accountsBox.Text,
                lanes = (int)_lanesInput.Value,
                proxyState = _proxyState.Select(kv => new { raw = kv.Key, ip = kv.Value.ip, next = kv.Value.next }).ToArray(),
                copied = _copied.Select(kv => new { line = kv.Key, targets = kv.Value.ToArray() }).ToArray(),
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json, Encoding.UTF8);
        }
        catch { }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private void SetStatus(string text)
    {
        if (InvokeRequired) { BeginInvoke(() => SetStatus(text)); return; }
        _statusLabel.Text = text;
    }

    private void AppendLog(string text)
    {
        if (InvokeRequired) { BeginInvoke(() => AppendLog(text)); return; }
        _logBox.AppendText(text + Environment.NewLine);
    }
}
