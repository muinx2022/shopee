using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace ShopeeStatApp.Services;

/// <summary>
/// Đọc file .docx danh mục Shopee (cây phân cấp theo thụt lề w:firstLine) và trả về danh sách
/// danh mục LÁ (cấp thấp nhất) kèm đường dẫn đầy đủ để AI phân loại không nhầm các mục "Khác".
/// </summary>
public static class ShopeeCategoryReference
{
    public sealed record LeafCategory(string Path, string Leaf);

    public static List<LeafCategory> LoadLeafCategories(string docxPath)
    {
        var xml = ReadDocumentXml(docxPath);

        // Mỗi đoạn <w:p>…</w:p> = 1 dòng; lấy mức thụt lề (w:firstLine, fallback w:left) + text.
        var nodes = new List<(int Indent, string Text)>();
        foreach (Match p in Regex.Matches(xml, "<w:p[ >].*?</w:p>", RegexOptions.Singleline))
        {
            var block = p.Value;
            var indent = 0;
            var mFirst = Regex.Match(block, "w:firstLine=\"(\\d+)\"");
            if (mFirst.Success) indent = int.Parse(mFirst.Groups[1].Value);
            else
            {
                var mLeft = Regex.Match(block, "w:left=\"(\\d+)\"");
                if (mLeft.Success) indent = int.Parse(mLeft.Groups[1].Value);
            }

            var sb = new StringBuilder();
            // <w:t> hoặc <w:t ...> — KHÔNG khớp <w:tab>/<w:tbl>… (phải là 'w:t' rồi khoảng trắng hoặc '>').
            foreach (Match t in Regex.Matches(block, "<w:t(?:\\s[^>]*)?>(.*?)</w:t>", RegexOptions.Singleline))
                sb.Append(System.Net.WebUtility.HtmlDecode(t.Groups[1].Value));
            var text = sb.ToString().Trim();
            if (text.Length > 0) nodes.Add((indent, text));
        }
        if (nodes.Count == 0) return [];

        // Quy đổi giá trị thụt lề → cấp 0,1,2… theo thứ tự tăng dần.
        var indents = nodes.Select(n => n.Indent).Distinct().OrderBy(x => x).ToList();
        int LevelOf(int indent) => indents.IndexOf(indent);

        // Dựng đường dẫn theo stack; node là LÁ nếu dòng kế tiếp không sâu hơn.
        var leaves = new List<LeafCategory>();
        var stack = new List<string>();
        for (var i = 0; i < nodes.Count; i++)
        {
            var lvl = LevelOf(nodes[i].Indent);
            while (stack.Count > lvl) stack.RemoveAt(stack.Count - 1);
            while (stack.Count < lvl) stack.Add(""); // phòng khi nhảy cấp
            stack.Add(nodes[i].Text);

            var isLeaf = i == nodes.Count - 1 || LevelOf(nodes[i + 1].Indent) <= lvl;
            if (isLeaf)
            {
                var path = string.Join(" > ", stack.Where(s => s.Length > 0));
                leaves.Add(new LeafCategory(path, nodes[i].Text));
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return leaves.Where(l => l.Path.Length > 0 && seen.Add(l.Path)).ToList();
    }

    // FileShare.ReadWrite để đọc được ngay cả khi Word đang mở file .docx.
    private static string ReadDocumentXml(string docxPath)
    {
        using var fs = new FileStream(docxPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        var entry = zip.GetEntry("word/document.xml")
            ?? throw new InvalidDataException("Không tìm thấy word/document.xml trong .docx");
        using var sr = new StreamReader(entry.Open(), Encoding.UTF8);
        return sr.ReadToEnd();
    }
}
