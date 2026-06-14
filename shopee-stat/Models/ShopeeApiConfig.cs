namespace ShopeeStatApp.Models;

public sealed class ShopeeApiConfig
{
    public SearchApiConfig SearchApi { get; set; } = new();
    public DetailApiConfig DetailApi { get; set; } = new();
    public ResponseFieldsConfig ResponseFields { get; set; } = new();
}

public sealed class SearchApiConfig
{
    public string Url { get; set; } = "https://shopee.vn/api/v4/search/search_items";
    public Dictionary<string, string> Params { get; set; } = new()
    {
        ["by"] = "sales",
        ["order"] = "desc",
        ["limit"] = "60",
        ["page_type"] = "search",
        ["scenario"] = "PAGE_GLOBAL_SEARCH",
        ["version"] = "2",
    };
    public string KeywordParam { get; set; } = "keyword";
    public string OffsetParam { get; set; } = "newest";
    public string CategoryParam { get; set; } = "match_id";
    public string LocationParam { get; set; } = "locations";
}

public sealed class DetailApiConfig
{
    public string Url { get; set; } = "https://shopee.vn/api/v4/item/get_item_detail_list";
    public string ItemListField { get; set; } = "item_list";
    public string ItemIdField { get; set; } = "itemid";
    public string ShopIdField { get; set; } = "shopid";
}

public sealed class ResponseFieldsConfig
{
    public string ItemsArray { get; set; } = "items";
    public string ItemBasic { get; set; } = "item_basic";
    public string ItemId { get; set; } = "itemid";
    public string ShopId { get; set; } = "shopid";
    public string Name { get; set; } = "name";
    public string Price { get; set; } = "price";
    public long PriceDivisor { get; set; } = 100000;
    public string PriceOriginal { get; set; } = "price_before_discount";
    public string MonthlySold { get; set; } = "sold";
    public string Rating { get; set; } = "rating_star";
    public string LikedCount { get; set; } = "liked_count";
    public string CommentCount { get; set; } = "cmt_count";
    public string ShopLocation { get; set; } = "shop_location";
    public string ImageHash { get; set; } = "image";
    public string ImageBaseUrl { get; set; } = "https://cf.shopee.vn/file/";
    public string FacetsArray { get; set; } = "facets";
    public string ModelsArray { get; set; } = "models";
    public string ModelStock { get; set; } = "stock";
}
