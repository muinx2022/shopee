namespace UpdateProduct;

internal static class BigSellerContextFactory
{
    public static BigSellerAccountConfig ActiveAccount(UpdateProductSettingsFile settings) =>
        settings.Accounts.FirstOrDefault(a => a.Id == settings.ActiveAccountId) ?? settings.Accounts.First();

    public static ShopConfig ActiveShop(UpdateProductSettingsFile settings)
    {
        var account = ActiveAccount(settings);
        return account.Shops.FirstOrDefault(s => s.Id == settings.ActiveShopId) ?? account.Shops.First();
    }

    public static BigSellerWorkflowSettings Build(UpdateProductSettingsFile settings)
    {
        UpdateProductSettings.Normalize(settings);
        var account = ActiveAccount(settings);
        var shop = ActiveShop(settings);
        BigSellerProfileManager.EnsureWorkflowProfile(settings.Accounts, account, shop);

        var pythonDir = Path.Combine(AppSession.RepoRootDirectory, "update-product-python");
        var pythonExe = Path.Combine(pythonDir, ".venv", "Scripts", "python.exe");
        if (!File.Exists(pythonExe))
            pythonExe = "python";

        var defaultKeyFile = Path.Combine(AppSession.RepoRootDirectory, "openai.key");

        return new BigSellerWorkflowSettings
        {
            BravePath = settings.BraveExe.Trim(),
            ProfileDir = Path.GetFullPath(AppSession.ResolvePersistentDataPath(shop.BigSellerProfileRelativePath)),
            DebugPort = shop.BigSellerDebugPort,
            ImportProfileDir = Path.GetFullPath(AppSession.ResolvePersistentDataPath(shop.BigSellerImportProfileRelativePath)),
            ImportDebugPort = shop.BigSellerImportDebugPort,
            ShopName = shop.DisplayName,
            WorkbookPath = account.WorkbookPath,
            DataSheet = shop.ShopeeDataSheet,
            BigSellerCookieFile = account.BigSellerCookieFile,
            StartRow = Math.Max(2, shop.BigSellerStartRow),
            EndRow = Math.Max(0, shop.BigSellerEndRow),
            PythonDir = pythonDir,
            PythonExe = pythonExe,
            ImagePath = string.IsNullOrWhiteSpace(shop.BigSellerImagePath) ? @"D:\images\1.jpeg" : shop.BigSellerImagePath,
            VideoFolder = string.IsNullOrWhiteSpace(shop.BigSellerVideoFolder) ? @"D:\videos" : shop.BigSellerVideoFolder,
            CrawlUrl = string.IsNullOrWhiteSpace(shop.BigSellerCrawlUrl) ? BigSellerCrawlHelper.CrawlUrl : shop.BigSellerCrawlUrl,
            ImportFromClaimedTab = shop.BigSellerImportFromClaimedTab,
            ImportMaxProcess = Math.Clamp(shop.BigSellerImportMaxProcess, 1, 10),
            UpdateMaxProcess = Math.Clamp(shop.BigSellerUpdateMaxProcess, 1, 10),
            ListingReloadSeconds = Math.Clamp(shop.BigSellerListingReloadSeconds, 3, 600),
            OpenAiModel = string.IsNullOrWhiteSpace(shop.OpenAiModel) ? "gpt-4.1-mini" : shop.OpenAiModel,
            OpenAiApiKeyFile = string.IsNullOrWhiteSpace(settings.OpenAiApiKeyFile) ? defaultKeyFile : settings.OpenAiApiKeyFile,
            OpenAiBatchSize = Math.Clamp(shop.OpenAiBatchSize, 1, 500),
        };
    }
}
