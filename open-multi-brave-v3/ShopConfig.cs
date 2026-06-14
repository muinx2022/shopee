namespace OpenMultiBraveLauncherV3;

public sealed class BigSellerAccountConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Email { get; set; } = "";
    public string WorkbookPath { get; set; } = "";
    public string BigSellerCookieFile { get; set; } = "";
    public List<ShopConfig> Shops { get; set; } = [];

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Email) ? $"Account {Id[..Math.Min(8, Id.Length)]}" : Email.Trim();

    public static BigSellerAccountConfig CreateDefault()
    {
        var account = new BigSellerAccountConfig
        {
            Email = "abc@test.com",
        };
        account.Shops.Add(ShopConfig.CreateDefault());
        return account;
    }
}

public sealed class ShopConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string ShopeeDataSheet { get; set; } = "";
    public bool UseSharedProfiles { get; set; } = true;
    public string BigSellerImagePath { get; set; } = @"D:\images\1.jpeg";
    public string BigSellerVideoFolder { get; set; } = @"D:\videos";
    public string BigSellerCrawlUrl { get; set; } = "";
    public bool BigSellerImportFromClaimedTab { get; set; }
    public int BigSellerListingReloadSeconds { get; set; } = 20;
    public string BigSellerProfileRelativePath { get; set; } = "";
    public int BigSellerDebugPort { get; set; }
    /// <summary>Brave profile riêng cho tab Import to store (tách khỏi Update product).</summary>
    public string BigSellerImportProfileRelativePath { get; set; } = "";
    public int BigSellerImportDebugPort { get; set; }
    public int BigSellerStartRow { get; set; } = 2;
    public int BigSellerEndRow { get; set; }
    public string OpenAiModel { get; set; } = "gpt-4.1-mini";
    public string OpenAiApiKeyFile { get; set; } = "";
    public int OpenAiBatchSize { get; set; } = 40;

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Name) ? $"Shop {Id[..Math.Min(8, Id.Length)]}" : Name.Trim();

    public static ShopConfig CreateDefault() => new()
    {
        Name = "Minoa Store",
    };
}
