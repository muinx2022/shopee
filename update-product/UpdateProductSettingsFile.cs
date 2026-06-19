namespace UpdateProduct;

public sealed class UpdateProductSettingsFile
{
    public string BraveExe { get; set; } = "";
    public string SourceUserData { get; set; } = "";
    public string OpenAiApiKeyFile { get; set; } = "";
    public List<BigSellerAccountConfig> Accounts { get; set; } = [];
    public string ActiveAccountId { get; set; } = "";
    public string ActiveShopId { get; set; } = "";
}
