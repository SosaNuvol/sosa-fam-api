using System.Linq.Expressions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SosaFam.Api.Data;
using SosaFam.Api.Realtime;
using SosaFam.Api.Tenancy;

namespace SosaFam.Api.Grocery;

public record StoreDto(Guid Id, string Name);
public record StoreRequest(string Name);

public record GroceryListDto(Guid Id, string Name, DateTime CreatedAt, bool Archived, int ItemCount, int OpenCount);
public record CreateListRequest(string Name);
public record UpdateListRequest(string? Name, bool? Archived);

public record GroceryItemDto(
    Guid Id, Guid ListId, string Name, decimal? Quantity, string? Unit, string? Note,
    Guid? StoreId, string? StoreName, string Source, Guid? ProductId,
    bool IsChecked, string? CheckedByName, DateTime? CheckedAt, string? AddedByName, DateTime CreatedAt);
public record GroceryListDetailDto(GroceryListDto List, IReadOnlyList<GroceryItemDto> Items);

/// <summary>Create or replace an item's details. Omitted fields are cleared, except Source/ProductId on update.</summary>
public record ItemRequest(string Name, decimal? Quantity, string? Unit, string? Note, Guid? StoreId, string? Source, Guid? ProductId);
public record CheckRequest(bool IsChecked);

public static class GroceryEndpoints
{
    public static void MapGroceryEndpoints(this IEndpointRouteBuilder app)
    {
        var hh = app.MapHouseholdGroup().WithTags("Grocery");

        // ---- Stores (shared by every app in the household) ----

        hh.MapGet("/stores", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Stores.AsNoTracking().OrderBy(s => s.Name).Select(s => new StoreDto(s.Id, s.Name)).ToListAsync(ct)));

        hh.MapPost("/stores", async (StoreRequest req, AppDbContext db, CancellationToken ct) =>
        {
            var name = Clean(req.Name, 60);
            if (name is null) return Invalid("name", "Store name is required (60 characters max).");
            if (await db.Stores.AnyAsync(s => s.Name == name, ct)) return Results.Problem(statusCode: 409, title: $"{name} is already a store.");
            var store = new Store { Id = Guid.NewGuid(), Name = name };
            db.Stores.Add(store);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new StoreDto(store.Id, store.Name));
        });

        hh.MapPatch("/stores/{storeId:guid}", async (Guid storeId, StoreRequest req, AppDbContext db, CancellationToken ct) =>
        {
            var name = Clean(req.Name, 60);
            if (name is null) return Invalid("name", "Store name is required (60 characters max).");
            var store = await db.Stores.FirstOrDefaultAsync(s => s.Id == storeId, ct);
            if (store is null) return Results.NotFound();
            if (await db.Stores.AnyAsync(s => s.Name == name && s.Id != storeId, ct)) return Results.Problem(statusCode: 409, title: $"{name} is already a store.");
            store.Name = name;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new StoreDto(store.Id, store.Name));
        });

        hh.MapDelete("/stores/{storeId:guid}", async (Guid storeId, TenantContext tenant, AppDbContext db, Notifier notify, CancellationToken ct) =>
        {
            if (tenant.RequireRole(Roles.Owner, Roles.Adult) is { } denied) return denied;
            if (!await db.Stores.AnyAsync(s => s.Id == storeId, ct)) return Results.NotFound();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // Items at this store become "any store".
            await db.GroceryItems.Where(i => i.StoreId == storeId).ExecuteUpdateAsync(u => u.SetProperty(i => i.StoreId, (Guid?)null), ct);
            // So do pantry products that preferred it, deleted ones too (FK_Products_Stores is NO ACTION).
            await db.Products.Where(p => p.StoreId == storeId)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.StoreId, (Guid?)null), ct);
            await db.Stores.Where(s => s.Id == storeId).ExecuteDeleteAsync(ct);
            await tx.CommitAsync(ct);
            await notify.ListsChanged(ct);
            return Results.NoContent();
        });

        // ---- Lists ----

        hh.MapGet("/grocery-lists", async (bool? archived, AppDbContext db, CancellationToken ct) =>
        {
            var showArchived = archived ?? false;
            return Results.Ok(await ListQuery(db, l => (l.ArchivedAt != null) == showArchived).ToListAsync(ct));
        });

        hh.MapPost("/grocery-lists", async (CreateListRequest req, TenantContext tenant, AppDbContext db, Notifier notify, CancellationToken ct) =>
        {
            var name = Clean(req.Name, 100);
            if (name is null) return Invalid("name", "List name is required (100 characters max).");
            var list = new GroceryList { Id = Guid.NewGuid(), Name = name, CreatedByUserId = tenant.UserId!.Value };
            db.GroceryLists.Add(list);
            await db.SaveChangesAsync(ct);
            await notify.ListsChanged(ct);
            return Results.Created($"/households/{tenant.HouseholdId}/grocery-lists/{list.Id}",
                await ListQuery(db, l => l.Id == list.Id).FirstAsync(ct));
        });

        hh.MapGet("/grocery-lists/{listId:guid}", async (Guid listId, AppDbContext db, CancellationToken ct) =>
        {
            var list = await ListQuery(db, l => l.Id == listId).FirstOrDefaultAsync(ct);
            if (list is null) return Results.NotFound();
            var items = (await ItemQuery(db, i => i.ListId == listId).ToListAsync(ct))
                .OrderBy(i => i.IsChecked).ThenBy(i => i.StoreName ?? "\uffff").ThenBy(i => i.CreatedAt).ToList();
            return Results.Ok(new GroceryListDetailDto(list, items));
        });

        hh.MapPatch("/grocery-lists/{listId:guid}", async (Guid listId, UpdateListRequest req, TenantContext tenant, AppDbContext db, Notifier notify, CancellationToken ct) =>
        {
            var list = await db.GroceryLists.FirstOrDefaultAsync(l => l.Id == listId, ct);
            if (list is null) return Results.NotFound();
            if (req.Name is not null)
            {
                var name = Clean(req.Name, 100);
                if (name is null) return Invalid("name", "List name is required (100 characters max).");
                list.Name = name;
            }
            if (req.Archived is { } archived) list.ArchivedAt = archived ? list.ArchivedAt ?? DateTime.UtcNow : null;
            await db.SaveChangesAsync(ct);
            await notify.ListsChanged(ct);
            return Results.Ok(await ListQuery(db, l => l.Id == listId).FirstAsync(ct));
        });

        hh.MapDelete("/grocery-lists/{listId:guid}", async (Guid listId, TenantContext tenant, AppDbContext db, Notifier notify, CancellationToken ct) =>
        {
            var list = await db.GroceryLists.FirstOrDefaultAsync(l => l.Id == listId, ct);
            if (list is null) return Results.NotFound();
            if (list.CreatedByUserId != tenant.UserId && tenant.RequireRole(Roles.Owner, Roles.Adult) is { } denied) return denied;
            db.GroceryLists.Remove(list);
            await db.SaveChangesAsync(ct);
            await notify.ListsChanged(ct);
            return Results.NoContent();
        });

        hh.MapPost("/grocery-lists/{listId:guid}/clear-checked", async (Guid listId, AppDbContext db, Notifier notify, CancellationToken ct) =>
        {
            if (!await db.GroceryLists.AnyAsync(l => l.Id == listId, ct)) return Results.NotFound();
            var ids = await db.GroceryItems.Where(i => i.ListId == listId && i.IsChecked).Select(i => i.Id).ToListAsync(ct);
            await db.GroceryItems.Where(i => ids.Contains(i.Id)).ExecuteDeleteAsync(ct);
            foreach (var id in ids) await notify.ItemDeleted(listId, id, ct);
            await notify.ListsChanged(ct);
            return Results.NoContent();
        });

        // ---- Items ----

        hh.MapPost("/grocery-lists/{listId:guid}/items", async (Guid listId, ItemRequest req, TenantContext tenant, AppDbContext db, Notifier notify, CancellationToken ct) =>
        {
            if (!await db.GroceryLists.AnyAsync(l => l.Id == listId, ct)) return Results.NotFound();
            var item = new GroceryItem { Id = Guid.NewGuid(), ListId = listId, AddedByUserId = tenant.UserId!.Value };
            if (await Apply(item, req, db, isNew: true, ct) is { } invalid) return invalid;
            db.GroceryItems.Add(item);
            await db.SaveChangesAsync(ct);
            var dto = await ItemQuery(db, i => i.Id == item.Id).FirstAsync(ct);
            await notify.ItemUpserted(dto, ct);
            return Results.Ok(dto);
        });

        hh.MapPut("/grocery-lists/{listId:guid}/items/{itemId:guid}", async (Guid listId, Guid itemId, ItemRequest req, AppDbContext db, Notifier notify, CancellationToken ct) =>
        {
            var item = await db.GroceryItems.FirstOrDefaultAsync(i => i.Id == itemId && i.ListId == listId, ct);
            if (item is null) return Results.NotFound();
            if (await Apply(item, req, db, isNew: false, ct) is { } invalid) return invalid;
            await db.SaveChangesAsync(ct);
            var dto = await ItemQuery(db, i => i.Id == item.Id).FirstAsync(ct);
            await notify.ItemUpserted(dto, ct);
            return Results.Ok(dto);
        });

        // The checkbox. Anyone in the family can check items off, children included.
        hh.MapPut("/grocery-lists/{listId:guid}/items/{itemId:guid}/checked", async (Guid listId, Guid itemId, CheckRequest req, TenantContext tenant, AppDbContext db, Notifier notify, CancellationToken ct) =>
        {
            var item = await db.GroceryItems.FirstOrDefaultAsync(i => i.Id == itemId && i.ListId == listId, ct);
            if (item is null) return Results.NotFound();
            if (item.IsChecked != req.IsChecked)
            {
                item.IsChecked = req.IsChecked;
                item.CheckedByUserId = req.IsChecked ? tenant.UserId : null;
                item.CheckedAt = req.IsChecked ? DateTime.UtcNow : null;
                await db.SaveChangesAsync(ct);
            }
            var dto = await ItemQuery(db, i => i.Id == item.Id).FirstAsync(ct);
            await notify.ItemUpserted(dto, ct);
            return Results.Ok(dto);
        });

        hh.MapDelete("/grocery-lists/{listId:guid}/items/{itemId:guid}", async (Guid listId, Guid itemId, AppDbContext db, Notifier notify, CancellationToken ct) =>
        {
            var deleted = await db.GroceryItems.Where(i => i.Id == itemId && i.ListId == listId).ExecuteDeleteAsync(ct);
            if (deleted == 0) return Results.NotFound();
            await notify.ItemDeleted(listId, itemId, ct);
            return Results.NoContent();
        });
    }

    private static async Task<IResult?> Apply(GroceryItem item, ItemRequest req, AppDbContext db, bool isNew, CancellationToken ct)
    {
        var name = Clean(req.Name, 120);
        if (name is null) return Invalid("name", "Item name is required (120 characters max).");
        if (req.Quantity is <= 0 or > 99_999_999) return Invalid("quantity", "Quantity must be more than 0.");
        if (req.StoreId is { } storeId && !await db.Stores.AnyAsync(s => s.Id == storeId, ct)) return Invalid("storeId", "Unknown store.");
        if (req.Source is not null && !GrocerySources.All.Contains(req.Source)) return Invalid("source", "Source must be Manual or LowStock.");

        item.Name = name;
        item.Quantity = req.Quantity;
        item.Unit = Clean(req.Unit, 20);
        item.Note = Clean(req.Note, 200);
        item.StoreId = req.StoreId;
        if (isNew || req.Source is not null) item.Source = req.Source ?? GrocerySources.Manual;
        if (isNew || req.ProductId is not null) item.ProductId = req.ProductId;
        return null;
    }

    private static IQueryable<GroceryListDto> ListQuery(AppDbContext db, Expression<Func<GroceryList, bool>> where) =>
        db.GroceryLists.AsNoTracking().Where(where).OrderByDescending(l => l.CreatedAt).Select(l => new GroceryListDto(
            l.Id, l.Name, l.CreatedAt, l.ArchivedAt != null,
            db.GroceryItems.Count(i => i.ListId == l.Id),
            db.GroceryItems.Count(i => i.ListId == l.Id && !i.IsChecked)));

    internal static IQueryable<GroceryItemDto> ItemQuery(AppDbContext db, Expression<Func<GroceryItem, bool>> where) =>
        db.GroceryItems.AsNoTracking().Where(where).Select(i => new GroceryItemDto(
            i.Id, i.ListId, i.Name, i.Quantity, i.Unit, i.Note,
            i.StoreId, db.Stores.Where(s => s.Id == i.StoreId).Select(s => s.Name).FirstOrDefault(),
            i.Source, i.ProductId, i.IsChecked,
            db.Members.Where(m => m.HouseholdId == i.HouseholdId && m.UserId == i.CheckedByUserId).Select(m => m.DisplayName).FirstOrDefault(),
            i.CheckedAt,
            db.Members.Where(m => m.HouseholdId == i.HouseholdId && m.UserId == i.AddedByUserId).Select(m => m.DisplayName).FirstOrDefault(),
            i.CreatedAt));

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    private static string? Clean(string? value, int max)
    {
        var v = value?.Trim();
        return string.IsNullOrEmpty(v) || v.Length > max ? null : v;
    }
}

/// <summary>Sends grocery changes to everyone watching the current household.</summary>
public class Notifier(IHubContext<HouseholdHub> hub, TenantContext tenant)
{
    private IClientProxy Group => hub.Clients.Group(HouseholdHub.Group(tenant.HouseholdId!.Value));

    public Task ListsChanged(CancellationToken ct) => Group.SendAsync("groceryListsChanged", ct);
    public Task ItemUpserted(GroceryItemDto item, CancellationToken ct) => Group.SendAsync("groceryItemUpserted", item.ListId, item, ct);
    public Task ItemDeleted(Guid listId, Guid itemId, CancellationToken ct) => Group.SendAsync("groceryItemDeleted", listId, itemId, ct);
}
