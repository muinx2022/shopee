using System.Drawing.Drawing2D;

namespace ShopeeStatApp.Forms;

/// <summary>Vòng tròn nhỏ xoay để báo "đang chạy" (thay cho ProgressBar marquee).</summary>
public sealed class Spinner : Control
{
    private readonly System.Windows.Forms.Timer _timer;
    private int _angle;

    public Spinner()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Size = new Size(22, 22);
        _timer = new System.Windows.Forms.Timer { Interval = 80 };
        _timer.Tick += (_, _) =>
        {
            _angle = (_angle + 30) % 360;
            Invalidate();
        };
    }

    public bool Spinning
    {
        get => _timer.Enabled;
        set
        {
            if (_timer.Enabled == value) return;
            _timer.Enabled = value;
            _angle = 0;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!Spinning) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var thickness = Math.Max(2f, Width / 8f);
        var pad = (int)Math.Ceiling(thickness / 2) + 1;
        var rect = new Rectangle(pad, pad, Width - pad * 2 - 1, Height - pad * 2 - 1);
        using var pen = new Pen(Color.FromArgb(0, 120, 215), thickness)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        e.Graphics.DrawArc(pen, rect, _angle, 270);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
