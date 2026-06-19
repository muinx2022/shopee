using Microsoft.Data.Sqlite;

namespace ShopeeStatApp.Services;

public sealed class SearchTaskStore
{
    private readonly string _dbPath;
    private readonly object _sync = new();

    public SearchTaskStore()
    {
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ShopeeStatApp");
        Directory.CreateDirectory(dataDir);
        _dbPath = Path.Combine(dataDir, "tasks.db");
        Initialize();
    }

    public long CreateTask(SearchConfig config, InstanceConfig account)
    {
        lock (_sync)
        {
            using var con = Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = """
                INSERT INTO search_tasks
                (keyword, account_id, account_name, region_filter, min_price, min_sold, check_stock,
                 status, resume_category_index, current_category, current_page, created_at, updated_at, last_error)
                VALUES ($keyword, $accountId, $accountName, $region, $minPrice, $minSold, $checkStock,
                 'Running', 1, '', 0, $now, $now, '');
                SELECT last_insert_rowid();
                """;
            var now = DateTime.Now.ToString("O");
            Add(cmd, "$keyword", config.Keyword);
            Add(cmd, "$accountId", account.Id);
            Add(cmd, "$accountName", account.DisplayName);
            Add(cmd, "$region", config.RegionFilterText);
            Add(cmd, "$minPrice", config.MinPriceVnd);
            Add(cmd, "$minSold", config.MinMonthlySold);
            Add(cmd, "$checkStock", config.CheckVariantStock ? 1 : 0);
            Add(cmd, "$now", now);
            return (long)(cmd.ExecuteScalar() ?? 0L);
        }
    }

    public void ResetTask(long taskId, SearchConfig config, InstanceConfig account)
    {
        lock (_sync)
        {
            using var con = Open();
            using var tx = con.BeginTransaction();
            using (var del = con.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM task_products WHERE task_id = $id";
                Add(del, "$id", taskId);
                del.ExecuteNonQuery();
            }

            using (var cmd = con.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    UPDATE search_tasks
                    SET keyword=$keyword, account_id=$accountId, account_name=$accountName,
                        region_filter=$region, min_price=$minPrice, min_sold=$minSold, check_stock=$checkStock,
                        status='Running', resume_category_index=1, current_category='', current_page=0,
                        product_count=0, updated_at=$now, last_error=''
                    WHERE id=$id
                    """;
                Add(cmd, "$id", taskId);
                Add(cmd, "$keyword", config.Keyword);
                Add(cmd, "$accountId", account.Id);
                Add(cmd, "$accountName", account.DisplayName);
                Add(cmd, "$region", config.RegionFilterText);
                Add(cmd, "$minPrice", config.MinPriceVnd);
                Add(cmd, "$minSold", config.MinMonthlySold);
                Add(cmd, "$checkStock", config.CheckVariantStock ? 1 : 0);
                Add(cmd, "$now", DateTime.Now.ToString("O"));
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    public void SaveProduct(long taskId, ProductResult product)
    {
        if (taskId <= 0 || product.ItemId <= 0 || product.ShopId <= 0) return;

        lock (_sync)
        {
            using var con = Open();
            using var tx = con.BeginTransaction();
            using (var cmd = con.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO task_products
                    (task_id, item_id, shop_id, name, price_vnd, original_price_vnd, monthly_sold, rating,
                     liked_count, comment_count, shop_location, image_url, category, shop_name, updated_at)
                    VALUES ($taskId, $itemId, $shopId, $name, $price, $originalPrice, $sold, $rating,
                     $liked, $comments, $location, $image, $category, $shopName, $now)
                    ON CONFLICT(task_id, item_id, shop_id) DO UPDATE SET
                        name=excluded.name,
                        price_vnd=excluded.price_vnd,
                        original_price_vnd=excluded.original_price_vnd,
                        monthly_sold=excluded.monthly_sold,
                        rating=excluded.rating,
                        liked_count=excluded.liked_count,
                        comment_count=excluded.comment_count,
                        shop_location=excluded.shop_location,
                        image_url=excluded.image_url,
                        category=excluded.category,
                        shop_name=excluded.shop_name,
                        updated_at=excluded.updated_at
                    """;
                AddProductParams(cmd, taskId, product);
                cmd.ExecuteNonQuery();
            }

            using (var count = con.CreateCommand())
            {
                count.Transaction = tx;
                count.CommandText = """
                    UPDATE search_tasks
                    SET product_count=(SELECT COUNT(*) FROM task_products WHERE task_id=$taskId),
                        updated_at=$now
                    WHERE id=$taskId
                    """;
                Add(count, "$taskId", taskId);
                Add(count, "$now", DateTime.Now.ToString("O"));
                count.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    public void UpdateCheckpoint(long taskId, int categoryIndex, string categoryName, int page)
    {
        if (taskId <= 0) return;
        lock (_sync)
        {
            using var con = Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = """
                UPDATE search_tasks
                SET resume_category_index=CASE WHEN $catIndex > 0 THEN $catIndex ELSE resume_category_index END,
                    current_category=$category,
                    current_page=$page,
                    updated_at=$now
                WHERE id=$id
                """;
            Add(cmd, "$id", taskId);
            Add(cmd, "$catIndex", Math.Max(1, categoryIndex));
            Add(cmd, "$category", categoryName);
            Add(cmd, "$page", page);
            Add(cmd, "$now", DateTime.Now.ToString("O"));
            cmd.ExecuteNonQuery();
        }
    }

    public void UpdateStatus(long taskId, string status, string lastError = "")
    {
        if (taskId <= 0) return;
        lock (_sync)
        {
            using var con = Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = """
                UPDATE search_tasks
                SET status=$status, last_error=$error, updated_at=$now
                WHERE id=$id
                """;
            Add(cmd, "$id", taskId);
            Add(cmd, "$status", status);
            Add(cmd, "$error", lastError);
            Add(cmd, "$now", DateTime.Now.ToString("O"));
            cmd.ExecuteNonQuery();
        }
    }

    public List<SearchTaskRecord> GetTasks()
    {
        lock (_sync)
        {
            using var con = Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT * FROM search_tasks ORDER BY updated_at DESC, id DESC LIMIT 500";
            using var reader = cmd.ExecuteReader();
            var tasks = new List<SearchTaskRecord>();
            while (reader.Read())
                tasks.Add(ReadTask(reader));
            return tasks;
        }
    }

    public SearchTaskRecord? GetTask(long id)
    {
        lock (_sync)
        {
            using var con = Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT * FROM search_tasks WHERE id=$id";
            Add(cmd, "$id", id);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadTask(reader) : null;
        }
    }

    /// <summary>
    /// Most recent NON-completed task for a keyword (status Running/Failed/Stopped) — its checkpoint
    /// and products are reused to resume. Returns 0 if none (start fresh).
    /// </summary>
    public long GetResumableTaskId(string keyword)
    {
        lock (_sync)
        {
            using var con = Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = """
                SELECT id FROM search_tasks
                WHERE keyword=$kw AND status IN ('Running','Failed','Stopped')
                ORDER BY updated_at DESC, id DESC LIMIT 1
                """;
            Add(cmd, "$kw", keyword);
            var result = cmd.ExecuteScalar();
            return result is long id ? id : (result != null && long.TryParse(result.ToString(), out var n) ? n : 0);
        }
    }

    /// <summary>Keywords that have a non-completed task (status Running/Failed/Stopped) — i.e. started
    /// but "chưa kết thúc", còn resume được. Case-insensitive set.</summary>
    public HashSet<string> GetResumableKeywords()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (_sync)
        {
            using var con = Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT keyword FROM search_tasks WHERE status IN ('Running','Failed','Stopped')";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var kw = reader.IsDBNull(0) ? "" : reader.GetString(0);
                if (!string.IsNullOrWhiteSpace(kw)) set.Add(kw);
            }
        }
        return set;
    }

    /// <summary>True if any task is still "Running" — a previous run was cut off mid-crawl.</summary>
    public bool HasRunningTask()
    {
        lock (_sync)
        {
            using var con = Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM search_tasks WHERE status='Running')";
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) != 0;
        }
    }

    public List<ProductResult> GetProducts(long taskId)
    {
        lock (_sync)
        {
            using var con = Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT * FROM task_products WHERE task_id=$taskId ORDER BY id";
            Add(cmd, "$taskId", taskId);
            using var reader = cmd.ExecuteReader();
            var products = new List<ProductResult>();
            while (reader.Read())
            {
                products.Add(new ProductResult
                {
                    ItemId = reader.GetInt64(reader.GetOrdinal("item_id")),
                    ShopId = reader.GetInt64(reader.GetOrdinal("shop_id")),
                    Name = GetString(reader, "name"),
                    PriceVnd = GetDecimal(reader, "price_vnd"),
                    PriceOriginalVnd = GetDecimal(reader, "original_price_vnd"),
                    MonthlySold = GetInt(reader, "monthly_sold"),
                    Rating = GetDouble(reader, "rating"),
                    LikedCount = GetInt(reader, "liked_count"),
                    CommentCount = GetInt(reader, "comment_count"),
                    ShopLocation = GetString(reader, "shop_location"),
                    ImageUrl = GetString(reader, "image_url"),
                    Category = GetString(reader, "category"),
                    ShopName = GetString(reader, "shop_name"),
                });
            }
            return products;
        }
    }

    /// <summary>
    /// All keyword-mode products across EVERY task, de-duplicated by itemId (shopId may repeat across
    /// keywords but itemId is unique). Keeps the most-recently-updated row per itemId. For the
    /// "Xuất tất cả (gộp)" combined export.
    /// </summary>
    public List<ProductResult> GetAllKeywordProducts()
    {
        lock (_sync)
        {
            using var con = Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT * FROM task_products ORDER BY updated_at DESC, id DESC";
            using var reader = cmd.ExecuteReader();
            var byItem = new Dictionary<long, ProductResult>();
            while (reader.Read())
            {
                var itemId = reader.GetInt64(reader.GetOrdinal("item_id"));
                if (byItem.ContainsKey(itemId)) continue; // đã có bản mới nhất → bỏ trùng itemId
                byItem[itemId] = new ProductResult
                {
                    ItemId = itemId,
                    ShopId = reader.GetInt64(reader.GetOrdinal("shop_id")),
                    Name = GetString(reader, "name"),
                    PriceVnd = GetDecimal(reader, "price_vnd"),
                    PriceOriginalVnd = GetDecimal(reader, "original_price_vnd"),
                    MonthlySold = GetInt(reader, "monthly_sold"),
                    Rating = GetDouble(reader, "rating"),
                    LikedCount = GetInt(reader, "liked_count"),
                    CommentCount = GetInt(reader, "comment_count"),
                    ShopLocation = GetString(reader, "shop_location"),
                    ImageUrl = GetString(reader, "image_url"),
                    Category = GetString(reader, "category"),
                    ShopName = GetString(reader, "shop_name"),
                };
            }
            return byItem.Values.ToList();
        }
    }

    private void Initialize()
    {
        lock (_sync)
        {
            using var con = Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS search_tasks (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    keyword TEXT NOT NULL,
                    account_id TEXT NOT NULL,
                    account_name TEXT NOT NULL,
                    region_filter TEXT NOT NULL DEFAULT '',
                    min_price INTEGER NOT NULL DEFAULT 0,
                    min_sold INTEGER NOT NULL DEFAULT 0,
                    check_stock INTEGER NOT NULL DEFAULT 1,
                    status TEXT NOT NULL,
                    resume_category_index INTEGER NOT NULL DEFAULT 1,
                    current_category TEXT NOT NULL DEFAULT '',
                    current_page INTEGER NOT NULL DEFAULT 0,
                    product_count INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    last_error TEXT NOT NULL DEFAULT ''
                );

                CREATE TABLE IF NOT EXISTS task_products (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    task_id INTEGER NOT NULL,
                    item_id INTEGER NOT NULL,
                    shop_id INTEGER NOT NULL,
                    name TEXT NOT NULL DEFAULT '',
                    price_vnd REAL NOT NULL DEFAULT 0,
                    original_price_vnd REAL NOT NULL DEFAULT 0,
                    monthly_sold INTEGER NOT NULL DEFAULT 0,
                    rating REAL NOT NULL DEFAULT 0,
                    liked_count INTEGER NOT NULL DEFAULT 0,
                    comment_count INTEGER NOT NULL DEFAULT 0,
                    shop_location TEXT NOT NULL DEFAULT '',
                    image_url TEXT NOT NULL DEFAULT '',
                    category TEXT NOT NULL DEFAULT '',
                    shop_name TEXT NOT NULL DEFAULT '',
                    updated_at TEXT NOT NULL,
                    UNIQUE(task_id, item_id, shop_id)
                );

                CREATE TABLE IF NOT EXISTS shop_products (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    shop_id INTEGER NOT NULL,
                    shop_name TEXT NOT NULL DEFAULT '',
                    source_link TEXT NOT NULL DEFAULT '',
                    item_id INTEGER NOT NULL,
                    name TEXT NOT NULL DEFAULT '',
                    price_vnd REAL NOT NULL DEFAULT 0,
                    original_price_vnd REAL NOT NULL DEFAULT 0,
                    monthly_sold INTEGER NOT NULL DEFAULT 0,
                    rating REAL NOT NULL DEFAULT 0,
                    liked_count INTEGER NOT NULL DEFAULT 0,
                    comment_count INTEGER NOT NULL DEFAULT 0,
                    shop_location TEXT NOT NULL DEFAULT '',
                    image_url TEXT NOT NULL DEFAULT '',
                    category TEXT NOT NULL DEFAULT '',
                    scanned_at TEXT NOT NULL,
                    UNIQUE(shop_id, item_id)
                );

                CREATE TABLE IF NOT EXISTS categories (
                    name TEXT PRIMARY KEY COLLATE NOCASE,
                    first_seen TEXT NOT NULL,
                    last_seen TEXT NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Một danh mục trong "từ điển" danh mục (số liệu tính trực tiếp từ shop_products).</summary>
    public sealed record CategoryRow(string Name, int ProductCount, int ShopCount, string FirstSeen, string LastSeen);

    /// <summary>Upsert danh mục (theo tên, không phân biệt hoa/thường): có thì cập nhật last_seen,
    /// chưa có thì thêm mới. Gọi tự động khi lưu sản phẩm shop (quét shop).</summary>
    public void UpsertCategories(IEnumerable<string> names)
    {
        var distinct = names
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinct.Count == 0) return;

        lock (_sync)
        {
            using var con = Open();
            using var tx = con.BeginTransaction();
            var now = DateTime.Now.ToString("O");
            foreach (var name in distinct)
            {
                using var cmd = con.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO categories (name, first_seen, last_seen)
                    VALUES ($name, $now, $now)
                    ON CONFLICT(name) DO UPDATE SET last_seen=excluded.last_seen
                    """;
                Add(cmd, "$name", name);
                Add(cmd, "$now", now);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>Tất cả sản phẩm shop (shopId, itemId, name) — để phân loại lại danh mục bằng AI theo tên.</summary>
    public List<(long ShopId, long ItemId, string Name)> GetAllShopProductsForCategory()
    {
        lock (_sync)
        {
            using var con = Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT shop_id, item_id, name FROM shop_products ORDER BY id";
            using var reader = cmd.ExecuteReader();
            var list = new List<(long, long, string)>();
            while (reader.Read())
                list.Add((reader.GetInt64(0), reader.GetInt64(1), GetString(reader, "name")));
            return list;
        }
    }

    /// <summary>Xóa các danh mục không còn sản phẩm nào tham chiếu (dọn danh mục rác sau khi phân loại lại).</summary>
    public void PruneUnusedCategories()
    {
        lock (_sync)
        {
            using var con = Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = """
                DELETE FROM categories
                WHERE name NOT IN (SELECT DISTINCT category FROM shop_products WHERE TRIM(category) <> '')
                """;
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Ghi danh mục cho các sản phẩm shop (sau khi phân loại bằng AI), rồi upsert vào từ điển.</summary>
    public void SetShopProductCategories(IReadOnlyList<(long ShopId, long ItemId, string Category)> updates)
    {
        if (updates.Count == 0) return;
        lock (_sync)
        {
            using var con = Open();
            using var tx = con.BeginTransaction();
            foreach (var u in updates)
            {
                using var cmd = con.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE shop_products SET category = $c WHERE shop_id = $s AND item_id = $i";
                Add(cmd, "$c", u.Category);
                Add(cmd, "$s", u.ShopId);
                Add(cmd, "$i", u.ItemId);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        UpsertCategories(updates.Select(u => u.Category));
    }

    /// <summary>Sản phẩm shop thuộc 1 danh mục (theo tên, không phân biệt hoa/thường) — cho tab Danh mục.</summary>
    public List<ProductResult> GetShopProductsByCategory(string category)
    {
        lock (_sync)
        {
            using var con = Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT * FROM shop_products WHERE category = $c COLLATE NOCASE ORDER BY monthly_sold DESC, id";
            Add(cmd, "$c", category);
            using var reader = cmd.ExecuteReader();
            var products = new List<ProductResult>();
            while (reader.Read())
            {
                products.Add(new ProductResult
                {
                    ItemId = reader.GetInt64(reader.GetOrdinal("item_id")),
                    ShopId = reader.GetInt64(reader.GetOrdinal("shop_id")),
                    Name = GetString(reader, "name"),
                    PriceVnd = GetDecimal(reader, "price_vnd"),
                    PriceOriginalVnd = GetDecimal(reader, "original_price_vnd"),
                    MonthlySold = GetInt(reader, "monthly_sold"),
                    Rating = GetDouble(reader, "rating"),
                    LikedCount = GetInt(reader, "liked_count"),
                    CommentCount = GetInt(reader, "comment_count"),
                    ShopLocation = GetString(reader, "shop_location"),
                    ImageUrl = GetString(reader, "image_url"),
                    Category = GetString(reader, "category"),
                    ShopName = GetString(reader, "shop_name"),
                });
            }
            return products;
        }
    }

    /// <summary>Đọc từ điển danh mục kèm số sản phẩm / số shop (tính từ shop_products theo tên).</summary>
    public List<CategoryRow> GetCategories()
    {
        lock (_sync)
        {
            using var con = Open();
            using var cmd = con.CreateCommand();
            // LEFT JOIN để danh mục chưa có sản phẩm (vd shop_products bị xoá) vẫn hiện với count 0.
            cmd.CommandText = """
                SELECT c.name AS name, c.first_seen AS first_seen, c.last_seen AS last_seen,
                       COUNT(sp.item_id) AS product_count,
                       COUNT(DISTINCT sp.shop_id) AS shop_count
                FROM categories c
                LEFT JOIN shop_products sp ON sp.category = c.name COLLATE NOCASE
                GROUP BY c.name
                ORDER BY product_count DESC, c.name COLLATE NOCASE
                """;
            using var reader = cmd.ExecuteReader();
            var rows = new List<CategoryRow>();
            while (reader.Read())
            {
                rows.Add(new CategoryRow(
                    GetString(reader, "name"),
                    GetInt(reader, "product_count"),
                    GetInt(reader, "shop_count"),
                    GetString(reader, "first_seen"),
                    GetString(reader, "last_seen")));
            }
            return rows;
        }
    }

    /// <summary>Persists scraped products of a shop (file/shop-from-link mode) for later export.</summary>
    public void SaveShopProducts(long shopId, string shopName, string sourceLink, IReadOnlyList<ProductResult> products)
    {
        if (products.Count == 0) return;
        lock (_sync)
        {
            using var con = Open();
            using var tx = con.BeginTransaction();
            foreach (var p in products)
            {
                if (p.ItemId <= 0) continue;
                using var cmd = con.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO shop_products
                    (shop_id, shop_name, source_link, item_id, name, price_vnd, original_price_vnd,
                     monthly_sold, rating, liked_count, comment_count, shop_location, image_url, category, scanned_at)
                    VALUES ($shopId, $shopName, $link, $itemId, $name, $price, $originalPrice,
                     $sold, $rating, $liked, $comments, $location, $image, $category, $now)
                    ON CONFLICT(shop_id, item_id) DO UPDATE SET
                        shop_name=excluded.shop_name,
                        source_link=excluded.source_link,
                        name=excluded.name,
                        price_vnd=excluded.price_vnd,
                        original_price_vnd=excluded.original_price_vnd,
                        monthly_sold=excluded.monthly_sold,
                        rating=excluded.rating,
                        liked_count=excluded.liked_count,
                        comment_count=excluded.comment_count,
                        shop_location=excluded.shop_location,
                        image_url=excluded.image_url,
                        category=excluded.category,
                        scanned_at=excluded.scanned_at
                    """;
                Add(cmd, "$shopId", shopId > 0 ? shopId : p.ShopId);
                Add(cmd, "$shopName", shopName);
                Add(cmd, "$link", sourceLink);
                Add(cmd, "$itemId", p.ItemId);
                Add(cmd, "$name", p.Name);
                Add(cmd, "$price", p.PriceVnd);
                Add(cmd, "$originalPrice", p.PriceOriginalVnd);
                Add(cmd, "$sold", p.MonthlySold);
                Add(cmd, "$rating", p.Rating);
                Add(cmd, "$liked", p.LikedCount);
                Add(cmd, "$comments", p.CommentCount);
                Add(cmd, "$location", p.ShopLocation);
                Add(cmd, "$image", p.ImageUrl);
                Add(cmd, "$category", p.Category);
                Add(cmd, "$now", DateTime.Now.ToString("O"));
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }

        // Tự upsert danh mục từ các sản phẩm vừa quét của shop này.
        UpsertCategories(products.Select(p => p.Category));
    }

    /// <summary>Reads stored products for a shop (for re-export).</summary>
    public List<ProductResult> GetShopProducts(long shopId)
    {
        lock (_sync)
        {
            using var con = Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT * FROM shop_products WHERE shop_id=$shopId ORDER BY id";
            Add(cmd, "$shopId", shopId);
            using var reader = cmd.ExecuteReader();
            var products = new List<ProductResult>();
            while (reader.Read())
            {
                products.Add(new ProductResult
                {
                    ItemId = reader.GetInt64(reader.GetOrdinal("item_id")),
                    ShopId = reader.GetInt64(reader.GetOrdinal("shop_id")),
                    Name = GetString(reader, "name"),
                    PriceVnd = GetDecimal(reader, "price_vnd"),
                    PriceOriginalVnd = GetDecimal(reader, "original_price_vnd"),
                    MonthlySold = GetInt(reader, "monthly_sold"),
                    Rating = GetDouble(reader, "rating"),
                    LikedCount = GetInt(reader, "liked_count"),
                    CommentCount = GetInt(reader, "comment_count"),
                    ShopLocation = GetString(reader, "shop_location"),
                    ImageUrl = GetString(reader, "image_url"),
                    Category = GetString(reader, "category"),
                    ShopName = GetString(reader, "shop_name"),
                });
            }
            return products;
        }
    }

    /// <summary>
    /// All file-mode (shop-from-link) products across EVERY shop ever scanned, de-duplicated by
    /// itemId (shopId may repeat, itemId is unique). Keeps the most-recently-scanned row per itemId.
    /// For the file tab's "Xuất tất cả" combined export — independent of which files are loaded.
    /// </summary>
    public List<ProductResult> GetAllShopProducts()
    {
        lock (_sync)
        {
            using var con = Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT * FROM shop_products ORDER BY scanned_at DESC, id DESC";
            using var reader = cmd.ExecuteReader();
            var byItem = new Dictionary<long, ProductResult>();
            while (reader.Read())
            {
                var itemId = reader.GetInt64(reader.GetOrdinal("item_id"));
                if (byItem.ContainsKey(itemId)) continue; // đã có bản mới nhất → bỏ trùng itemId
                byItem[itemId] = new ProductResult
                {
                    ItemId = itemId,
                    ShopId = reader.GetInt64(reader.GetOrdinal("shop_id")),
                    Name = GetString(reader, "name"),
                    PriceVnd = GetDecimal(reader, "price_vnd"),
                    PriceOriginalVnd = GetDecimal(reader, "original_price_vnd"),
                    MonthlySold = GetInt(reader, "monthly_sold"),
                    Rating = GetDouble(reader, "rating"),
                    LikedCount = GetInt(reader, "liked_count"),
                    CommentCount = GetInt(reader, "comment_count"),
                    ShopLocation = GetString(reader, "shop_location"),
                    ImageUrl = GetString(reader, "image_url"),
                    Category = GetString(reader, "category"),
                    ShopName = GetString(reader, "shop_name"),
                };
            }
            return byItem.Values.ToList();
        }
    }

    /// <summary>Xóa task + sản phẩm của tab "Tìm với từ khóa" (keyword không phải URL).</summary>
    public void ClearKeywordSearchHistory()
    {
        lock (_sync)
        {
            using var con = Open();
            using var tx = con.BeginTransaction();
            Exec(con, tx, """
                DELETE FROM task_products
                WHERE task_id IN (SELECT id FROM search_tasks WHERE keyword NOT LIKE 'http%')
                """);
            Exec(con, tx, "DELETE FROM search_tasks WHERE keyword NOT LIKE 'http%'");
            tx.Commit();
        }
    }

    /// <summary>Xóa task link + bảng shop_products của tab "Tìm theo file".</summary>
    public void ClearFileSearchHistory()
    {
        lock (_sync)
        {
            using var con = Open();
            using var tx = con.BeginTransaction();
            Exec(con, tx, """
                DELETE FROM task_products
                WHERE task_id IN (SELECT id FROM search_tasks WHERE keyword LIKE 'http%')
                """);
            Exec(con, tx, "DELETE FROM search_tasks WHERE keyword LIKE 'http%'");
            Exec(con, tx, "DELETE FROM shop_products");
            Exec(con, tx, "DELETE FROM categories");
            tx.Commit();
        }
    }

    private static void Exec(SqliteConnection con, SqliteTransaction tx, string sql)
    {
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var con = new SqliteConnection($"Data Source={_dbPath}");
        con.Open();
        return con;
    }

    private static void Add(SqliteCommand cmd, string name, object? value) =>
        cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static void AddProductParams(SqliteCommand cmd, long taskId, ProductResult product)
    {
        Add(cmd, "$taskId", taskId);
        Add(cmd, "$itemId", product.ItemId);
        Add(cmd, "$shopId", product.ShopId);
        Add(cmd, "$name", product.Name);
        Add(cmd, "$price", product.PriceVnd);
        Add(cmd, "$originalPrice", product.PriceOriginalVnd);
        Add(cmd, "$sold", product.MonthlySold);
        Add(cmd, "$rating", product.Rating);
        Add(cmd, "$liked", product.LikedCount);
        Add(cmd, "$comments", product.CommentCount);
        Add(cmd, "$location", product.ShopLocation);
        Add(cmd, "$image", product.ImageUrl);
        Add(cmd, "$category", product.Category);
        Add(cmd, "$shopName", product.ShopName);
        Add(cmd, "$now", DateTime.Now.ToString("O"));
    }

    private static SearchTaskRecord ReadTask(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(reader.GetOrdinal("id")),
        Keyword = GetString(reader, "keyword"),
        AccountId = GetString(reader, "account_id"),
        AccountName = GetString(reader, "account_name"),
        RegionFilterText = GetString(reader, "region_filter"),
        MinPriceVnd = reader.GetInt64(reader.GetOrdinal("min_price")),
        MinMonthlySold = GetInt(reader, "min_sold"),
        CheckVariantStock = GetInt(reader, "check_stock") != 0,
        Status = GetString(reader, "status"),
        ResumeCategoryIndex = GetInt(reader, "resume_category_index"),
        CurrentCategory = GetString(reader, "current_category"),
        CurrentPage = GetInt(reader, "current_page"),
        ProductCount = GetInt(reader, "product_count"),
        CreatedAt = ParseDate(GetString(reader, "created_at")),
        UpdatedAt = ParseDate(GetString(reader, "updated_at")),
        LastError = GetString(reader, "last_error"),
    };

    private static string GetString(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? "" : reader.GetString(ordinal);
    }

    private static int GetInt(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? 0 : reader.GetInt32(ordinal);
    }

    private static double GetDouble(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? 0 : reader.GetDouble(ordinal);
    }

    private static decimal GetDecimal(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? 0 : Convert.ToDecimal(reader.GetValue(ordinal));
    }

    private static DateTime ParseDate(string value) =>
        DateTime.TryParse(value, out var dt) ? dt : DateTime.MinValue;
}
