using System.Security.Cryptography;
using System.Text;

namespace OpenMultiBraveLauncherV3;

internal static class UnpackedExtensionId
{
    /// <summary>ID 32 k� t? Chrome g�n cho thu m?c --load-extension (SHA-256 path ? a-p).</summary>
    public static string FromPath(string path)
    {
        var fullPath = Path.GetFullPath(path);

        byte[] pathBytes;
        if (OperatingSystem.IsWindows())
        {
            // Trên Windows, chuẩn hóa ký tự ổ đĩa thành chữ hoa
            if (fullPath.Length >= 2 && fullPath[1] == ':' && char.IsLower(fullPath[0]))
            {
                fullPath = char.ToUpper(fullPath[0]) + fullPath[1..];
            }
            // Sử dụng dấu gạch chéo ngược trên Windows
            fullPath = fullPath.Replace('/', '\\');
            
            // Trên Windows, FilePath là wchar_t (UTF-16 LE)
            pathBytes = Encoding.Unicode.GetBytes(fullPath);
        }
        else
        {
            // Trên Unix, FilePath là char (UTF-8) và sử dụng dấu gạch chéo xuôi
            fullPath = fullPath.Replace('\\', '/');
            pathBytes = Encoding.UTF8.GetBytes(fullPath);
        }

        var hash = SHA256.HashData(pathBytes);
        var chars = new char[32];
        for (var i = 0; i < 16; i++)
        {
            chars[i * 2] = (char)('a' + (hash[i] >> 4));
            chars[i * 2 + 1] = (char)('a' + (hash[i] & 0xF));
        }

        return new string(chars);
    }
}
