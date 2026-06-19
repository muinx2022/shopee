namespace ShopeeStatApp.Services;

public static class ExcelExporter
{
    private static readonly string[] Headers =
    [
        "link sp", "Giá gốc", "Giá bán", "SKU", "ID sản phẩm gốc",
        "Tên sp", "Tên sp đã sửa", "Danh mục", "Shop", "Rating",
        "Đã bán/tháng", "Lượt thích", "Số đánh giá", "Khu vực shop",
        "Ảnh", "Shop ID", "Item ID",
    ];

    public static string Export(IReadOnlyList<ProductResult> results, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var fileName = $"shopee-stat_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
        return Export(results, outputDir, fileName);
    }

    public static string Export(IReadOnlyList<ProductResult> results, string outputDir, string fileName)
    {
        Directory.CreateDirectory(outputDir);
        if (!fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            fileName += ".xlsx";
        var path = Path.Combine(outputDir, fileName);

        using var wb = new XLWorkbook();

        // Mỗi DANH MỤC = 1 sheet (không dồn chung 1 sheet). Sản phẩm không có danh mục → sheet "Khác".
        // Giữ thứ tự danh mục theo lần xuất hiện đầu tiên để khớp trình tự quét.
        var groups = results
            .GroupBy(p => string.IsNullOrWhiteSpace(p.Category) ? "Khác" : p.Category.Trim())
            .ToList();
        if (groups.Count == 0)
            WriteSheet(wb, "sheet data shop", []);
        else
        {
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in groups)
                WriteSheet(wb, UniqueSheetName(g.Key, usedNames), g.ToList());
        }

        var tmpPath = Path.Combine(outputDir,
            $"{Path.GetFileNameWithoutExtension(fileName)}_{Guid.NewGuid():N}.tmp.xlsx");
        wb.SaveAs(tmpPath);
        File.Move(tmpPath, path, overwrite: true);

        return path;
    }

    private static void WriteSheet(XLWorkbook wb, string sheetName, IReadOnlyList<ProductResult> results)
    {
        var ws = wb.AddWorksheet(sheetName);

        // Header row
        for (var c = 0; c < Headers.Length; c++)
        {
            var cell = ws.Cell(1, c + 1);
            cell.Value = Headers[c];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#4472C4");
            cell.Style.Font.FontColor = XLColor.White;
        }

        // Data rows
        for (var r = 0; r < results.Count; r++)
        {
            var row = r + 2;
            var p = results[r];
            ws.Cell(row, 1).Value = p.Link;
            ws.Cell(row, 2).Value = (double)p.PriceOriginalVnd;
            ws.Cell(row, 3).Value = (double)p.PriceVnd;
            ws.Cell(row, 4).Value = "";           // SKU — filled later
            ws.Cell(row, 5).Value = p.ItemId.ToString();
            ws.Cell(row, 6).Value = p.Name;
            ws.Cell(row, 7).Value = "";           // Tên sp đã sửa — filled later
            ws.Cell(row, 8).Value = p.Category;
            ws.Cell(row, 9).Value = p.ShopName;
            ws.Cell(row, 10).Value = p.Rating;
            ws.Cell(row, 11).Value = p.MonthlySold;
            ws.Cell(row, 12).Value = p.LikedCount;
            ws.Cell(row, 13).Value = p.CommentCount;
            ws.Cell(row, 14).Value = p.ShopLocation;
            ws.Cell(row, 15).Value = p.ImageUrl;
            ws.Cell(row, 16).Value = p.ShopId.ToString();
            ws.Cell(row, 17).Value = p.ItemId.ToString();
        }

        ws.Columns().AdjustToContents(1, 50);
        ws.Column(1).Width = 45;   // link
        ws.Column(6).Width = 60;   // Tên sp
        ws.Column(15).Width = 60;  // Ảnh

        // Freeze header row
        ws.SheetView.FreezeRows(1);
    }

    // Excel: tên sheet ≤ 31 ký tự, không chứa : \ / ? * [ ], không trùng. Trả về tên hợp lệ + duy nhất.
    private static string UniqueSheetName(string raw, HashSet<string> used)
    {
        var clean = new string((raw ?? "").Select(ch => ":\\/?*[]".Contains(ch) ? ' ' : ch).ToArray()).Trim();
        if (clean.Length == 0) clean = "Khác";
        if (clean.Length > 31) clean = clean[..31];

        var name = clean;
        var n = 2;
        while (!used.Add(name))
        {
            var suffix = $" ({n++})";
            var head = clean.Length + suffix.Length > 31 ? clean[..(31 - suffix.Length)] : clean;
            name = head + suffix;
        }
        return name;
    }
}
