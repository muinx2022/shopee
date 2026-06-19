namespace ShopeeStatApp.Models;

public sealed class LauncherSettings
{
    public List<InstanceConfig> Instances { get; set; } = [];
    public List<string> Keywords { get; set; } = ["giày nữ"];
    public List<string> UsedKeywords { get; set; } = [];
    public string EdgePath { get; set; } = "";
    public string ExtensionPath { get; set; } = "";
    public int WsPort { get; set; } = 9111;
    public string OutputDirectory { get; set; } = "";

    /// <summary>Số lane (cửa sổ Edge + account) chạy song song khi bấm "Tự động".</summary>
    public int MaxParallelLanes { get; set; } = 6;

    /// <summary>Danh sách file .xlsx đã chọn lần trước ở tab "Tìm theo file" — để mở app nạp lại + resume.</summary>
    public List<string> LastFilePaths { get; set; } = [];

    /// <summary>Số Process (lane) lần trước ở tab "Tìm theo file" — giữ lại để không phải set lại mỗi lần chạy.</summary>
    public int LastFileProcessCount { get; set; } = 1;

    /// <summary>
    /// Id tài khoản được dùng sau cùng. Lượt chạy kế tiếp bắt đầu từ account NGAY SAU id này
    /// (xoay vòng), thay vì luôn chọn lại từ đầu danh sách — tránh dồn traffic lên vài account đầu
    /// và bị Shopee đánh dấu "traffic error" sau mỗi lần dừng/chạy lại.
    /// </summary>
    public string LastUsedAccountId { get; set; } = "";
}

