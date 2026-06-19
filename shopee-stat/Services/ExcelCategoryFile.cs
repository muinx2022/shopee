using ClosedXML.Excel;

namespace ShopeeStatApp.Services;

/// <summary>
/// Mở 1 file .xlsx (định dạng xuất của tool), tìm cột "Tên sp" + "Danh mục" ở mọi sheet, gom tên
/// sản phẩm để AI phân loại, rồi ghi danh mục trở lại đúng ô và lưu lại CHÍNH file đó. Không đụng CSDL.
/// </summary>
public sealed class ExcelCategoryFile : IDisposable
{
    private readonly XLWorkbook _wb;
    private readonly List<IXLCell> _catCells = [];

    /// <summary>Tên sản phẩm theo từng dòng (cùng thứ tự với ô danh mục sẽ ghi).</summary>
    public List<string> Names { get; } = [];

    public ExcelCategoryFile(string path)
    {
        _wb = new XLWorkbook(path); // nạp toàn bộ vào bộ nhớ; Save() sẽ ghi lại đúng file này

        foreach (var ws in _wb.Worksheets)
        {
            var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
            if (lastCol == 0) continue;

            int nameCol = 0, catCol = 0;
            for (var c = 1; c <= lastCol; c++)
            {
                var h = (ws.Cell(1, c).GetString() ?? "").Trim().ToLowerInvariant();
                if (nameCol == 0 && (h.Contains("tên sp") || h.Contains("tên sản phẩm") || h == "tên")) nameCol = c;
                if (catCol == 0 && (h.Contains("danh mục") || h.Contains("danh muc"))) catCol = c;
            }
            if (nameCol == 0) continue;                 // sheet không có cột tên → bỏ qua
            if (catCol == 0)                            // chưa có cột danh mục → thêm cột mới ở cuối
            {
                catCol = lastCol + 1;
                ws.Cell(1, catCol).Value = "Danh mục";
            }

            var lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;
            for (var r = 2; r <= lastRow; r++)
            {
                var name = (ws.Cell(r, nameCol).GetString() ?? "").Trim();
                if (name.Length == 0) continue;
                Names.Add(name);
                _catCells.Add(ws.Cell(r, catCol));
            }
        }
    }

    /// <summary>Ghi danh mục (cùng thứ tự với <see cref="Names"/>) vào ô tương ứng rồi lưu lại file.</summary>
    public int ApplyAndSave(IReadOnlyList<string> categories)
    {
        var written = 0;
        for (var i = 0; i < _catCells.Count && i < categories.Count; i++)
        {
            if (string.IsNullOrEmpty(categories[i])) continue;
            _catCells[i].Value = categories[i];
            written++;
        }
        _wb.Save();
        return written;
    }

    public void Dispose() => _wb.Dispose();
}
