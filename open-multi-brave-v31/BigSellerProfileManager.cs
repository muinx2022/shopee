namespace OpenMultiBraveLauncherV3;

internal static class BigSellerProfileManager
{
    public static void EnsureWorkflowProfile(
        IEnumerable<BigSellerAccountConfig> accounts,
        BigSellerAccountConfig account,
        ShopConfig shop) =>
        EnsureAccountProfile(accounts, account);

    /// <summary>
    /// Một profile + một debug port DUY NHẤT cho cả account (mọi shop dùng chung).
    /// BigSeller chỉ một login/account; nhiều profile/shop = nhiều login = server xoay muc_token
    /// đá nhau (rotation war). Update và Import cũng gộp chung jar — chọn store đích bằng tên trong UI.
    /// </summary>
    public static string EnsureAccountProfile(
        IEnumerable<BigSellerAccountConfig> accounts,
        BigSellerAccountConfig account)
    {
        var profile = Path.Combine("bigseller-profiles", account.Id);

        // 1 port cho cả account: dùng lại port đã có ở bất kỳ shop nào (set trong cùng phiên),
        // chưa có thì cấp mới — tránh đụng port của account KHÁC.
        var port = account.Shops
            .SelectMany(s => new[] { s.BigSellerDebugPort, s.BigSellerImportDebugPort })
            .FirstOrDefault(p => p > 0);
        if (port <= 0)
            port = AllocateAccountDebugPort(accounts, account.Id);

        foreach (var s in account.Shops)
        {
            s.BigSellerProfileRelativePath = profile;
            s.BigSellerImportProfileRelativePath = profile;
            s.BigSellerDebugPort = port;
            s.BigSellerImportDebugPort = port;
        }

        Directory.CreateDirectory(Path.GetFullPath(AppSession.ResolvePersistentDataPath(profile)));
        return profile;
    }

    private static int AllocateAccountDebugPort(
        IEnumerable<BigSellerAccountConfig> accounts,
        string accountId)
    {
        var used = accounts
            .Where(a => a.Id != accountId)
            .SelectMany(a => a.Shops)
            .SelectMany(s => new[] { s.BigSellerDebugPort, s.BigSellerImportDebugPort })
            .Where(p => p > 0)
            .ToHashSet();

        for (var attempt = 0; attempt < 400; attempt++)
        {
            var port = PortAllocator.Shared.AllocateBigSellerPort();
            if (!used.Contains(port))
                return port;

            PortAllocator.Shared.Release(port);
        }

        throw new InvalidOperationException("Khong con port BigSeller trong.");
    }
}
