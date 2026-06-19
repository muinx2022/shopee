namespace OpenMultiBraveLauncherV3;

/// <summary>
/// Hộp thoại nhỏ "đang xử lý" với spinner tự vẽ (xoay bằng Timer).
/// Hiển thị non-modal qua Show(owner); UI thread phải rảnh để spinner quay
/// → công việc nặng phải chạy ở background (await Task...).
/// </summary>
internal sealed class BusyDialog : Form
{
    private readonly Spinner _spinner = new();
    private readonly Label _label;

    public BusyDialog(string message)
    {
        FormBorderStyle = FormBorderStyle.None;
        // Borderless + CenterParent không đáng tin (hay nhảy về góc) → canh giữa thủ công.
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.White;
        ClientSize = new Size(280, 120);
        ShowInTaskbar = false;
        ControlBox = false;

        // Đặt sẵn ra giữa màn hình hiện tại để khung đầu tiên không nháy ở (0,0).
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(
            wa.Left + (wa.Width - ClientSize.Width) / 2,
            wa.Top + (wa.Height - ClientSize.Height) / 2);

        // Viền mảnh quanh dialog
        Padding = new Padding(1);

        _spinner.Size = new Size(40, 40);
        _spinner.Location = new Point((ClientSize.Width - 40) / 2, 26);

        _label = new Label
        {
            Text = message,
            AutoSize = false,
            Dock = DockStyle.Bottom,
            Height = 34,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(Font.FontFamily, Font.Size + 0.5f, FontStyle.Regular),
            ForeColor = Color.FromArgb(60, 64, 70),
        };

        Controls.Add(_spinner);
        Controls.Add(_label);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Color.FromArgb(214, 217, 222));
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    public void SetMessage(string message)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => SetMessage(message)); return; }
        _label.Text = message;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // Canh giữa TRƯỚC khi form hiện lần đầu — tránh nháy ở góc (0,0) rồi mới nhảy vào giữa.
        CenterOnOwner();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        CenterOnOwner();
        _spinner.Start();
    }

    private void CenterOnOwner()
    {
        var area = Owner is { } owner && owner.Visible
            ? owner.Bounds
            : Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(
            area.Left + (area.Width - Width) / 2,
            area.Top + (area.Height - Height) / 2);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _spinner.Stop();
        base.OnFormClosed(e);
    }

    /// <summary>Spinner kiểu 12 nan hoa mờ dần, xoay theo Timer.</summary>
    private sealed class Spinner : Control
    {
        private const int Spokes = 12;
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 80 };
        private int _step;

        public Spinner()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            _timer.Tick += (_, _) =>
            {
                _step = (_step + 1) % Spokes;
                Invalidate();
            };
        }

        public void Start() => _timer.Start();

        public void Stop() => _timer.Stop();

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            var cx = Width / 2f;
            var cy = Height / 2f;
            var inner = Math.Min(Width, Height) * 0.22f;
            var outer = Math.Min(Width, Height) * 0.46f;
            var penWidth = Math.Max(2f, Math.Min(Width, Height) * 0.10f);

            for (var i = 0; i < Spokes; i++)
            {
                var angle = (Math.PI * 2 * i / Spokes) - Math.PI / 2;
                var cos = (float)Math.Cos(angle);
                var sin = (float)Math.Sin(angle);

                // Nan hoa ngay sau "đầu" sáng nhất, mờ dần về sau.
                var distance = (i - _step + Spokes) % Spokes;
                var alpha = (int)(40 + 215 * (distance / (float)(Spokes - 1)));
                alpha = Math.Clamp(255 - alpha, 40, 255);

                using var pen = new Pen(Color.FromArgb(alpha, 60, 120, 215), penWidth)
                {
                    StartCap = System.Drawing.Drawing2D.LineCap.Round,
                    EndCap = System.Drawing.Drawing2D.LineCap.Round,
                };
                e.Graphics.DrawLine(pen,
                    cx + cos * inner, cy + sin * inner,
                    cx + cos * outer, cy + sin * outer);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
