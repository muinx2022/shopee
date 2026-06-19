namespace OpenMultiBraveLauncherV3;

/// <summary>
/// Form hiển thị trạng thái đăng nhập BigSeller.
/// Mở Brave không proxy (IP máy thật), chờ user đăng nhập, tự động lưu cookie.
/// </summary>
internal sealed class BigSellerLoginForm : Form
{
    private readonly string _braveExe;
    private readonly string _sourceUserData;
    private readonly string _cookieFilePath;
    private readonly RichTextBox _logBox;
    private readonly Label _statusLabel;
    private readonly Button _actionButton;
    private BigSellerLoginRunner? _runner;
    private CancellationTokenSource? _cts;
    private bool _loginSucceeded;

    public bool LoginSucceeded => _loginSucceeded;

    public BigSellerLoginForm(string braveExe, string sourceUserData, string cookieFilePath)
    {
        _braveExe = braveExe;
        _sourceUserData = sourceUserData;
        _cookieFilePath = cookieFilePath;

        Text = "Đăng nhập BigSeller (IP máy thật)";
        StartPosition = FormStartPosition.CenterParent;
        Width = 620;
        Height = 440;
        MinimumSize = new Size(500, 340);

        _statusLabel = new Label
        {
            Text = "Đang khởi động Brave...",
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 36,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0),
            Font = new Font(Font.FontFamily, Font.Size + 1f, FontStyle.Bold),
        };

        _logBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = Color.FromArgb(28, 28, 28),
            ForeColor = Color.FromArgb(210, 210, 210),
            Font = new Font("Consolas", 9f),
            ScrollBars = RichTextBoxScrollBars.Vertical,
            BorderStyle = BorderStyle.None,
        };

        var note = new Label
        {
            Text = "Brave sẽ mở KHÔNG proxy → đăng nhập BigSeller trong cửa sổ đó → cookie tự lưu.",
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 28,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0),
            ForeColor = Color.FromArgb(100, 160, 220),
        };

        _actionButton = new Button
        {
            Text = "Dừng / Đóng Brave",
            Width = 160,
            Height = 34,
            Margin = new Padding(8),
        };
        _actionButton.Click += (_, _) => StopAndClose();

        var bottomPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(4),
        };
        bottomPanel.Controls.Add(_actionButton);

        Controls.Add(_logBox);
        Controls.Add(note);
        Controls.Add(_statusLabel);
        Controls.Add(bottomPanel);

        FormClosing += (_, _) => CleanupSession();
        Shown += async (_, _) => await RunLoginSessionAsync();
    }

    private void AppendLog(string msg)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => AppendLog(msg)); return; }
        _logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\n");
        _logBox.ScrollToCaret();
    }

    private void SetStatus(string text, Color? color = null)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => SetStatus(text, color)); return; }
        _statusLabel.Text = text;
        _statusLabel.ForeColor = color ?? SystemColors.ControlText;
    }

    private async Task RunLoginSessionAsync()
    {
        _cts = new CancellationTokenSource();
        try
        {
            AppendLog($"File cookie sẽ lưu tại: {_cookieFilePath}");

            _runner = await BigSellerLoginRunner.LaunchAsync(
                _braveExe, _sourceUserData, AppendLog, _cts.Token).ConfigureAwait(true);

            SetStatus("Đang chờ đăng nhập BigSeller trong cửa sổ Brave...");
            AppendLog("Đăng nhập trong cửa sổ Brave. Cookie tự lưu khi phát hiện đăng nhập thành công.");

            var success = await _runner.PollForLoginAndSaveCookiesAsync(
                _cookieFilePath,
                AppendLog,
                onLoginDetected: _ =>
                {
                    if (!IsDisposed)
                        BeginInvoke(() =>
                        {
                            _loginSucceeded = true;
                            SetStatus("Đăng nhập thành công! Cookie đã lưu.", Color.FromArgb(30, 160, 60));
                            _actionButton.Text = "Đóng";
                        });
                },
                cancellationToken: _cts.Token).ConfigureAwait(true);

            if (!success && !_loginSucceeded)
                SetStatus("Không phát hiện đăng nhập (hết thời gian hoặc bị hủy).",
                    Color.FromArgb(200, 80, 60));
        }
        catch (OperationCanceledException)
        {
            SetStatus("Đã hủy.");
        }
        catch (Exception ex)
        {
            SetStatus($"Lỗi: {ex.Message}", Color.FromArgb(200, 80, 60));
            AppendLog($"Lỗi: {ex.Message}");
        }
    }

    private void CleanupSession()
    {
        try { _cts?.Cancel(); } catch { }
        _runner?.Dispose();
        _runner = null;
    }

    private void StopAndClose()
    {
        CleanupSession();
        Close();
    }
}
