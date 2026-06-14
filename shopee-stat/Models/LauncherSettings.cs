namespace ShopeeStatApp.Models;

public sealed class LauncherSettings
{
    public List<InstanceConfig> Instances { get; set; } = [];
    public List<string> Keywords { get; set; } = ["giày nữ"];
    public List<string> UsedKeywords { get; set; } = [];
    public string BravePath { get; set; } = "";
    public int WsPort { get; set; } = 9111;
    public string OutputDirectory { get; set; } = "";

    /// <summary>Số lane (cửa sổ Brave + account) chạy song song khi bấm "Tự động".</summary>
    public int MaxParallelLanes { get; set; } = 6;

    /// <summary>Danh sách file .xlsx đã chọn lần trước ở tab "Tìm theo file" — để mở app nạp lại + resume.</summary>
    public List<string> LastFilePaths { get; set; } = [];
}
