using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SosaFam.Api.Data;
using SosaFam.Api.Grocery;
using SosaFam.Api.Realtime;
using SosaFam.Api.Tenancy;

namespace SosaFam.Api.Pantry;

// ---- DTOs. Ids are Proxy-IDs (strings); integer keys never leave the API. ----

public record LocationDto(string Id, string Name, string TrackingMode, int SortOrder);
public record LocationRequest(string? Name, string? TrackingMode, int? SortOrder);

public record StockDto(string LocationId, string LocationName, string TrackingMode, decimal Quantity, string? Status, DateTimeOffset UpdatedAt);

/// <param name="GroceryProductId">The value grocery items carry in productId for this product.</param>
/// <param name="OnHand">Total across Count locations.</param>
/// <param name="NeedsRestock">OnHand is below MinQuantity, or a staple is Low or Out in a Status location.</param>
/// <param name="SuggestedQuantity">How much to buy to reach the target (or the minimum when there is no target).</param>
public record ProductDto(
    string Id, Guid GroceryProductId, string Name, string? Barcode, string? Category, string? Unit,
    decimal? MinQuantity, decimal? TargetQuantity, bool IsStaple, Guid? StoreId, string? StoreName, string? ImageUrl,
    decimal OnHand, bool NeedsRestock, decimal? SuggestedQuantity, IReadOnlyList<StockDto> Stock);

public record ProductRequest(
    string Name, string? Barcode, string? Category, string? Unit, decimal? MinQuantity, decimal? TargetQuantity,
    bool? IsStaple, Guid? StoreId, string? ImageUrl);

public record AdjustRequest(decimal Delta, string? Note);
public record SetStockRequest(decimal? Quantity, string? Status, string? Note);
public record MoveRequest(string FromLocationId, string ToLocationId, decimal Quantity);

public record NeedDto(ProductDto Product, bool OnOpenList);
public record AddNeedsRequest(Guid ListId, IReadOnlyList<string>? ProductIds);
public record AddNeedsResult(int Added, int AlreadyOnList, IReadOnlyList<GroceryItemDto> Items);

/// <param name="Quantity">Defaults to the grocery item's quantity, or 1.</param>
public record PutAwayRequest(Guid GroceryItemId, string LocationId, decimal? Quantity);

public record ActivityDto(string ProductId, string ProductName, string LocationName, string ChangeType,
    decimal? QuantityBefore, decimal? QuantityAfter, string? StatusAfter, string? Note, string? ByName, DateTimeOffset At);

public static class PantryEndpoints
{
    /// <summary>Created the first time a household opens the food storage app.</summary>
    public static readonly (string Name, string Mode)[] DefaultLocations =
        [("Pantry", TrackingModes.Count), ("Fridge", TrackingModes.Status), ("Basement", TrackingModes.Count)];

    public static void MapPantryEndpoints(this IEndpointRouteBuilder app)
    {
        var hh = app.MapHouseholdGroup().MapGroup("/pantry").WithTags("Pantry");

        // ---- Locations ----

        hh.MapGet("/locations", async (AppDbContext db, CancellationToken ct) =>
        {
            if (!await db.Locations.AnyAsync(l => !l.IsDeleted, ct))
            {
                var now = DateTime.UtcNow;
                var order = 0;
                foreach (var (name, mode) in DefaultLocations)
                {
                    var loc = new Location { Name = name, TrackingMode = mode, SortOrder = order++ };
                    ProxyId.Assign(loc, Location.Prefix, now);
                    db.Locations.Add(loc);
                }
                try { await db.SaveChangesAsync(ct); }
                catch (DbUpdateException) { /* another phone created them at the same moment */ }
            }
            return Results.Ok(await LocationsQuery(db).ToListAsync(ct));
        });

        hh.MapPost("/locations", async (LocationRequest req, AppDbContext db, PantryNotifier notify, CancellationToken ct) =>
        {
            var name = Clean(req.Name, 60);
            if (name is null) return Invalid("name", "Location name is required (60 characters max).");
            var mode = req.TrackingMode ?? TrackingModes.Count;
            if (!TrackingModes.All.Contains(mode)) return Invalid("trackingMode", "Tracking mode must be Count or Status.");
            if (await db.Locations.AnyAsync(l => !l.IsDeleted && l.Name == name, ct)) return Results.Problem(statusCode: 409, title: $"{name} is already a location.");
            var loc = new Location
            {
                Name = name, TrackingMode = mode,
                SortOrder = req.SortOrder ?? (await db.Locations.Where(l => !l.IsDeleted).MaxAsync(l => (int?)l.SortOrder, ct) ?? -1) + 1,
            };
            ProxyId.Assign(loc, Location.Prefix, DateTime.UtcNow);
            db.Locations.Add(loc);
            await db.SaveChangesAsync(ct);
            await notify.LocationsChanged(ct);
            return Results.Ok(ToDto(loc));
        });

        hh.MapPatch("/locations/{locationId}", async (string locationId, LocationRequest req, AppDbContext db, PantryNotifier notify, CancellationToken ct) =>
        {
            var loc = await FindLocation(db, locationId, ct);
            if (loc is null) return Results.NotFound();
            if (req.Name is not null)
            {
                var name = Clean(req.Name, 60);
                if (name is null) return Invalid("name", "Location name is required (60 characters max).");
                if (await db.Locations.AnyAsync(l => !l.IsDeleted && l.Name == name && l.LocationID != loc.LocationID, ct))
                    return Results.Problem(statusCode: 409, title: $"{name} is already a location.");
                loc.Name = name;
            }
            if (req.TrackingMode is not null)
            {
                if (!TrackingModes.All.Contains(req.TrackingMode)) return Invalid("trackingMode", "Tracking mode must be Count or Status.");
                loc.TrackingMode = req.TrackingMode;
            }
            if (req.SortOrder is { } sort) loc.SortOrder = sort;
            await db.SaveChangesAsync(ct);
            await notify.LocationsChanged(ct);
            return Results.Ok(ToDto(loc));
        });

        hh.MapDelete("/locations/{locationId}", async (string locationId, TenantContext tenant, AppDbContext db, PantryNotifier notify, CancellationToken ct) =>
        {
            if (tenant.RequireRole(Roles.Owner, Roles.Adult) is { } denied) return denied;
            var loc = await FindLocation(db, locationId, ct);
            if (loc is null) return Results.NotFound();
            // Soft delete: the location and its stock disappear; history keeps them.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            loc.IsDeleted = true;
            await db.SaveChangesAsync(ct);
            await SoftDeleteStock(db, s => s.LocationId == loc.LocationID, tenant, ct);
            await tx.CommitAsync(ct);
            await notify.LocationsChanged(ct);
            await notify.ProductsChanged(ct);
            return Results.NoContent();
        });

        // ---- Products ----

        hh.MapGet("/products", async (string? search, string? category, bool? needsRestock, AppDbContext db, CancellationToken ct) =>
        {
            var q = db.Products.AsNoTracking().Where(p => !p.IsDeleted);
            if (Clean(search, 120) is { } s) q = q.Where(p => p.Name.Contains(s) || p.Barcode == s);
            if (Clean(category, 60) is { } c) q = q.Where(p => p.Category == c);
            var products = await Load(db, q, ct);
            if (needsRestock is { } n) products = products.Where(p => p.NeedsRestock == n).ToList();
            return Results.Ok(products);
        });

        hh.MapGet("/products/{productId}", async (string productId, AppDbContext db, CancellationToken ct) =>
            await LoadOne(db, productId, ct) is { } dto ? Results.Ok(dto) : Results.NotFound());

        // Barcode scan: 200 with the product, or 404 when the household has not seen this barcode yet.
        hh.MapGet("/products/by-barcode/{barcode}", async (string barcode, AppDbContext db, CancellationToken ct) =>
        {
            var code = Clean(barcode, 32);
            if (code is null) return Results.NotFound();
            var list = await Load(db, db.Products.AsNoTracking().Where(p => !p.IsDeleted && p.Barcode == code), ct);
            return list.Count == 1 ? Results.Ok(list[0]) : Results.NotFound();
        });

        hh.MapPost("/products", async (ProductRequest req, AppDbContext db, PantryNotifier notify, CancellationToken ct) =>
        {
            var product = new Product();
            if (await Apply(product, req, db, ct) is { } invalid) return invalid;
            ProxyId.Assign(product, Product.Prefix, DateTime.UtcNow);
            db.Products.Add(product);
            await db.SaveChangesAsync(ct);
            var dto = (await LoadOne(db, product.Proxy, ct))!;
            await notify.ProductUpserted(dto, ct);
            return Results.Ok(dto);
        });

        hh.MapPut("/products/{productId}", async (string productId, ProductRequest req, AppDbContext db, PantryNotifier notify, CancellationToken ct) =>
        {
            var product = await FindProduct(db, productId, ct);
            if (product is null) return Results.NotFound();
            if (await Apply(product, req, db, ct) is { } invalid) return invalid;
            await db.SaveChangesAsync(ct);
            var dto = (await LoadOne(db, product.Proxy, ct))!;
            await notify.ProductUpserted(dto, ct);
            return Results.Ok(dto);
        });

        hh.MapDelete("/products/{productId}", async (string productId, TenantContext tenant, AppDbContext db, PantryNotifier notify, CancellationToken ct) =>
        {
            var product = await FindProduct(db, productId, ct);
            if (product is null) return Results.NotFound();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            product.IsDeleted = true;
            await db.SaveChangesAsync(ct);
            await SoftDeleteStock(db, s => s.ProductId == product.ProductID, tenant, ct);
            await tx.CommitAsync(ct);
            await notify.ProductDeleted(product.Proxy, ct);
            return Results.NoContent();
        });

        // ---- Stock ----

        // +/- buttons. Deltas (not absolute values) so two people tapping at once both count.
        hh.MapPost("/products/{productId}/stock/{locationId}/adjust", async (string productId, string locationId, AdjustRequest req,
            AppDbContext db, PantryNotifier notify, CancellationToken ct) =>
        {
            if (req.Delta == 0 || Math.Abs(req.Delta) > 99_999) return Invalid("delta", "Delta must be a non-zero amount.");
            var (product, loc, error) = await FindPair(db, productId, locationId, ct);
            if (error is not null) return error;
            if (loc!.TrackingMode != TrackingModes.Count) return Invalid("locationId", $"{loc.Name} tracks Have / Low / Out; set its status instead.");
            await Stock.Adjust(db, product!, loc, req.Delta, StockChangeTypes.Adjust, Clean(req.Note, 200), ct);
            return await Reply(db, product!, notify, ct);
        });

        // Set an exact count (Count locations) or a status (Status locations).
        hh.MapPut("/products/{productId}/stock/{locationId}", async (string productId, string locationId, SetStockRequest req,
            AppDbContext db, PantryNotifier notify, CancellationToken ct) =>
        {
            var (product, loc, error) = await FindPair(db, productId, locationId, ct);
            if (error is not null) return error;
            if (loc!.TrackingMode == TrackingModes.Count)
            {
                if (req.Quantity is not { } qty || qty < 0 || qty > 99_999_999) return Invalid("quantity", "Quantity must be 0 or more.");
                await Stock.Set(db, product!, loc, qty, null, Clean(req.Note, 200), ct);
            }
            else
            {
                if (req.Status is null || !StockStatuses.All.Contains(req.Status)) return Invalid("status", "Status must be Have, Low or Out.");
                await Stock.Set(db, product!, loc, null, req.Status, Clean(req.Note, 200), ct);
            }
            return await Reply(db, product!, notify, ct);
        });

        // "Bring up 2 from the basement."
        hh.MapPost("/products/{productId}/move", async (string productId, MoveRequest req, AppDbContext db, PantryNotifier notify, CancellationToken ct) =>
        {
            if (req.Quantity <= 0 || req.Quantity > 99_999) return Invalid("quantity", "Quantity must be more than 0.");
            var (product, from, error) = await FindPair(db, productId, req.FromLocationId, ct);
            if (error is not null) return error;
            var to = await FindLocation(db, req.ToLocationId, ct);
            if (to is null) return Results.NotFound();
            if (from!.LocationID == to.LocationID) return Invalid("toLocationId", "Pick a different location.");
            if (from.TrackingMode != TrackingModes.Count || to.TrackingMode != TrackingModes.Count)
                return Invalid("toLocationId", "Moves are between counted locations.");

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var available = await db.StockLevels.Where(s => s.ProductId == product!.ProductID && s.LocationId == from.LocationID && !s.IsDeleted)
                .Select(s => (decimal?)s.Quantity).FirstOrDefaultAsync(ct) ?? 0;
            if (available < req.Quantity) return Invalid("quantity", $"Only {available:0.##} in {from.Name}.");
            await Stock.Adjust(db, product!, from, -req.Quantity, StockChangeTypes.MoveOut, $"To {to.Name}", ct);
            await Stock.Adjust(db, product!, to, req.Quantity, StockChangeTypes.MoveIn, $"From {from.Name}", ct);
            await tx.CommitAsync(ct);
            return await Reply(db, product!, notify, ct);
        });

        // ---- Shopping ----

        // What to buy: below minimum, or a staple running low in the fridge. OnOpenList says whether
        // an unchecked item for it is already on any open grocery list.
        hh.MapGet("/needs", async (AppDbContext db, CancellationToken ct) =>
        {
            var needs = (await Load(db, db.Products.AsNoTracking().Where(p => !p.IsDeleted), ct)).Where(p => p.NeedsRestock).ToList();
            var onList = await OpenListProductIds(db, ct);
            return Results.Ok(needs.Select(p => new NeedDto(p, onList.Contains(p.GroceryProductId))).ToList());
        });

        // Puts needs on a grocery list (all of them, or the ones named), tagged with each product's store.
        // Products already unchecked on that list are skipped.
        hh.MapPost("/needs/add-to-list", async (AddNeedsRequest req, TenantContext tenant, AppDbContext db, Notifier groceryNotify, CancellationToken ct) =>
        {
            var list = await db.GroceryLists.AsNoTracking().FirstOrDefaultAsync(l => l.Id == req.ListId && l.ArchivedAt == null, ct);
            if (list is null) return Results.NotFound();
            var needs = (await Load(db, db.Products.AsNoTracking().Where(p => !p.IsDeleted), ct)).Where(p => p.NeedsRestock);
            if (req.ProductIds is { Count: > 0 } ids) needs = needs.Where(p => ids.Contains(p.Id));
            var already = (await db.GroceryItems.AsNoTracking()
                .Where(i => i.ListId == list.Id && !i.IsChecked && i.ProductId != null)
                .Select(i => i.ProductId!.Value).ToListAsync(ct)).ToHashSet();

            var added = new List<GroceryItem>();
            var skipped = 0;
            foreach (var p in needs)
            {
                if (already.Contains(p.GroceryProductId)) { skipped++; continue; }
                added.Add(new GroceryItem
                {
                    Id = Guid.NewGuid(), ListId = list.Id, AddedByUserId = tenant.UserId!.Value,
                    Name = p.Name, Quantity = p.SuggestedQuantity, Unit = p.Unit, StoreId = p.StoreId,
                    Source = GrocerySources.LowStock, ProductId = p.GroceryProductId,
                });
            }
            db.GroceryItems.AddRange(added);
            await db.SaveChangesAsync(ct);

            var addedIds = added.Select(a => a.Id).ToList();
            var dtos = await GroceryEndpoints.ItemQuery(db, i => addedIds.Contains(i.Id)).ToListAsync(ct);
            foreach (var dto in dtos) await groceryNotify.ItemUpserted(dto, ct);
            if (dtos.Count > 0) await groceryNotify.ListsChanged(ct);
            return Results.Ok(new AddNeedsResult(dtos.Count, skipped, dtos));
        });

        // After shopping: a checked-off grocery item for a product goes into a location.
        hh.MapPost("/put-away", async (PutAwayRequest req, AppDbContext db, PantryNotifier notify, CancellationToken ct) =>
        {
            var item = await db.GroceryItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == req.GroceryItemId, ct);
            if (item is null) return Results.NotFound();
            if (item.ProductId is not { } guid) return Invalid("groceryItemId", "This grocery item is not linked to a pantry product.");
            var product = await db.Products.FirstOrDefaultAsync(p => p.ProxyGuid == guid && !p.IsDeleted, ct);
            if (product is null) return Invalid("groceryItemId", "The linked pantry product no longer exists.");
            var loc = await FindLocation(db, req.LocationId, ct);
            if (loc is null) return Results.NotFound();

            if (loc.TrackingMode == TrackingModes.Count)
            {
                var qty = req.Quantity ?? item.Quantity ?? 1;
                if (qty <= 0 || qty > 99_999) return Invalid("quantity", "Quantity must be more than 0.");
                await Stock.Adjust(db, product, loc, qty, StockChangeTypes.PutAway, item.Name, ct);
            }
            else
            {
                await Stock.Set(db, product, loc, null, StockStatuses.Have, item.Name, ct, StockChangeTypes.PutAway);
            }
            return await Reply(db, product, notify, ct);
        });

        // ---- History ----

        hh.MapGet("/activity", async (int? take, AppDbContext db, CancellationToken ct) =>
        {
            var n = Math.Clamp(take ?? 50, 1, 200);
            var rows = await db.StockChanges.AsNoTracking()
                .OrderByDescending(c => c.StockChangeID).Take(n)
                .Select(c => new ActivityDto(
                    c.StockLevel!.Product!.Proxy, c.StockLevel.Product.Name, c.StockLevel.Location!.Name,
                    c.ChangeType, c.QuantityBefore, c.QuantityAfter, c.StatusAfter, c.Note,
                    db.Members.Where(m => m.HouseholdId == c.HouseholdId && m.UserId == c.CreatedById).Select(m => m.DisplayName).FirstOrDefault(),
                    c.CreatedDate))
                .ToListAsync(ct);
            return Results.Ok(rows);
        });
    }

    // ---- Helpers ----

    private static IQueryable<LocationDto> LocationsQuery(AppDbContext db) =>
        db.Locations.AsNoTracking().Where(l => !l.IsDeleted).OrderBy(l => l.SortOrder).ThenBy(l => l.Name)
            .Select(l => new LocationDto(l.Proxy, l.Name, l.TrackingMode, l.SortOrder));

    private static LocationDto ToDto(Location l) => new(l.Proxy, l.Name, l.TrackingMode, l.SortOrder);

    private static Task<Location?> FindLocation(AppDbContext db, string? proxy, CancellationToken ct) =>
        ProxyId.IsValid(proxy, Location.Prefix)
            ? db.Locations.FirstOrDefaultAsync(l => l.Proxy == proxy && !l.IsDeleted, ct)
            : Task.FromResult<Location?>(null);

    private static Task<Product?> FindProduct(AppDbContext db, string? proxy, CancellationToken ct) =>
        ProxyId.IsValid(proxy, Product.Prefix)
            ? db.Products.FirstOrDefaultAsync(p => p.Proxy == proxy && !p.IsDeleted, ct)
            : Task.FromResult<Product?>(null);

    private static async Task<(Product?, Location?, IResult?)> FindPair(AppDbContext db, string productId, string locationId, CancellationToken ct)
    {
        var product = await FindProduct(db, productId, ct);
        var loc = await FindLocation(db, locationId, ct);
        return product is null || loc is null ? (null, null, Results.NotFound()) : (product, loc, null);
    }

    private static async Task<IResult> Reply(AppDbContext db, Product product, PantryNotifier notify, CancellationToken ct)
    {
        var dto = (await LoadOne(db, product.Proxy, ct))!;
        await notify.ProductUpserted(dto, ct);
        return Results.Ok(dto);
    }

    private static async Task SoftDeleteStock(AppDbContext db, System.Linq.Expressions.Expression<Func<StockLevel, bool>> where, TenantContext tenant, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var userId = tenant.UserId;
        await db.StockLevels.Where(where).Where(s => !s.IsDeleted).ExecuteUpdateAsync(u => u
            .SetProperty(s => s.IsDeleted, true)
            .SetProperty(s => s.ModifiedDate, now)
            .SetProperty(s => s.ModifiedById, userId)
            .SetProperty(s => s.DEX_ROW_TS, now.UtcDateTime), ct);
    }

    private static async Task<HashSet<Guid>> OpenListProductIds(AppDbContext db, CancellationToken ct) =>
        (await db.GroceryItems.AsNoTracking()
            .Where(i => !i.IsChecked && i.ProductId != null && db.GroceryLists.Any(l => l.Id == i.ListId && l.ArchivedAt == null))
            .Select(i => i.ProductId!.Value).Distinct().ToListAsync(ct)).ToHashSet();

    internal static async Task<ProductDto?> LoadOne(AppDbContext db, string proxy, CancellationToken ct) =>
        (await Load(db, db.Products.AsNoTracking().Where(p => p.Proxy == proxy && !p.IsDeleted), ct)).FirstOrDefault();

    /// <summary>Products with their stock in every (non-deleted) location, and the restock math.</summary>
    internal static async Task<List<ProductDto>> Load(AppDbContext db, IQueryable<Product> products, CancellationToken ct)
    {
        var rows = await products.OrderBy(p => p.Name).Select(p => new
        {
            p,
            StoreName = db.Stores.Where(s => s.Id == p.StoreId).Select(s => s.Name).FirstOrDefault(),
            Stock = p.StockLevels.Where(s => !s.IsDeleted && !s.Location!.IsDeleted)
                .OrderBy(s => s.Location!.SortOrder)
                .Select(s => new StockDto(s.Location!.Proxy, s.Location.Name, s.Location.TrackingMode, s.Quantity, s.Status,
                    s.ModifiedDate ?? s.CreatedDate))
                .ToList(),
        }).ToListAsync(ct);

        return rows.Select(r => ToDto(r.p, r.StoreName, r.Stock)).ToList();
    }

    internal static ProductDto ToDto(Product p, string? storeName, List<StockDto> stock)
    {
        var onHand = stock.Where(s => s.TrackingMode == TrackingModes.Count).Sum(s => s.Quantity);
        var belowMin = p.MinQuantity is { } min && onHand < min;
        var stapleLow = p.IsStaple && stock.Any(s => s.TrackingMode == TrackingModes.Status && s.Status is StockStatuses.Low or StockStatuses.Out);
        decimal? suggested = null;
        if (belowMin) suggested = Math.Max((p.TargetQuantity ?? p.MinQuantity!.Value) - onHand, 0);
        else if (stapleLow) suggested = 1;
        if (suggested == 0) suggested = null;

        return new ProductDto(p.Proxy, p.ProxyGuid, p.Name, p.Barcode, p.Category, p.Unit, p.MinQuantity, p.TargetQuantity,
            p.IsStaple, p.StoreId, storeName, p.ImageUrl, onHand, belowMin || stapleLow, suggested, stock);
    }

    private static async Task<IResult?> Apply(Product product, ProductRequest req, AppDbContext db, CancellationToken ct)
    {
        var name = Clean(req.Name, 120);
        if (name is null) return Invalid("name", "Product name is required (120 characters max).");
        var barcode = Clean(req.Barcode, 32);
        if (req.Barcode is not null && barcode is null && !string.IsNullOrWhiteSpace(req.Barcode)) return Invalid("barcode", "Barcode is 32 characters max.");
        if (barcode is not null && await db.Products.AnyAsync(p => !p.IsDeleted && p.Barcode == barcode && p.ProductID != product.ProductID, ct))
            return Results.Problem(statusCode: 409, title: "Another product already has this barcode.");
        if (req.MinQuantity is < 0 or > 99_999_999) return Invalid("minQuantity", "Minimum must be 0 or more.");
        if (req.TargetQuantity is { } target && (target < (req.MinQuantity ?? 0) || target > 99_999_999))
            return Invalid("targetQuantity", "Target must be at least the minimum.");
        if (req.StoreId is { } storeId && !await db.Stores.AnyAsync(s => s.Id == storeId, ct)) return Invalid("storeId", "Unknown store.");
        var imageUrl = Clean(req.ImageUrl, 500);
        if (imageUrl is not null && !(Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps))
            return Invalid("imageUrl", "Image URL must be an https link.");

        product.Name = name;
        product.Barcode = barcode;
        product.Category = Clean(req.Category, 60);
        product.Unit = Clean(req.Unit, 20);
        product.MinQuantity = req.MinQuantity;
        product.TargetQuantity = req.TargetQuantity;
        product.IsStaple = req.IsStaple ?? false;
        product.StoreId = req.StoreId;
        product.ImageUrl = imageUrl;
        return null;
    }

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    private static string? Clean(string? value, int max)
    {
        var v = value?.Trim();
        return string.IsNullOrEmpty(v) || v.Length > max ? null : v;
    }
}

/// <summary>Stock writes, each recorded in PAN.StockChanges.</summary>
internal static class Stock
{
    /// <summary>Adds <paramref name="delta"/> atomically in SQL (never below 0), creating the row when missing.</summary>
    public static async Task Adjust(AppDbContext db, Product product, Location loc, decimal delta, string changeType, string? note, CancellationToken ct)
    {
        var level = await GetOrCreate(db, product, loc, ct);
        var before = await db.StockLevels.AsNoTracking().Where(s => s.StockLevelID == level.StockLevelID).Select(s => s.Quantity).FirstAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var userId = db.CurrentUser;
        await db.StockLevels.Where(s => s.StockLevelID == level.StockLevelID).ExecuteUpdateAsync(u => u
            .SetProperty(s => s.Quantity, s => s.Quantity + delta < 0 ? 0 : s.Quantity + delta)
            .SetProperty(s => s.ModifiedDate, now)
            .SetProperty(s => s.ModifiedById, userId)
            .SetProperty(s => s.DEX_ROW_TS, now.UtcDateTime), ct);
        var after = await db.StockLevels.AsNoTracking().Where(s => s.StockLevelID == level.StockLevelID).Select(s => s.Quantity).FirstAsync(ct);
        db.StockChanges.Add(new StockChange
        {
            StockLevelId = level.StockLevelID, ChangeType = changeType,
            QuantityBefore = before, QuantityAfter = after, Note = note,
        });
        await db.SaveChangesAsync(ct);
    }

    public static async Task Set(AppDbContext db, Product product, Location loc, decimal? quantity, string? status, string? note,
        CancellationToken ct, string? changeType = null)
    {
        var level = await GetOrCreate(db, product, loc, ct);
        var before = level.Quantity;
        if (quantity is { } q) level.Quantity = q;
        if (status is not null) level.Status = status;
        db.StockChanges.Add(new StockChange
        {
            StockLevelId = level.StockLevelID,
            ChangeType = changeType ?? (status is not null ? StockChangeTypes.Status : StockChangeTypes.Set),
            QuantityBefore = quantity is null ? null : before, QuantityAfter = quantity, StatusAfter = status, Note = note,
        });
        await db.SaveChangesAsync(ct);
    }

    private static async Task<StockLevel> GetOrCreate(AppDbContext db, Product product, Location loc, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var level = await db.StockLevels.FirstOrDefaultAsync(s => s.ProductId == product.ProductID && s.LocationId == loc.LocationID && !s.IsDeleted, ct);
            if (level is not null) return level;
            level = new StockLevel { ProductId = product.ProductID, LocationId = loc.LocationID };
            ProxyId.Assign(level, StockLevel.Prefix, DateTime.UtcNow);
            db.StockLevels.Add(level);
            try
            {
                await db.SaveChangesAsync(ct);
                return level;
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                // Someone else created it at the same moment (UQ_StockLevels_ProductId_LocationId): use theirs.
                db.Entry(level).State = EntityState.Detached;
            }
        }
    }
}

/// <summary>Sends food storage changes to everyone watching the current household (see HouseholdHub).</summary>
public class PantryNotifier(IHubContext<HouseholdHub> hub, TenantContext tenant)
{
    private IClientProxy Group => hub.Clients.Group(HouseholdHub.Group(tenant.HouseholdId!.Value));

    public Task LocationsChanged(CancellationToken ct) => Group.SendAsync("pantryLocationsChanged", ct);
    public Task ProductsChanged(CancellationToken ct) => Group.SendAsync("pantryProductsChanged", ct);
    public Task ProductUpserted(ProductDto product, CancellationToken ct) => Group.SendAsync("pantryProductUpserted", product, ct);
    public Task ProductDeleted(string productId, CancellationToken ct) => Group.SendAsync("pantryProductDeleted", productId, ct);
}
