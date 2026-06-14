namespace ShopeeStatApp.Models;

public sealed class InstanceConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "";

    /// <summary>username|password|.shopee.vn=SPC_F=value</summary>
    public string ShopeeAccountLogin { get; set; } = "";

    public string KiotProxyKey { get; set; } = "";
    public string ManualProxy { get; set; } = "";
    public string ProxyType { get; set; } = "http";
    public string ProfileRelativePath { get; set; } = "";
    public bool CreateNewProfileOnNextStart { get; set; }

    /// <summary>
    /// Lần đầu dùng tài khoản (hoặc proxy lỗi khi login) → cần đăng nhập Shopee trước khi search.
    /// Bị xóa sau khi đăng nhập thành công.
    /// </summary>
    public bool OpenWithShopeeAccount { get; set; }

    [JsonIgnore]
    public string Username =>
        ShopeeAccountLogin.Contains('|')
            ? ShopeeAccountLogin.Split('|', 2)[0].Trim()
            : ShopeeAccountLogin.Trim();

    [JsonIgnore]
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Label) ? Label :
        !string.IsNullOrWhiteSpace(Username) ? Username :
        $"Instance {Id[..Math.Min(8, Id.Length)]}";

    [JsonIgnore]
    public string ProxySummary =>
        !string.IsNullOrWhiteSpace(KiotProxyKey) ? $"kiot:{KiotProxyKey[..Math.Min(12, KiotProxyKey.Length)]}…" :
        !string.IsNullOrWhiteSpace(ManualProxy) ? ManualProxy :
        "(no proxy)";

    public void EnsureProfileRelativePath()
    {
        if (string.IsNullOrWhiteSpace(ProfileRelativePath))
            ProfileRelativePath = Path.Combine("profiles", Id);
    }
}
