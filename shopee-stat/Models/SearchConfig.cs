namespace ShopeeStatApp.Models;

public sealed class SearchConfig
{
    public string Keyword { get; set; } = "";
    public string RegionFilterText { get; set; } = "";
    public long MinPriceVnd { get; set; } = 100_000;
    public int MinMonthlySold { get; set; } = 50;
    public bool CheckVariantStock { get; set; } = true;
    public int ResumeCategoryIndex { get; set; } = 1;

    /// <summary>1-based page to resume at WITHIN the resumed category (account swap → continue
    /// where the failed attempt stopped instead of restarting that category at page 1).</summary>
    public int ResumePage { get; set; } = 1;

    /// <summary>"keyword" (search by keyword) or "shopFromLink" (open a product link → its shop → all products).</summary>
    public string Mode { get; set; } = "keyword";

    /// <summary>Product URL to open when Mode == "shopFromLink".</summary>
    public string ProductLink { get; set; } = "";

    /// <summary>
    /// When true, results below MinPriceVnd are dropped in the app (used by shop-from-link mode,
    /// where there is no Shopee price-range filter UI to enforce it).
    /// </summary>
    public bool FilterPriceClientSide { get; set; }
}
