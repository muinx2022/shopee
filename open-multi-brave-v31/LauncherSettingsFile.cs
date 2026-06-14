namespace OpenMultiBraveLauncherV3;

public sealed class LauncherSettingsFile
{
    public string BraveExe { get; set; } = "";
    public string SourceUserData { get; set; } = "";
    /// <summary>Số profile chạy runner đồng thời tối đa (chạy lượt).</summary>
    public int MaxConcurrentProfiles { get; set; } = 3;
    /// <summary>Bật: chỉ chạy lượt tự động, không chạy thủ công từng profile.</summary>
    public bool AutoRunEnabled { get; set; }
    public string RangeSheetName { get; set; } = "";
    public int RangeStartRow { get; set; } = 2;
    public int RangeRowsPerProfile { get; set; } = 30;
    /// <summary>Sheet dùng chung khi dàn auto cho toàn bộ profile.</summary>
    public string AutoSheetName { get; set; } = "";
    /// <summary>Auto ch?y t? instance th? m?y trong danh s�ch (1-based).</summary>
    public int AutoRunFromInstance { get; set; } = 1;
    /// <summary>Auto ch?y d?n instance th? m?y (0 = d?n h?t danh s�ch).</summary>
    public int AutoRunToInstance { get; set; }
    /// <summary>Dòng bắt đầu để tự dàn range cho toàn bộ profile.</summary>
    public int AutoStartRow { get; set; } = 2;
    /// <summary>Số cộng vào start row để ra to row cho mỗi profile.</summary>
    public int AutoRowsPerProfile { get; set; } = 30;
    public List<BigSellerAccountConfig> Accounts { get; set; } = [];
    public string ActiveAccountId { get; set; } = "";
    public string ActiveShopId { get; set; } = "";
    public List<InstanceConfig> Instances { get; set; } = [];
}
