namespace OpenMultiBraveLauncherV3;

internal static class ShopeeLoginAutomation
{
    public static bool TryParseLoginLine(
        string raw,
        out ShopeeAccountLogin login,
        out string error)
    {
        login = default;
        error = "";
        var parts = (raw ?? "").Split('|', StringSplitOptions.TrimEntries);
        if (parts.Length < 3)
        {
            error = "Dinh dang Shopee account phai la username|password|.shopee.vn=SPC_F=...";
            return false;
        }

        var username = parts[0].Trim();
        var password = parts[1].Trim();
        var cookiePart = string.Join('|', parts.Skip(2)).Trim();
        if (string.IsNullOrWhiteSpace(username) ||
            string.IsNullOrWhiteSpace(password) ||
            string.IsNullOrWhiteSpace(cookiePart))
        {
            error = "Username, password, SPC_F khong duoc de trong.";
            return false;
        }

        var eq = cookiePart.IndexOf('=', StringComparison.Ordinal);
        if (eq <= 0)
        {
            error = "Cookie SPC_F khong hop le.";
            return false;
        }

        var domain = cookiePart[..eq].Trim();
        var valuePart = cookiePart[(eq + 1)..].Trim();
        const string prefix = "SPC_F=";
        if (!valuePart.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            error = "Cookie phai co dang .shopee.vn=SPC_F=...";
            return false;
        }

        var cookieValue = valuePart[prefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(cookieValue))
        {
            error = "Domain hoac gia tri SPC_F khong hop le.";
            return false;
        }

        login = new ShopeeAccountLogin(username, password, domain, cookieValue);
        return true;
    }
}

internal readonly record struct ShopeeAccountLogin(
    string Username,
    string Password,
    string CookieDomain,
    string SpcF);
