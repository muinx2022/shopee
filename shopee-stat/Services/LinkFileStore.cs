namespace ShopeeStatApp.Services;

/// <summary>
/// Reads product links from an .xlsx file and tracks per-row processing status in a
/// trailing "status" column, so a file run can be resumed and marked as it progresses.
/// </summary>
public sealed class LinkFileStore
{
    public const string Processing = "processing";
    public const string Processed = "Processed";

    public sealed record LinkRow(int RowNumber, string Link, string Status)
    {
        public bool IsDone => string.Equals(Status, Processed, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly Regex LinkRx = new(@"-i\.\d+\.\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string Path { get; }
    public int StatusColumn { get; private set; }
    public string SheetName { get; private set; } = "";

    public LinkFileStore(string path) => Path = path;

    /// <summary>Loads all rows that contain a Shopee product link, plus their current status.</summary>
    public List<LinkRow> Load()
    {
        using var wb = new XLWorkbook(Path);
        var ws = wb.Worksheets.First();
        SheetName = ws.Name;

        var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
        var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
        if (lastRow == 0 || lastCol == 0)
        {
            StatusColumn = 1;
            return [];
        }

        StatusColumn = ResolveStatusColumn(ws, lastRow, lastCol);

        var rows = new List<LinkRow>();
        for (var r = 1; r <= lastRow; r++)
        {
            var link = FindLinkInRow(ws, r, lastCol);
            if (string.IsNullOrWhiteSpace(link)) continue;
            var status = ws.Cell(r, StatusColumn).GetString().Trim();
            rows.Add(new LinkRow(r, link, status));
        }
        return rows;
    }

    /// <summary>Clears every status cell in the status column (reset resume state).</summary>
    public void ClearAllStatuses()
    {
        using var wb = new XLWorkbook(Path);
        var ws = wb.Worksheets.First();
        var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
        var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
        if (lastRow == 0 || lastCol == 0) return;

        var statusCol = ResolveStatusColumn(ws, lastRow, lastCol);
        for (var r = 1; r <= lastRow; r++)
            ws.Cell(r, statusCol).Value = "";
        wb.Save();
    }

    /// <summary>Writes a status value into a row's status column and saves the workbook.</summary>
    public void MarkStatus(int rowNumber, string status)
    {
        using var wb = new XLWorkbook(Path);
        var ws = wb.Worksheets.First();
        var col = StatusColumn > 0 ? StatusColumn : (ws.LastColumnUsed()?.ColumnNumber() ?? 1) + 1;
        ws.Cell(rowNumber, col).Value = status;
        wb.Save();
    }

    private static int ResolveStatusColumn(IXLWorksheet ws, int lastRow, int lastCol)
    {
        // Reuse the last column if it already holds only status-like values (a re-run),
        // otherwise put the status in a fresh column right after the data.
        return IsStatusColumn(ws, lastCol, lastRow) ? lastCol : lastCol + 1;
    }

    private static bool IsStatusColumn(IXLWorksheet ws, int col, int lastRow)
    {
        var sawAny = false;
        for (var r = 1; r <= lastRow; r++)
        {
            var v = ws.Cell(r, col).GetString().Trim();
            if (v.Length == 0) continue;
            sawAny = true;
            var isStatus = v.Equals(Processing, StringComparison.OrdinalIgnoreCase)
                || v.Equals(Processed, StringComparison.OrdinalIgnoreCase)
                || v.StartsWith("Trạng thái", StringComparison.OrdinalIgnoreCase)
                || v.StartsWith("Lỗi", StringComparison.OrdinalIgnoreCase);
            if (!isStatus) return false;
        }
        return sawAny;
    }

    private static string FindLinkInRow(IXLWorksheet ws, int row, int lastCol)
    {
        for (var c = 1; c <= lastCol; c++)
        {
            var v = ws.Cell(row, c).GetString().Trim();
            if (v.Length == 0) continue;
            if (LinkRx.IsMatch(v) || v.Contains("shopee.vn/", StringComparison.OrdinalIgnoreCase))
                return v;
        }
        return "";
    }
}
