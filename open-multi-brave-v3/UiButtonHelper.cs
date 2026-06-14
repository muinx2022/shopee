using System.Drawing.Drawing2D;

namespace OpenMultiBraveLauncherV3;

internal static class UiButtonHelper
{
    private const int IconSize = 16;

    public static void Style(Button button, Bitmap icon, string text)
    {
        button.UseVisualStyleBackColor = true;
        button.FlatStyle = FlatStyle.Standard;
        button.BackColor = SystemColors.Control;
        button.ForeColor = SystemColors.ControlText;
        button.Image = icon;
        button.Text = text;
        button.TextImageRelation = TextImageRelation.ImageBeforeText;
        button.ImageAlign = ContentAlignment.MiddleLeft;
        button.TextAlign = ContentAlignment.MiddleCenter;
    }

    public static Bitmap OpenProfile() => Draw(g =>
    {
        using var pen = new Pen(Color.FromArgb(0, 120, 215), 2f);
        g.DrawRectangle(pen, 3, 5, 10, 9);
        g.DrawLine(pen, 3, 8, 13, 8);
    });

    public static Bitmap CloseProfile() => Draw(g =>
    {
        using var pen = new Pen(Color.FromArgb(196, 43, 28), 2.2f);
        g.DrawLine(pen, 4, 4, 12, 12);
        g.DrawLine(pen, 12, 4, 4, 12);
    });

    public static Bitmap OpenFolder() => Draw(g =>
    {
        using var brush = new SolidBrush(Color.FromArgb(240, 173, 78));
        var folder = new Point[] { new(2, 6), new(7, 6), new(8, 4), new(14, 4), new(14, 13), new(2, 13) };
        g.FillPolygon(brush, folder);
        using var pen = new Pen(Color.FromArgb(200, 130, 40), 1f);
        g.DrawPolygon(pen, folder);
    });

    public static Bitmap Run() => Draw(g =>
    {
        using var brush = new SolidBrush(Color.FromArgb(40, 167, 69));
        g.FillPolygon(brush, new Point[] { new(5, 3), new(13, 8), new(5, 13) });
    });

    public static Bitmap Stop() => Draw(g =>
    {
        using var brush = new SolidBrush(Color.FromArgb(220, 53, 69));
        g.FillRectangle(brush, 4, 4, 8, 8);
    });

    public static Bitmap Resume() => Draw(g =>
    {
        using var brush = new SolidBrush(Color.FromArgb(32, 201, 151));
        g.FillPolygon(brush, new Point[] { new(3, 3), new(3, 13), new(11, 8) });
        g.FillRectangle(brush, 12, 3, 2, 10);
    });

    public static Bitmap PushExt() => Draw(g =>
    {
        using var pen = new Pen(Color.FromArgb(0, 123, 255), 2f);
        g.DrawLine(pen, 8, 11, 8, 3);
        g.DrawLine(pen, 8, 3, 5, 6);
        g.DrawLine(pen, 8, 3, 11, 6);
        g.DrawArc(pen, 3, 7, 10, 6, 0, 180);
    });

    public static Bitmap Sync() => Draw(g =>
    {
        using var pen = new Pen(Color.FromArgb(111, 66, 193), 2f);
        g.DrawArc(pen, 4, 3, 8, 8, 135, 180);
        g.DrawLine(pen, 11, 4, 13, 2);
        g.DrawLine(pen, 11, 4, 9, 2);
        g.DrawArc(pen, 4, 5, 8, 8, -45, 180);
        g.DrawLine(pen, 5, 12, 3, 14);
        g.DrawLine(pen, 5, 12, 7, 14);
    });

    public static Bitmap Export() => Draw(g =>
    {
        using var pen = new Pen(Color.FromArgb(253, 126, 20), 2f);
        g.DrawLine(pen, 8, 3, 8, 10);
        g.DrawLine(pen, 8, 3, 5, 6);
        g.DrawLine(pen, 8, 3, 11, 6);
        g.DrawLine(pen, 4, 12, 12, 12);
    });

    public static Bitmap Import() => Draw(g =>
    {
        using var pen = new Pen(Color.FromArgb(102, 16, 242), 2f);
        g.DrawLine(pen, 8, 10, 8, 3);
        g.DrawLine(pen, 8, 10, 5, 7);
        g.DrawLine(pen, 8, 10, 11, 7);
        g.DrawLine(pen, 4, 12, 12, 12);
    });

    public static Bitmap Add() => Draw(g =>
    {
        using var pen = new Pen(Color.FromArgb(40, 167, 69), 2.2f);
        g.DrawLine(pen, 8, 3, 8, 13);
        g.DrawLine(pen, 3, 8, 13, 8);
    });

    public static Bitmap Remove() => Draw(g =>
    {
        using var pen = new Pen(Color.FromArgb(220, 53, 69), 2.2f);
        g.DrawLine(pen, 3, 8, 13, 8);
    });

    public static Bitmap Settings() => Draw(g =>
    {
        using var pen = new Pen(Color.FromArgb(108, 117, 125), 2f);
        g.DrawEllipse(pen, 4, 4, 8, 8);
        for (var i = 0; i < 8; i++)
        {
            var a = i * Math.PI / 4;
            var x1 = 8 + (int)(Math.Cos(a) * 4);
            var y1 = 8 + (int)(Math.Sin(a) * 4);
            var x2 = 8 + (int)(Math.Cos(a) * 6);
            var y2 = 8 + (int)(Math.Sin(a) * 6);
            g.DrawLine(pen, x1, y1, x2, y2);
        }
    });

    private static Bitmap Draw(Action<Graphics> draw)
    {
        var bmp = new Bitmap(IconSize, IconSize);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        draw(g);
        return bmp;
    }
}
