using System.Text.Json.Serialization;

namespace OpenMultiBraveLauncherV3;

public sealed class InstanceConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string AccountId { get; set; } = "";
    public string ShopId { get; set; } = "";
    public string Label { get; set; } = "";
    public string KiotProxyKey { get; set; } = "";
    public string Region { get; set; } = "random";
    public string ProxyType { get; set; } = "http";

    /// <summary>Proxy thủ công (host:port hoặc http://…) — dùng khi không có KiotProxy key.</summary>
    public string ManualProxy { get; set; } = "";
    public string ProfileRelativePath { get; set; } = "";
    public bool CreateNewProfileOnNextStart { get; set; }
    public bool ExportShopee { get; set; } = true;
    public bool ExportBigSeller { get; set; } = true;
    public string BigSellerCookieFile { get; set; } = "";
    public string ShopeeAccountLogin { get; set; } = "";
    public bool OpenWithShopeeAccount { get; set; }

    /// <summary>Tự đóng profile (tắt Brave) khi chạy xong hết link.</summary>
    public bool AutoCloseProfileOnFinish { get; set; } = true;

    /// <summary>Sheet data.xlsx — nhập trên launcher (nguồn đúng).</summary>
    public string DataSheet { get; set; } = "";

    /// <summary>Từ dòng — nhập trên launcher (nguồn đúng).</summary>
    public int? StartRow { get; set; }

    /// <summary>Đến dòng — nhập trên launcher (nguồn đúng).</summary>
    public int? EndRow { get; set; }

    /// <summary>Dòng sẽ chạy khi bấm Chạy / auto (sửa được; gợi ý từ tiến độ).</summary>
    public int? NextRunRow { get; set; }

    /// <summary>Dòng hoàn thành gần nhất (từ extension).</summary>
    public int? LastCompletedRow { get; set; }

    /// <summary>Dòng đang xử lý / dừng giữa chừng (từ extension).</summary>
    public int? CurrentRow { get; set; }

    public string? LastSku { get; set; }
    public string? RunnerPhase { get; set; }
    public bool? RunnerRunning { get; set; }
    public string? LastRunnerMessage { get; set; }
    public List<RunnerLogEntry> RunLog { get; set; } = [];
    public DateTimeOffset? ProgressSyncedAt { get; set; }

    [JsonIgnore]
    public string DisplayName =>
        string.IsNullOrWhiteSpace(Label) ? $"Instance {Id[..Math.Min(8, Id.Length)]}" : Label.Trim();

    public void EnsureProfileRelativePath()
    {
        if (string.IsNullOrWhiteSpace(ProfileRelativePath))
            ProfileRelativePath = Path.Combine("profiles", Id);
    }

    public static InstanceConfig CreateNew(int index)
    {
        var id = Guid.NewGuid().ToString("N");
        return new InstanceConfig
        {
            Id = id,
            Label = $"Instance {index}",
            ProfileRelativePath = Path.Combine("profiles", id),
        };
    }

    /// <summary>Chỉ cập nhật tiến độ chạy — không ghi đè sheet / từ dòng / đến dòng trên form.</summary>
    public void ApplyExtensionProgress(ExtensionRunnerState state)
    {
        if (string.IsNullOrWhiteSpace(DataSheet) && !string.IsNullOrWhiteSpace(state.SheetName))
            DataSheet = state.SheetName.Trim();

        if (StartRow is null or < 1 && state.StartRow is > 0)
            StartRow = state.StartRow;

        if (EndRow is null or < 1 && state.EndRow is > 0)
            EndRow = state.EndRow;

        if (state.LastCompletedRow is > 0)
            LastCompletedRow = state.LastCompletedRow;

        if (state.CurrentRow is > 0)
            CurrentRow = state.CurrentRow;

        if (!string.IsNullOrWhiteSpace(state.LastSku))
            LastSku = state.LastSku.Trim();

        if (!string.IsNullOrWhiteSpace(state.Phase))
            RunnerPhase = state.Phase.Trim();

        if (state.Running is not null)
            RunnerRunning = state.Running;

        if (!string.IsNullOrWhiteSpace(state.LastMessage))
            LastRunnerMessage = state.LastMessage.Trim();

        ProgressSyncedAt = DateTimeOffset.Now;
    }

    [JsonIgnore]
    public bool IsInterruptedMidRun => new ExtensionRunnerState
    {
        SheetName = DataSheet,
        StartRow = StartRow,
        EndRow = EndRow,
        LastCompletedRow = LastCompletedRow,
        CurrentRow = CurrentRow,
        Phase = RunnerPhase,
        Running = RunnerRunning,
    }.IsInterruptedMidRun();

    [JsonIgnore]
    public int? StoppedAtRow => new ExtensionRunnerState
    {
        LastCompletedRow = LastCompletedRow,
        CurrentRow = CurrentRow,
    }.StoppedAtRow;

    [JsonIgnore]
    public string ProgressSummary
    {
        get
        {
            if (StartRow is null &&
                EndRow is null &&
                LastCompletedRow is null)
                return "";

            var from = StartRow?.ToString() ?? "?";
            var to = EndRow?.ToString() ?? "h\u1ebft";
            var next = GetEffectiveRunRow()?.ToString() ?? "-";
            return $"{from}-{to} (ti\u1ebfp {next})";
        }
    }

    /// <summary>Gợi ý dòng tiếp theo từ tiến độ extension.</summary>
    public int? ComputeSuggestedNextRow()
    {
        if (LastCompletedRow is > 0)
            return LastCompletedRow.Value + 1;
        if (CurrentRow is > 0)
            return CurrentRow.Value + 1;
        return StartRow;
    }

    public int? GetEffectiveRunRow()
    {
        var suggested = ComputeSuggestedNextRow();
        if (suggested is > 0 && !IsRowInsideConfiguredRange(suggested.Value))
            suggested = StartRow;

        if (NextRunRow is > 0)
        {
            if (!IsRowInsideConfiguredRange(NextRunRow.Value))
                return suggested;
            return NextRunRow;
        }
        return suggested;
    }

    private bool IsRowInsideConfiguredRange(int row)
    {
        if (StartRow is > 0 && row < StartRow.Value)
            return false;
        if (EndRow is > 0 && row > EndRow.Value)
            return false;
        return true;
    }

    public bool TryValidateRunRow(int row, out string? error)
    {
        if (row < 2)
        {
            error = "Dòng chạy phải ≥ 2.";
            return false;
        }

        if (StartRow is > 0 && row < StartRow.Value)
        {
            error = $"Dòng {row} nhỏ hơn \"Từ dòng\" ({StartRow}).";
            return false;
        }

        if (EndRow is > 0 && row > EndRow.Value)
        {
            error = $"Dòng {row} lớn hơn \"Đến dòng\" ({EndRow}).";
            return false;
        }

        error = null;
        return true;
    }

    [JsonIgnore]
    public int? SuggestedResumeRow => ComputeSuggestedNextRow();
}
