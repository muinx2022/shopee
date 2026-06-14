namespace ShopeeStatApp.Models;

public sealed class SearchTaskRecord
{
    public long Id { get; set; }
    public string Keyword { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string AccountName { get; set; } = "";
    public string RegionFilterText { get; set; } = "";
    public long MinPriceVnd { get; set; }
    public int MinMonthlySold { get; set; }
    public bool CheckVariantStock { get; set; }
    public string Status { get; set; } = "Running";
    public int ResumeCategoryIndex { get; set; } = 1;
    public string CurrentCategory { get; set; } = "";
    public int CurrentPage { get; set; }
    public int ProductCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string LastError { get; set; } = "";
}
