using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace OpenMultiBraveLauncher;

/// <summary>
/// Encapsulates one Brave browser instance: its UI card + all proxy/process logic.
/// Each instance gets a unique CDP port and an independent proxy monitor.
/// </summary>
internal sealed class BraveInstanceCard : IDisposable
{
    private const string KiotProxyNewUrl     = "https://api.kiotproxy.com/api/v1/proxies/new";
    private const string KiotProxyCurrentUrl = "https://api.kiotproxy.com/api/v1/proxies/current";

    // ── UI ──────────────────────────────────────────────────────────────────
    public Control Root { get; }
    private readonly Label    _statusLabel;
    private readonly Button   _startStopButton;
    private readonly Button   _exportButton;
    private readonly Button   _importButton;
    private readonly TextBox  _keyTextBox;
    private readonly ComboBox _regionComboBox;
    private readonly ComboBox _proxyTypeComboBox;
    private readonly CheckBox _shopeeCheck;
    private readonly CheckBox _bigsellerCheck;

    // ── Identity ─────────────────────────────────────────────────────────────
    private readonly int    _index;
    private readonly int    _cdpPort;

    // ── Deps injected from MainForm ──────────────────────────────────────────
    private readonly Func<string>    _getBraveExe;
    private readonly Func<string>    _getSourceUserData;
    private readonly Action<string>  _globalLog;

    // ── Runtime state ────────────────────────────────────────────────────────
    private Process?       _braveProcess;
    private DirectoryInfo? _profileRoot;
    private string?        _currentProxyFingerprint;
    private bool           _running;
    private bool           _busy;
    private bool           _restarting;

    private readonly System.Windows.Forms.Timer _monitorTimer;

    // ── Construction ─────────────────────────────────────────────────────────

    public BraveInstanceCard(
        int index,
        int cdpPort,
        Func<string> getBraveExe,
        Func<string> getSourceUserData,
        Action<string> globalLog)
    {
        _index          = index;
        _cdpPort        = cdpPort;
        _getBraveExe    = getBraveExe;
        _getSourceUserData = getSourceUserData;
        _globalLog      = globalLog;

        _monitorTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
        _monitorTimer.Tick += async (_, _) => await CheckProxyAndRestartIfNeededAsync();

        Root = BuildCard(out _statusLabel, out _startStopButton, out _exportButton,
                         out _importButton, out _keyTextBox, out _regionComboBox,
                         out _proxyTypeComboBox, out _shopeeCheck, out _bigsellerCheck);
    }

    private Control BuildCard(
        out Label statusLabel,
        out Button startStopButton,
        out Button exportButton,
        out Button importButton,
        out TextBox keyTextBox,
        out ComboBox regionComboBox,
        out ComboBox proxyTypeComboBox,
        out CheckBox shopeeCheck,
        out CheckBox bigsellerCheck)
    {
        var group = new GroupBox
        {
            Text    = $"Instance {_index}  —  CDP port: {_cdpPort}",
            AutoSize     = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8, 12, 8, 8),
            Margin  = new Padding(0, 0, 0, 6),
            Font    = new Font("Segoe UI", 9, FontStyle.Bold),
        };

        var table = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            AutoSize    = true,
            ColumnCount = 4,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        group.Controls.Add(table);

        // Row 0 — KiotProxy key
        table.Controls.Add(CardLabel("KiotProxy key"), 0, 0);
        keyTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 9, FontStyle.Regular),
        };
        table.Controls.Add(keyTextBox, 1, 0);
        table.SetColumnSpan(keyTextBox, 3);

        // Row 1 — Region + Proxy type
        table.Controls.Add(CardLabel("Region"), 0, 1);
        regionComboBox = MakeCombo("random", "random", "bac", "trung", "nam");
        table.Controls.Add(regionComboBox, 1, 1);
        table.Controls.Add(CardLabel("Proxy type"), 2, 1);
        proxyTypeComboBox = MakeCombo("http", "http", "socks5");
        table.Controls.Add(proxyTypeComboBox, 3, 1);

        // Row 2 — Status
        statusLabel = new Label
        {
            Text      = "● Stopped",
            ForeColor = Color.Gray,
            AutoSize  = true,
            Font      = new Font("Segoe UI", 9, FontStyle.Regular),
            Padding   = new Padding(0, 4, 0, 0),
        };
        table.Controls.Add(statusLabel, 0, 2);
        table.SetColumnSpan(statusLabel, 4);

        // Row 3 — Buttons
        var btnFlow = new FlowLayoutPanel
        {
            Dock            = DockStyle.Fill,
            AutoSize        = true,
            FlowDirection   = FlowDirection.LeftToRight,
            WrapContents    = true,
            Padding         = new Padding(0, 4, 0, 0),
        };

        startStopButton = new Button
        {
            Text   = "Start",
            Width  = 80,
            Height = 30,
            Font   = new Font("Segoe UI", 9, FontStyle.Regular),
        };
        startStopButton.Click += async (_, _) => await ToggleAsync();
        btnFlow.Controls.Add(startStopButton);

        exportButton = new Button
        {
            Text    = "Export Cookie",
            Width   = 115,
            Height  = 30,
            Enabled = false,
            Font    = new Font("Segoe UI", 9, FontStyle.Regular),
        };
        exportButton.Click += async (_, _) => await ExportCookiesAsync();
        btnFlow.Controls.Add(exportButton);

        importButton = new Button
        {
            Text    = "Import Cookie",
            Width   = 115,
            Height  = 30,
            Enabled = false,
            Font    = new Font("Segoe UI", 9, FontStyle.Regular),
        };
        importButton.Click += async (_, _) => await ImportCookiesAsync();
        btnFlow.Controls.Add(importButton);

        shopeeCheck = new CheckBox
        {
            Text    = "Shopee",
            Checked = true,
            AutoSize = true,
            Padding = new Padding(6, 7, 0, 0),
            Font    = new Font("Segoe UI", 9, FontStyle.Regular),
        };
        btnFlow.Controls.Add(shopeeCheck);

        bigsellerCheck = new CheckBox
        {
            Text    = "BigSeller",
            Checked = true,
            AutoSize = true,
            Padding = new Padding(2, 7, 0, 0),
            Font    = new Font("Segoe UI", 9, FontStyle.Regular),
        };
        btnFlow.Controls.Add(bigsellerCheck);

        table.Controls.Add(btnFlow, 0, 3);
        table.SetColumnSpan(btnFlow, 4);

        return group;
    }

    internal string GetProxyKey() => _keyTextBox.Text.Trim();

    internal void SetProxyKey(string key) => _keyTextBox.Text = key;

    // ── Start / Stop ─────────────────────────────────────────────────────────

    private async Task ToggleAsync()
    {
        if (_running)
            Stop();
        else
            await StartAsync();
    }

    private async Task StartAsync()
    {
        if (_busy) return;
        _busy = true;
        _startStopButton.Enabled = false;
        SetStatus("● Starting…", Color.DarkOrange);

        try
        {
            var braveExe = new FileInfo(_getBraveExe());
            if (!braveExe.Exists)
                throw new FileNotFoundException("Brave executable not found.", braveExe.FullName);

            var sourceData = new DirectoryInfo(_getSourceUserData());
            if (!sourceData.Exists)
                throw new DirectoryNotFoundException("Brave User Data folder not found.");

            _profileRoot = CreateProfileFromDefault(sourceData);
            Log($"Profile created: {_profileRoot.FullName}");

            var proxy      = await GetWorkingProxyAsync();
            var proxyType  = SelectedProxyType();
            var proxyServer = BuildProxyServer(proxy, proxyType);
            var args       = BuildBraveArguments(_profileRoot.FullName, proxyServer);

            LaunchBrave(braveExe.FullName, args);
            _currentProxyFingerprint = BuildFingerprint(proxy);
            _monitorTimer.Start();

            SetButtonState(running: true);
            SetStatus($"● Running — {proxyServer}", Color.DarkGreen);
        }
        catch (Exception ex)
        {
            SetStatus($"● Error: {ex.Message}", Color.Red);
            MessageBox.Show(ex.Message, $"Instance {_index}", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _busy = false;
            _startStopButton.Enabled = true;
        }
    }

    public void Stop()
    {
        _monitorTimer.Stop();
        KillBraveProcess();
        SetButtonState(running: false);
        SetStatus("● Stopped", Color.Gray);
    }

    public void Dispose()
    {
        _monitorTimer.Stop();
        _monitorTimer.Dispose();
        KillBraveProcess();
    }

    // ── Brave process management ──────────────────────────────────────────────

    private void LaunchBrave(string exePath, string arguments)
    {
        KillBraveProcess();
        _braveProcess = Process.Start(new ProcessStartInfo
        {
            FileName         = exePath,
            Arguments        = arguments,
            UseShellExecute  = false,
        });
        Log($"Launched Brave PID={_braveProcess?.Id}");
    }

    private void KillBraveProcess()
    {
        if (_braveProcess is null) return;
        try
        {
            if (!_braveProcess.HasExited)
            {
                _braveProcess.Kill(entireProcessTree: true);
                _braveProcess.WaitForExit(10_000);
                Log("Brave process killed.");
            }
        }
        catch { }
        finally
        {
            _braveProcess.Dispose();
            _braveProcess = null;
        }
    }

    private string BuildBraveArguments(string userDataDir, string proxyServer)
    {
        var extensionPath = Path.Combine(AppContext.BaseDirectory, "extension");
        var parts = new List<string>
        {
            $"--user-data-dir=\"{userDataDir}\"",
            "--profile-directory=Default",
            "--new-window",
            "--no-first-run",
            "--no-default-browser-check",
            $"--remote-debugging-port={_cdpPort}",
            $"--proxy-server={proxyServer}",
        };

        if (Directory.Exists(extensionPath))
            parts.Add($"--load-extension=\"{extensionPath}\"");

        return string.Join(" ", parts);
    }

    // ── Proxy helpers ─────────────────────────────────────────────────────────

    private string SelectedProxyType() =>
        (_proxyTypeComboBox.SelectedItem?.ToString() ?? "http").Trim().ToLowerInvariant();

    private async Task<Dictionary<string, object>> GetProxyAsync()
    {
        var key    = _keyTextBox.Text.Trim();
        var region = (_regionComboBox.SelectedItem?.ToString() ?? "random").Trim();
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("KiotProxy key is required.");

        var url = $"{KiotProxyNewUrl}?key={Uri.EscapeDataString(key)}&region={Uri.EscapeDataString(region)}";
        Log($"Requesting proxy: {url}");

        using var response = await MainForm.Http.GetAsync(url);
        var json = await response.Content.ReadAsStringAsync();
        response.EnsureSuccessStatusCode();

        using var doc  = JsonDocument.Parse(json);
        var root       = doc.RootElement;

        if (!root.TryGetProperty("success", out var s) || !s.GetBoolean())
        {
            var msg = root.TryGetProperty("message", out var m) ? m.GetString() : null;
            var err = root.TryGetProperty("error",   out var e) ? e.GetString() : null;
            throw new InvalidOperationException(msg ?? err ?? "KiotProxy returned failure.");
        }

        return ParseProxyData(root.GetProperty("data"));
    }

    private async Task<Dictionary<string, object>> GetWorkingProxyAsync(int maxAttempts = 5)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var proxy       = await GetProxyAsync();
            var proxyServer = BuildProxyServer(proxy, SelectedProxyType());
            Log($"Validating proxy attempt {attempt}/{maxAttempts}: {proxyServer}");
            if (await IsProxyWorkingAsync(proxyServer))
                return proxy;
            lastError = new InvalidOperationException($"Proxy test failed: {proxyServer}");
        }
        throw lastError ?? new InvalidOperationException("All proxy attempts failed.");
    }

    private async Task<Dictionary<string, object>> GetCurrentProxyAsync()
    {
        var key = _keyTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("KiotProxy key is required.");

        var url = $"{KiotProxyCurrentUrl}?key={Uri.EscapeDataString(key)}";
        using var response = await MainForm.Http.GetAsync(url);
        var json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"KiotProxy current API {(int)response.StatusCode}: {ExtractError(json)}");

        using var doc = JsonDocument.Parse(json);
        var root      = doc.RootElement;

        if (!root.TryGetProperty("success", out var s) || !s.GetBoolean())
        {
            var msg = root.TryGetProperty("message", out var m) ? m.GetString() : null;
            var err = root.TryGetProperty("error",   out var e) ? e.GetString() : null;
            throw new InvalidOperationException(msg ?? err ?? "KiotProxy returned failure.");
        }

        return ParseProxyData(root.GetProperty("data"));
    }

    private static Dictionary<string, object> ParseProxyData(JsonElement data)
    {
        var proxy = new Dictionary<string, object>();
        foreach (var prop in data.EnumerateObject())
        {
            proxy[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                JsonValueKind.Number => prop.Value.TryGetInt64(out var i) ? i : prop.Value.GetDouble(),
                JsonValueKind.True   => true,
                JsonValueKind.False  => false,
                _                   => prop.Value.ToString() ?? string.Empty,
            };
        }
        return proxy;
    }

    private static string BuildProxyServer(Dictionary<string, object> proxy, string selectedType)
    {
        string? value = selectedType == "socks5"
            ? proxy.TryGetValue("socks5", out var s) ? s?.ToString() : null
            : proxy.TryGetValue("http",   out var h) ? h?.ToString() : null;

        value ??= proxy.TryGetValue("http",   out var hf) ? hf?.ToString() : null;
        value ??= proxy.TryGetValue("socks5", out var sf) ? sf?.ToString() : null;

        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("KiotProxy did not return a usable proxy endpoint.");

        return value.StartsWith("http://",   StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase)
            ? value
            : $"{(selectedType == "socks5" ? "socks5" : "http")}://{value}";
    }

    private static async Task<bool> IsProxyWorkingAsync(string proxyServer)
    {
        try
        {
            var handler = new HttpClientHandler
            {
                Proxy = new System.Net.WebProxy(proxyServer),
                UseProxy = true,
                AutomaticDecompression = System.Net.DecompressionMethods.All,
            };
            using var client   = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            using var response = await client.GetAsync("https://api.ipify.org?format=json");
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private static string BuildFingerprint(Dictionary<string, object> proxy)
    {
        static string Get(Dictionary<string, object> p, string k) =>
            p.TryGetValue(k, out var v) ? v?.ToString() ?? "" : "";
        return string.Join("|", Get(proxy, "realIpAddress"), Get(proxy, "host"),
                                Get(proxy, "http"),          Get(proxy, "socks5"));
    }

    private static string ExtractError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var parts = new List<string>();
            if (root.TryGetProperty("message", out var m) && !string.IsNullOrWhiteSpace(m.GetString()))
                parts.Add(m.GetString()!);
            if (root.TryGetProperty("error", out var e) && !string.IsNullOrWhiteSpace(e.GetString()))
                parts.Add(e.GetString()!);
            if (parts.Count > 0) return string.Join(" | ", parts);
        }
        catch { }
        return string.IsNullOrWhiteSpace(json) ? "Unknown error." : json.Trim();
    }

    private static bool IsProxyExpiredError(string msg) =>
        msg.Contains("KiotProxy current API returned 400",       StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("PROXY_NOT_FOUND_BY_KEY",                   StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("Could not find the proxy being used by key", StringComparison.OrdinalIgnoreCase);

    // ── Proxy monitor ─────────────────────────────────────────────────────────

    private async Task CheckProxyAndRestartIfNeededAsync()
    {
        if (!_running || _restarting || _busy || _profileRoot is null || _braveProcess is null)
            return;

        try
        {
            Dictionary<string, object> current;
            try
            {
                current = await GetCurrentProxyAsync();
            }
            catch (HttpRequestException ex)
            {
                SetStatus("⚠ Không kết nối được KiotProxy API", Color.DarkOrange);
                Log($"Monitor network error: {ex.Message}");
                return;
            }
            catch (Exception ex) when (IsProxyExpiredError(ex.Message))
            {
                _restarting = true;
                Log("Proxy hết/không còn gán. Lấy proxy mới…");
                try   { await RestartWithFreshProxyAsync(); }
                catch (Exception rfEx) { Log($"Refresh error: {rfEx.Message}"); }
                return;
            }

            var fp     = BuildFingerprint(current);
            var server = BuildProxyServer(current, SelectedProxyType());

            // Proxy identity changed → restart immediately
            if (!string.Equals(fp, _currentProxyFingerprint, StringComparison.Ordinal))
            {
                _restarting = true;
                Log($"Proxy changed → {server}. Restarting…");
                var args = BuildBraveArguments(_profileRoot.FullName, server);
                KillBraveProcess();
                LaunchBrave(new FileInfo(_getBraveExe()).FullName, args);
                _currentProxyFingerprint = fp;
                SetStatus($"● Running — {server} (updated)", Color.DarkGreen);
                return;
            }

            // Proxy identity same → test actual connectivity
            Log($"Proxy unchanged. Testing: {server}");
            if (!await IsProxyWorkingAsync(server))
            {
                _restarting = true;
                Log("Proxy không phản hồi. Lấy proxy mới…");
                try   { await RestartWithFreshProxyAsync(); }
                catch (Exception rfEx) { Log($"Refresh error: {rfEx.Message}"); SetStatus($"⚠ Lấy proxy mới thất bại: {rfEx.Message}", Color.Red); }
                return;
            }

            Log("Proxy OK.");
        }
        catch (Exception ex)
        {
            SetStatus($"⚠ Monitor error: {ex.Message}", Color.Red);
            Log($"Monitor error: {ex.Message}");
        }
        finally
        {
            _restarting = false;
        }
    }

    private async Task RestartWithFreshProxyAsync()
    {
        Log("Requesting fresh proxy…");
        var proxy  = await GetWorkingProxyAsync();
        var server = BuildProxyServer(proxy, SelectedProxyType());
        var args   = BuildBraveArguments(_profileRoot!.FullName, server);
        KillBraveProcess();
        LaunchBrave(new FileInfo(_getBraveExe()).FullName, args);
        _currentProxyFingerprint = BuildFingerprint(proxy);
        SetStatus($"● Running — {server} (refreshed)", Color.DarkGreen);
    }

    // ── Profile helpers ───────────────────────────────────────────────────────

    private static DirectoryInfo CreateProfileFromDefault(DirectoryInfo sourceUserData)
    {
        var sourceDefault = new DirectoryInfo(Path.Combine(sourceUserData.FullName, "Default"));
        if (!sourceDefault.Exists)
            throw new DirectoryNotFoundException($"Default profile not found: {sourceDefault.FullName}");

        var profilesDir = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "profiles"));
        profilesDir.Create();

        var profileRoot = new DirectoryInfo(
            Path.Combine(profilesDir.FullName, $"brave_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}"));
        profileRoot.Create();

        var targetDefault = new DirectoryInfo(Path.Combine(profileRoot.FullName, "Default"));
        targetDefault.Create();

        CopyExtensionState(sourceDefault, targetDefault);
        return profileRoot;
    }

    private static void CopyExtensionState(DirectoryInfo src, DirectoryInfo dst)
    {
        foreach (var file in new[] { "Preferences", "Secure Preferences", "Bookmarks" })
        {
            var s = Path.Combine(src.FullName, file);
            var d = Path.Combine(dst.FullName, file);
            if (File.Exists(s)) File.Copy(s, d, overwrite: true);
        }

        foreach (var dir in new[]
        {
            "Extensions", "Extension Rules", "Extension State",
            "Local Extension Settings", "Sync Extension Settings", "Managed Extension State",
        })
        {
            var s = Path.Combine(src.FullName, dir);
            var d = Path.Combine(dst.FullName, dir);
            if (Directory.Exists(s)) CopyDir(s, d);
        }
    }

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.GetDirectories(src))
            CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    // ── Cookie export / import via CDP ────────────────────────────────────────

    private async Task ExportCookiesAsync()
    {
        try
        {
            SetStatus("● Exporting cookies…", Color.DarkBlue);
            var cookies = await GetAllCookiesFromBraveAsync();

            var defaultName = $"cookies-instance{_index}.json";
            using var dlg = new SaveFileDialog
            {
                Filter          = "JSON (*.json)|*.json|All files|*.*",
                FileName        = defaultName,
                InitialDirectory = AppContext.BaseDirectory,
                Title           = $"Export cookies — Instance {_index}",
            };
            if (dlg.ShowDialog() != DialogResult.OK)
            {
                SetStatus($"● Running", Color.DarkGreen);
                return;
            }

            var payload = new { exportedAt = DateTimeOffset.Now, cookies };
            var json    = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(dlg.FileName, json, Encoding.UTF8);

            var shopeeCount    = cookies.Count(c => DomainContains(c, "shopee"));
            var bigsellerCount = cookies.Count(c => DomainContains(c, "bigseller"));
            Log($"Exported {cookies.Count} cookies (Shopee: {shopeeCount}, BigSeller: {bigsellerCount}) → {dlg.FileName}");
            SetStatus("● Running", Color.DarkGreen);
        }
        catch (Exception ex)
        {
            SetStatus($"⚠ Export error: {ex.Message}", Color.Red);
            Log($"Export error: {ex}");
        }
    }

    private async Task ImportCookiesAsync()
    {
        try
        {
            using var dlg = new OpenFileDialog
            {
                Filter = "JSON (*.json)|*.json|All files|*.*",
                Title  = $"Import cookies — Instance {_index}",
            };
            if (dlg.ShowDialog() != DialogResult.OK) return;

            SetStatus("● Importing cookies…", Color.DarkBlue);
            var json = await File.ReadAllTextAsync(dlg.FileName, Encoding.UTF8);
            using var doc    = JsonDocument.Parse(json);
            var root         = doc.RootElement;
            var cookiesEl    = root.TryGetProperty("cookies", out var cp) ? cp : root;

            if (cookiesEl.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Invalid cookie file: expected array or { cookies: [] }.");

            if (!_shopeeCheck.Checked && !_bigsellerCheck.Checked)
                throw new InvalidOperationException("Choose at least one site: Shopee or BigSeller.");

            var count = await SetCookiesToBraveAsync(cookiesEl, _shopeeCheck.Checked, _bigsellerCheck.Checked);
            Log($"Imported {count} cookies.");
            SetStatus("● Running", Color.DarkGreen);
        }
        catch (Exception ex)
        {
            SetStatus($"⚠ Import error: {ex.Message}", Color.Red);
            Log($"Import error: {ex}");
        }
    }

    private async Task<string> GetCdpWebSocketUrlAsync()
    {
        using var response = await MainForm.DirectHttp.GetAsync(
            $"http://127.0.0.1:{_cdpPort}/json/list");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Unexpected CDP /json/list response.");

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var type = item.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (!string.Equals(type, "page", StringComparison.OrdinalIgnoreCase)) continue;
            if (item.TryGetProperty("webSocketDebuggerUrl", out var wsProp))
            {
                var ws = wsProp.GetString();
                if (!string.IsNullOrWhiteSpace(ws)) return ws;
            }
        }

        throw new InvalidOperationException(
            $"No page found on CDP port {_cdpPort}. Open at least one normal tab.");
    }

    private static async Task<JsonElement> SendCdpAsync(
        ClientWebSocket socket, int id, string method, object? @params)
    {
        var bytes = Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { id, method, @params }));
        await socket.SendAsync(
            new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);

        var buffer = new byte[1024 * 512];
        while (true)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult recv;
            do
            {
                recv = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (recv.MessageType == WebSocketMessageType.Close)
                    throw new InvalidOperationException("CDP socket closed.");
                ms.Write(buffer, 0, recv.Count);
            } while (!recv.EndOfMessage);

            using var doc  = JsonDocument.Parse(ms.ToArray());
            var root       = doc.RootElement;
            if (!root.TryGetProperty("id", out var idProp) || idProp.GetInt32() != id)
                continue;
            if (root.TryGetProperty("error",  out var err))
                throw new InvalidOperationException($"CDP error: {err}");
            if (!root.TryGetProperty("result", out var result))
                throw new InvalidOperationException("CDP result missing.");
            return result.Clone();
        }
    }

    private async Task<List<Dictionary<string, object?>>> GetAllCookiesFromBraveAsync()
    {
        var wsUrl = await GetCdpWebSocketUrlAsync();
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(wsUrl), CancellationToken.None);

        var result = await SendCdpAsync(socket, 1, "Network.getAllCookies", null);
        if (!result.TryGetProperty("cookies", out var cookiesEl) ||
            cookiesEl.ValueKind != JsonValueKind.Array)
            return new List<Dictionary<string, object?>>();

        var list = new List<Dictionary<string, object?>>();
        foreach (var cookie in cookiesEl.EnumerateArray())
        {
            var domain = cookie.TryGetProperty("domain", out var dp) ? (dp.GetString() ?? "") : "";
            var lower  = domain.ToLowerInvariant();
            if (!lower.Contains("shopee") && !lower.Contains("bigseller")) continue;

            var map = new Dictionary<string, object?>();
            foreach (var p in cookie.EnumerateObject())
            {
                map[p.Name] = p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString(),
                    JsonValueKind.Number => p.Value.TryGetInt64(out var i) ? i : p.Value.GetDouble(),
                    JsonValueKind.True   => true,
                    JsonValueKind.False  => false,
                    _                   => p.Value.ToString(),
                };
            }
            list.Add(map);
        }
        return list;
    }

    private async Task<int> SetCookiesToBraveAsync(
        JsonElement cookiesArray, bool includeShopee, bool includeBigseller)
    {
        var wsUrl = await GetCdpWebSocketUrlAsync();
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(wsUrl), CancellationToken.None);
        await SendCdpAsync(socket, 1, "Network.enable", new { });

        var attempted = 0;
        var succeeded = 0;
        var cmdId     = 1000;

        foreach (var cookie in cookiesArray.EnumerateArray())
        {
            if (cookie.ValueKind != JsonValueKind.Object) continue;

            var domain     = cookie.TryGetProperty("domain", out var dp) ? (dp.GetString() ?? "") : "";
            var lower      = domain.ToLowerInvariant();
            var isShopee   = lower.Contains("shopee");
            var isBigseller = lower.Contains("bigseller");

            if (isShopee    && !includeShopee)    continue;
            if (isBigseller && !includeBigseller) continue;
            if (!isShopee   && !isBigseller)      continue;

            var payload = new Dictionary<string, object?>();
            foreach (var k in new[]
            {
                "name", "value", "url", "domain", "path",
                "secure", "httpOnly", "sameSite", "expires",
                "priority", "sourceScheme", "sourcePort",
            })
            {
                if (!cookie.TryGetProperty(k, out var v)) continue;
                payload[k] = v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString(),
                    JsonValueKind.Number => v.TryGetInt64(out var i) ? i : v.GetDouble(),
                    JsonValueKind.True   => true,
                    JsonValueKind.False  => false,
                    _                   => null,
                };
            }

            if (!payload.ContainsKey("name") || !payload.ContainsKey("value")) continue;
            if (!payload.ContainsKey("url")  && !payload.ContainsKey("domain")) continue;

            // Synthesise url from domain when absent
            if (!payload.ContainsKey("url") && payload.TryGetValue("domain", out var dv))
            {
                var ds = (dv as string ?? "").TrimStart('.');
                if (!string.IsNullOrEmpty(ds))
                    payload["url"] = $"https://{ds}/";
            }

            // __Host- cookies must be host-only
            var cookieName = payload.TryGetValue("name", out var nv) ? nv as string ?? "" : "";
            if (cookieName.StartsWith("__Host-", StringComparison.OrdinalIgnoreCase))
            {
                payload.Remove("domain");
                payload["path"] = "/";
            }

            attempted++;
            try
            {
                var result = await SendCdpAsync(socket, cmdId++, "Network.setCookie", payload);
                var ok     = result.TryGetProperty("success", out var sp) && sp.GetBoolean();
                if (!ok)
                {
                    var fb = new Dictionary<string, object?>(payload);
                    fb.Remove("sourceScheme");
                    fb.Remove("sourcePort");
                    var fbResult = await SendCdpAsync(socket, cmdId++, "Network.setCookie", fb);
                    ok = fbResult.TryGetProperty("success", out var fp) && fp.GetBoolean();
                }
                if (ok) succeeded++;
                else Log($"  [FAIL] {cookieName} @ {domain}");
            }
            catch (Exception ex)
            {
                Log($"  [ERR] {cookieName} @ {domain} — {ex.Message}");
            }
        }

        Log($"Import result: {succeeded}/{attempted} cookies set.");
        return succeeded;
    }

    // ── UI helpers ────────────────────────────────────────────────────────────

    private static bool DomainContains(Dictionary<string, object?> cookie, string part) =>
        cookie.TryGetValue("domain", out var d) &&
        (d as string ?? "").Contains(part, StringComparison.OrdinalIgnoreCase);

    private void SetStatus(string message, Color? color = null)
    {
        if (_statusLabel.InvokeRequired)
        {
            _statusLabel.BeginInvoke(new Action(() => SetStatus(message, color)));
            return;
        }
        _statusLabel.Text      = message;
        _statusLabel.ForeColor = color ?? Color.DarkBlue;
        Log(message);
    }

    private void SetButtonState(bool running)
    {
        if (_startStopButton.InvokeRequired)
        {
            _startStopButton.BeginInvoke(new Action(() => SetButtonState(running)));
            return;
        }
        _running = running;
        _startStopButton.Text      = running ? "Stop"  : "Start";
        _startStopButton.BackColor = running ? Color.FromArgb(210, 70, 70) : Color.FromArgb(60, 150, 60);
        _startStopButton.ForeColor = Color.White;
        _exportButton.Enabled      = running;
        _importButton.Enabled      = running;
    }

    private void Log(string message) => _globalLog($"[I{_index}] {message}");

    private static Label CardLabel(string text) =>
        new()
        {
            Text    = text,
            AutoSize = true,
            Padding = new Padding(0, 6, 0, 0),
            Font    = new Font("Segoe UI", 9, FontStyle.Regular),
        };

    private static ComboBox MakeCombo(string defaultVal, params string[] items)
    {
        var c = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width         = 160,
            Font          = new Font("Segoe UI", 9, FontStyle.Regular),
        };
        c.Items.AddRange(items);
        c.SelectedItem = defaultVal;
        return c;
    }
}
