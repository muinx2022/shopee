namespace OpenMultiBraveLauncherV3;

internal sealed class InstanceLogWindow : Form
{
    private readonly RichTextBox _logTextBox;

    public string InstanceId { get; }

    public InstanceLogWindow(string instanceId, string title)
    {
        InstanceId = instanceId;
        Text = title;
        StartPosition = FormStartPosition.Manual;
        Width = 640;
        Height = 420;
        MinimumSize = new Size(420, 260);

        _logTextBox = new RichTextBox
        {
            ReadOnly = true,
            Dock = DockStyle.Fill,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            WordWrap = true,
            Font = new Font("Segoe UI", 9f),
            BackColor = Color.FromArgb(22, 22, 26),
            ForeColor = Color.FromArgb(215, 215, 220),
            BorderStyle = BorderStyle.None,
        };

        Controls.Add(_logTextBox);
    }

    public void AppendLog(string message)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLog(message));
            return;
        }

        var time = DateTime.Now.ToString("HH:mm:ss");
        _logTextBox.SelectionStart = _logTextBox.TextLength;
        _logTextBox.SelectionLength = 0;
        _logTextBox.SelectionColor = Color.FromArgb(110, 110, 120);
        _logTextBox.AppendText($"{time} ");
        _logTextBox.SelectionColor = ClassifyLogLineColor(message);
        _logTextBox.AppendText(message);
        _logTextBox.AppendText(Environment.NewLine);
        _logTextBox.SelectionStart = _logTextBox.TextLength;
        _logTextBox.SelectionLength = 0;
        _logTextBox.SelectionColor = _logTextBox.ForeColor;
        _logTextBox.ScrollToCaret();

        if (_logTextBox.Lines.Length > 800)
        {
            _logTextBox.Select(0, _logTextBox.GetFirstCharIndexFromLine(150));
            _logTextBox.SelectedText = "";
            _logTextBox.SelectionStart = _logTextBox.TextLength;
            _logTextBox.SelectionLength = 0;
            _logTextBox.SelectionColor = _logTextBox.ForeColor;
        }
    }

    private static Color ClassifyLogLineColor(string text)
    {
        var t = text.ToLowerInvariant();
        if (t.Contains("lỗi") || t.Contains("error") || t.Contains("fail") || t.Contains("không "))
            return Color.FromArgb(255, 120, 120);
        if (t.Contains("xong") || t.Contains("ok") || t.Contains("đã "))
            return Color.FromArgb(120, 220, 140);
        if (t.Contains("chờ") || t.Contains("đang") || t.Contains("retry") || t.Contains("thử lại"))
            return Color.FromArgb(200, 200, 130);
        return Color.FromArgb(215, 215, 220);
    }
}
