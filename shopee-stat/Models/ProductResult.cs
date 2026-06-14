namespace ShopeeStatApp.Models;

public sealed class ProductResult
{
    public long ItemId { get; set; }
    public long ShopId { get; set; }
    public string Name { get; set; } = "";
    public decimal PriceVnd { get; set; }
    public decimal PriceOriginalVnd { get; set; }
    public int MonthlySold { get; set; }
    public double Rating { get; set; }
    public int LikedCount { get; set; }
    public int CommentCount { get; set; }
    public string ShopLocation { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public string Category { get; set; } = "";
    public string ShopName { get; set; } = "";

    [JsonIgnore]
    public string Link => $"https://shopee.vn/product/{ShopId}/{ItemId}";
}
