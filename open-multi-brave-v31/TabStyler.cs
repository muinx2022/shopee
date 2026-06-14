namespace OpenMultiBraveLauncherV3;

/// <summary>
/// Tô lại TabControl. Có 2 cấp:
/// - <see cref="ApplyMain"/>: tab chính — to, nền xanh đậm + chữ trắng đậm, nổi bật.
/// - <see cref="ApplySub"/>: sub-tab — nhỏ & thấp hơn, tông xám nhạt + chữ tối, phụ thuộc tab chính.
/// Lưu ý: KHÔNG đổi <c>tabs.Font</c> vì control con trong tab sẽ kế thừa — chỉ dùng font cục bộ để VẼ tab.
/// </summary>
internal static class TabStyler
{
    private static readonly Color Blue = Color.FromArgb(0, 120, 215);

    public static void ApplyMain(TabControl tabs) => Apply(
        tabs,
        accent: Blue, selectedText: Color.White,
        inactiveBg: Color.FromArgb(233, 233, 239), inactiveText: Color.FromArgb(70, 70, 80),
        fontSize: 10.5f, height: 36, extraWidth: 64, minWidth: 150);

    public static void ApplySub(TabControl tabs) => Apply(
        tabs,
        accent: Color.FromArgb(205, 211, 220), selectedText: Color.FromArgb(45, 48, 55),
        inactiveBg: Color.FromArgb(247, 247, 250), inactiveText: Color.FromArgb(122, 126, 134),
        fontSize: 9f, height: 27, extraWidth: 32, minWidth: 120);

    private static void Apply(
        TabControl tabs,
        Color accent, Color selectedText,
        Color inactiveBg, Color inactiveText,
        float fontSize, int height, int extraWidth, int minWidth)
    {
        tabs.Alignment = TabAlignment.Top;
        tabs.SizeMode = TabSizeMode.Fixed;
        tabs.DrawMode = TabDrawMode.OwnerDrawFixed;

        // Font RIÊNG để vẽ tab — không gán vào tabs.Font để tránh control con kế thừa.
        var tabFont = new Font(tabs.Font.FontFamily, fontSize, FontStyle.Regular);
        var boldFont = new Font(tabFont, FontStyle.Bold);

        // Bề rộng tab = theo nhãn dài nhất (đo bằng font đậm để tab đang chọn không bị cắt chữ).
        var width = minWidth;
        using (var bmp = new Bitmap(1, 1))
        using (var g = Graphics.FromImage(bmp))
        {
            foreach (TabPage page in tabs.TabPages)
                width = Math.Max(width, (int)Math.Ceiling(g.MeasureString(page.Text, boldFont).Width) + extraWidth);
        }

        tabs.ItemSize = new Size(width, height);

        tabs.DrawItem += (_, e) =>
        {
            if (e.Index < 0 || e.Index >= tabs.TabPages.Count)
                return;

            var selected = e.Index == tabs.SelectedIndex;
            var rect = tabs.GetTabRect(e.Index);

            using (var bg = new SolidBrush(selected ? accent : inactiveBg))
                e.Graphics.FillRectangle(bg, rect);

            TextRenderer.DrawText(
                e.Graphics,
                tabs.TabPages[e.Index].Text,
                selected ? boldFont : tabFont,
                rect,
                selected ? selectedText : inactiveText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };

        tabs.Invalidate();
    }
}
