namespace UpdateProduct;

internal sealed class BigSellerWorkflowSettings
{
    public string BravePath { get; init; } = "";
    public string ProfileDir { get; init; } = "";
    public int DebugPort { get; init; }
    public string ImportProfileDir { get; init; } = "";
    public int ImportDebugPort { get; init; }
    public string ShopName { get; init; } = "";
    public string WorkbookPath { get; init; } = "";
    public string DataSheet { get; init; } = "";
    public string BigSellerCookieFile { get; init; } = "";
    public string BatchId { get; init; } = "";
    public int StartRow { get; init; } = 2;
    public int EndRow { get; init; }
    public string PythonDir { get; init; } = "";
    public string PythonExe { get; init; } = "";
    public string ImagePath { get; init; } = "";
    public string VideoFolder { get; init; } = "";
    public string CrawlUrl { get; init; } = "";
    public bool ImportFromClaimedTab { get; init; }
    public int ImportMaxProcess { get; init; } = 1;
    public int UpdateMaxProcess { get; init; } = 1;
    public int ListingReloadSeconds { get; init; } = 20;
    public string OpenAiModel { get; init; } = "gpt-4.1-mini";
    public string OpenAiApiKeyFile { get; init; } = "";
    public int OpenAiBatchSize { get; init; } = 40;
}
