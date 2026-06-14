using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Windows.Forms;
using Microsoft.Win32;

namespace OpenMultiBraveLauncher;

public sealed class MainForm : Form
{
    // CDP ports start at 9230 to avoid collision with single-brave tool (9224).
    private const int BaseCdpPort = 9230;

    internal static readonly HttpClient Http = new();
    internal static readonly HttpClient DirectHttp = new(new HttpClientHandler
    {
        UseProxy = false,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    private readonly TextBox _bravePathTextBox;
    private readonly TextBox _userDataTextBox;
    private readonly Panel _scrollContainer;
    private readonly FlowLayoutPanel _instancesPanel;
    private readonly TextBox _logTextBox;
    private readonly List<BraveInstanceCard> _instances = new();

    public MainForm()
    {
        Text = "Multi Brave Manager";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 920;
        Height = 780;
        MinimumSize = new Size(700, 500);

        var outer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 5,
        };
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));  // title
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));  // global settings
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));  // add/remove buttons
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // instances scroll area
        outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 160)); // log
        Controls.Add(outer);

        // --- Title ---
        var title = new Label
        {
            Text = "Multi Brave Manager",
            Font = new Font("Segoe UI", 15, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8),
        };
        outer.Controls.Add(title, 0, 0);

        // --- Global settings: Brave exe + User Data ---
        var settingsTable = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 2,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 6),
        };
        settingsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        settingsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        settingsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));

        settingsTable.Controls.Add(SmallLabel("Brave exe"), 0, 0);
        _bravePathTextBox = new TextBox { Dock = DockStyle.Fill };
        settingsTable.Controls.Add(_bravePathTextBox, 1, 0);
        var braveBrowse = SmallButton("Browse");
        braveBrowse.Click += (_, _) => BrowseBraveExe();
        settingsTable.Controls.Add(braveBrowse, 2, 0);

        settingsTable.Controls.Add(SmallLabel("Source user data"), 0, 1);
        _userDataTextBox = new TextBox { Dock = DockStyle.Fill };
        settingsTable.Controls.Add(_userDataTextBox, 1, 1);
        var udBrowse = SmallButton("Browse");
        udBrowse.Click += (_, _) => BrowseUserData();
        settingsTable.Controls.Add(udBrowse, 2, 1);

        outer.Controls.Add(settingsTable, 0, 1);

        // --- Add / Remove buttons ---
        var btnRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 4, 0, 4),
        };
        var addBtn = new Button { Text = "+ Add Instance", AutoSize = true, Height = 32 };
        addBtn.Click += (_, _) => AddInstance();
        btnRow.Controls.Add(addBtn);
        var removeBtn = new Button { Text = "− Remove Last", AutoSize = true, Height = 32 };
        removeBtn.Click += (_, _) => RemoveLastInstance();
        btnRow.Controls.Add(removeBtn);
        var saveKeysBtn = new Button { Text = "Save proxy keys", AutoSize = true, Height = 32 };
        saveKeysBtn.Click += (_, _) => SaveProxyKeys();
        btnRow.Controls.Add(saveKeysBtn);
        outer.Controls.Add(btnRow, 0, 2);

        // --- Scrollable instances area ---
        _scrollContainer = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BorderStyle = BorderStyle.FixedSingle,
        };
        _instancesPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(4),
        };
        _scrollContainer.Controls.Add(_instancesPanel);
        _scrollContainer.Resize += (_, _) => ResizeInstanceCards();
        outer.Controls.Add(_scrollContainer, 0, 3);

        // --- Log ---
        var logGroup = new GroupBox { Text = "Log", Dock = DockStyle.Fill };
        _logTextBox = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            ReadOnly = true,
            WordWrap = false,
            Font = new Font("Consolas", 8.5f),
        };
        logGroup.Controls.Add(_logTextBox);
        outer.Controls.Add(logGroup, 0, 4);

        Load += (_, _) => OnLoad();
        FormClosing += (_, _) => CleanupAll();
    }

    private void OnLoad()
    {
        var brave = DetectBraveExe();
        var userData = DetectBraveUserData();
        if (brave is not null) _bravePathTextBox.Text = brave.FullName;
        if (userData is not null) _userDataTextBox.Text = userData.FullName;
        LoadSavedProxyKeys();
    }

    private void LoadSavedProxyKeys()
    {
        var saved = LauncherSettings.LoadProxyKeys();
        if (saved.Count == 0 || saved.All(string.IsNullOrWhiteSpace))
        {
            AddInstance();
            return;
        }

        for (var i = 0; i < saved.Count; i++)
            AddInstance();

        AppendLog($"Loaded {saved.Count} proxy key(s) from {LauncherSettings.SettingsPath}");
    }

    private void SaveProxyKeys()
    {
        if (_instances.Count == 0)
        {
            MessageBox.Show(this, "No instance to save.", "Save proxy keys",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var keys = _instances.Select(i => i.GetProxyKey()).ToList();
        if (keys.All(string.IsNullOrWhiteSpace))
        {
            MessageBox.Show(this, "Enter at least one KiotProxy key.", "Save proxy keys",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            LauncherSettings.SaveProxyKeys(keys);
            AppendLog($"Saved {keys.Count(k => !string.IsNullOrWhiteSpace(k))} proxy key(s) to {LauncherSettings.SettingsPath}");
            MessageBox.Show(this,
                $"Saved {keys.Count(k => !string.IsNullOrWhiteSpace(k))} key(s).\n{LauncherSettings.SettingsPath}",
                "Save proxy keys", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            AppendLog($"Save proxy keys failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Save proxy keys",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void AddInstance()
    {
        var index = _instances.Count + 1;
        var cdpPort = BaseCdpPort + (_instances.Count);
        var card = new BraveInstanceCard(
            index, cdpPort,
            () => _bravePathTextBox.Text.Trim(),
            () => _userDataTextBox.Text.Trim(),
            AppendLog);

        _instances.Add(card);
        card.Root.Width = CardWidth();
        _instancesPanel.Controls.Add(card.Root);
        _scrollContainer.ScrollControlIntoView(card.Root);

        var saved = LauncherSettings.LoadProxyKeys();
        if (saved.Count >= index)
            card.SetProxyKey(saved[index - 1]);
    }

    private void RemoveLastInstance()
    {
        if (_instances.Count == 0) return;
        var last = _instances[^1];
        last.Dispose();
        _instancesPanel.Controls.Remove(last.Root);
        _instances.RemoveAt(_instances.Count - 1);
    }

    private void CleanupAll()
    {
        foreach (var card in _instances)
            card.Dispose();
    }

    private void ResizeInstanceCards()
    {
        var w = CardWidth();
        foreach (Control c in _instancesPanel.Controls)
            c.Width = w;
    }

    private int CardWidth() =>
        Math.Max(400, _scrollContainer.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 8);

    internal void AppendLog(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => AppendLog(message)));
            return;
        }
        _logTextBox.AppendText($"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}");
        _logTextBox.SelectionStart = _logTextBox.TextLength;
        _logTextBox.ScrollToCaret();
    }

    private void BrowseBraveExe()
    {
        using var d = new OpenFileDialog
        {
            Filter = "brave.exe|brave.exe|*.exe|*.exe|All files|*.*",
            Title = "Select brave.exe",
        };
        if (d.ShowDialog(this) == DialogResult.OK)
            _bravePathTextBox.Text = d.FileName;
    }

    private void BrowseUserData()
    {
        using var d = new FolderBrowserDialog { Description = "Select Brave User Data folder" };
        if (d.ShowDialog(this) == DialogResult.OK)
            _userDataTextBox.Text = d.SelectedPath;
    }

    private static Label SmallLabel(string text) =>
        new() { Text = text, AutoSize = true, Padding = new Padding(0, 7, 0, 0) };

    private static Button SmallButton(string text) =>
        new() { Text = text, Dock = DockStyle.Fill };

    private static FileInfo? DetectBraveExe()
    {
        var candidates = new List<string>();
        foreach (var folder in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        })
        {
            if (!string.IsNullOrWhiteSpace(folder))
                candidates.Add(Path.Combine(folder, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"));
        }

        foreach (var (root, sub) in new[]
        {
            (Registry.CurrentUser,  @"Software\Microsoft\Windows\CurrentVersion\App Paths\brave.exe"),
            (Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\App Paths\brave.exe"),
        })
        {
            try
            {
                using var key = root.OpenSubKey(sub);
                var val = key?.GetValue(string.Empty)?.ToString();
                if (!string.IsNullOrWhiteSpace(val)) candidates.Add(val);
            }
            catch { }
        }

        foreach (var c in candidates)
            if (File.Exists(c)) return new FileInfo(c);
        return null;
    }

    private static DirectoryInfo? DetectBraveUserData()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData)) return null;
        var candidate = Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "User Data");
        return Directory.Exists(candidate) ? new DirectoryInfo(candidate) : null;
    }
}
