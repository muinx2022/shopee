namespace OpenMultiBraveLauncherV3;

public sealed class AccountConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Email { get; set; } = "";
    public string WorkbookPath { get; set; } = "";
    public List<ShopConfig> Shops { get; set; } = [];

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Email) ? $"Account {Id[..Math.Min(8, Id.Length)]}" : Email.Trim();

    public static AccountConfig CreateDefault()
    {
        var account = new AccountConfig
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
