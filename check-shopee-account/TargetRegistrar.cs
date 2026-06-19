using System.Text.Json.Nodes;

namespace CheckShopeeAccount;

public enum ExportApp { ShopeeStat, V31 }

/// <summary>
/// Đăng ký tài khoản OK vào settings.json của app đích (shopee-stat / v31) và cho biết nơi đặt
/// profile. Giữ nguyên mọi field khác trong file (dùng JsonNode), backup trước khi ghi, dedupe
/// theo chuỗi login (cùng tk thì cập nhật lại, không tạo trùng).
///
/// Khác biệt 2 app:
///  - shopee-stat: settings ở %AppData%/ShopeeStatApp/settings.json, JSON camelCase, profile
///    resolve theo CWD nên ta đặt ProfileRelativePath = đường dẫn TUYỆT ĐỐI (khỏi lệ thuộc CWD).
///  - v31: settings ở open-multi-brave-v31/launcher-settings.json, JSON PascalCase, profile ở
///    persistent-data/profiles/&lt;Id&gt; nên ProfileRelativePath = "profiles/&lt;Id&gt;" (tương đối).
/// </summary>
public sealed class TargetRegistrar
{
    public sealed record Slot(string Id, string DestProfile, JsonObject? Existing);

    private readonly bool _camel;
    private readonly bool _profilePathAbsolute;
    private readonly string _settingsPath;
    private readonly string _profilesDestBase;

    private JsonObject _root = null!;
    private JsonArray _instances = null!;

    public string SettingsPath => _settingsPath;
    public string ProfilesDestBase => _profilesDestBase;

    public TargetRegistrar(ExportApp app, string repoRoot, string shopeeStatDataDir)
    {
        if (app == ExportApp.ShopeeStat)
        {
            _camel = true;
            _profilePathAbsolute = true;
            _settingsPath = Path.Combine(shopeeStatDataDir, "settings.json");
            _profilesDestBase = Path.Combine(shopeeStatDataDir, "profiles");
        }
        else
        {
            _camel = false;
            _profilePathAbsolute = false;
            _settingsPath = Path.Combine(repoRoot, "open-multi-brave-v31", "launcher-settings.json");
            _profilesDestBase = Path.Combine(repoRoot, "open-multi-brave-v31", "persistent-data", "profiles");
        }
    }

    public bool TryLoad(out string? error)
    {
        error = null;
        if (!File.Exists(_settingsPath))
        {
            error = $"Chưa thấy settings của app đích:\n{_settingsPath}\n\nHãy mở app đó ít nhất 1 lần (để tạo settings) rồi thử lại.";
            return false;
        }
        try
        {
            _root = JsonNode.Parse(File.ReadAllText(_settingsPath)) as JsonObject
                    ?? throw new InvalidOperationException("settings không phải JSON object.");
            var key = FindKey(_root, "instances") ?? (_camel ? "instances" : "Instances");
            if (_root[key] is not JsonArray arr)
            {
                arr = new JsonArray();
                _root[key] = arr;
            }
            _instances = arr;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public void Backup()
    {
        try
        {
            var bak = _settingsPath + ".bak-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            if (!File.Exists(bak)) File.Copy(_settingsPath, bak);
        }
        catch { }
    }

    /// <summary>Xác định Id + nơi copy profile (tái dùng instance cũ nếu cùng login), CHƯA ghi gì.</summary>
    public Slot Resolve(string line)
    {
        JsonObject? existing = null;
        foreach (var n in _instances)
        {
            if (n is JsonObject o)
            {
                var lk = FindKey(o, "shopeeAccountLogin");
                if (lk is not null && string.Equals((string?)o[lk], line, StringComparison.Ordinal))
                {
                    existing = o;
                    break;
                }
            }
        }

        var id = existing is not null
            ? (string?)existing[FindKey(existing, "id") ?? "id"] ?? Guid.NewGuid().ToString("N")
            : Guid.NewGuid().ToString("N");

        return new Slot(id, Path.Combine(_profilesDestBase, id), existing);
    }

    /// <summary>Ghi/ cập nhật instance vào mảng (gọi sau khi copy profile thành công).</summary>
    public void Apply(Slot slot, string line, string username)
    {
        var node = slot.Existing ?? new JsonObject();
        var profilePath = _profilePathAbsolute ? slot.DestProfile : "profiles/" + slot.Id;

        if (_camel)
        {
            node["id"] = slot.Id;
            node["label"] = username;
            node["shopeeAccountLogin"] = line;
            node["profileRelativePath"] = profilePath;
            node["proxyType"] = "http";
            node["openWithShopeeAccount"] = false;
        }
        else
        {
            node["Id"] = slot.Id;
            node["Label"] = username;
            node["ShopeeAccountLogin"] = line;
            node["ProfileRelativePath"] = profilePath;
            node["ProxyType"] = "http";
            node["OpenWithShopeeAccount"] = false;
            node["RequireProxy"] = false; // cho phép mở dù chưa gán proxy (đã có cookie login sẵn)
        }

        if (slot.Existing is null) _instances.Add(node);
    }

    public void Save() =>
        File.WriteAllText(_settingsPath,
            _root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            Encoding.UTF8);

    private static string? FindKey(JsonObject o, string name)
    {
        foreach (var kv in o)
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                return kv.Key;
        return null;
    }
}
