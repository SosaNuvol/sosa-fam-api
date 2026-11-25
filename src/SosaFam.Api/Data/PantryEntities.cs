namespace SosaFam.Api.Data;

// Food storage app (PAN schema in sosa-fam-database). These tables follow the database
// standards: INT keys that never leave the API, a Proxy-ID that does, and the audit tail.

/// <summary>Rows with the standard audit tail. AppDbContext fills it on save.</summary>
public interface IAudited
{
    bool IsActive { get; set; }
    bool IsDeleted { get; set; }
    DateTimeOffset? ModifiedDate { get; set; }
    Guid? ModifiedById { get; set; }
    DateTimeOffset CreatedDate { get; set; }
    Guid CreatedById { get; set; }
    DateTime DEX_ROW_TS { get; set; }
}

/// <summary>Rows the API exposes by Proxy-ID ({Prefix}:{PartKey}:{GUID}).</summary>
public interface IProxied
{
    string Proxy { get; set; }
    string PartKey { get; set; }
}

public abstract class PantryRow : IHouseholdScoped, IAudited
{
    public Guid HouseholdId { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsDeleted { get; set; }
    public DateTimeOffset? ModifiedDate { get; set; }
    public Guid? ModifiedById { get; set; }
    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedById { get; set; }
    public DateTime DEX_ROW_TS { get; set; }
}

public static class TrackingModes
{
    /// <summary>Exact quantities (pantry, basement).</summary>
    public const string Count = "Count";
    /// <summary>Have / Low / Out (fridge).</summary>
    public const string Status = "Status";
    public static readonly string[] All = [Count, Status];
}

public static class StockStatuses
{
    public const string Have = "Have";
    public const string Low = "Low";
    public const string Out = "Out";
    public static readonly string[] All = [Have, Low, Out];
}

public static class StockChangeTypes
{
    public const string Adjust = "Adjust";
    public const string Set = "Set";
    public const string MoveIn = "MoveIn";
    public const string MoveOut = "MoveOut";
    public const string PutAway = "PutAway";
    public const string Status = "Status";
}

/// <summary>PAN.Locations: Pantry, Fridge, Basement, ...</summary>
public class Location : PantryRow, IProxied
{
    public const string Prefix = "PALO";
    public int LocationID { get; set; }
    public string Proxy { get; set; } = "";
    public string PartKey { get; set; } = "";
    public string Name { get; set; } = "";
    public string TrackingMode { get; set; } = TrackingModes.Count;
    public int SortOrder { get; set; }
}

/// <summary>PAN.Products: a kind of food, with household-wide minimum and target quantities.</summary>
public class Product : PantryRow, IProxied
{
    public const string Prefix = "PAPR";
    public int ProductID { get; set; }
    /// <summary>The preferred store (core.Stores).</summary>
    public Guid? StoreId { get; set; }
    public string Proxy { get; set; } = "";
    public string PartKey { get; set; } = "";
    /// <summary>The GUID segment of Proxy (persisted computed column). family.GroceryItems.ProductId holds it.</summary>
    public Guid ProxyGuid { get; set; }
    public string Name { get; set; } = "";
    public string? Barcode { get; set; }
    public string? Category { get; set; }
    public string? Unit { get; set; }
    public decimal? MinQuantity { get; set; }
    public decimal? TargetQuantity { get; set; }
    public bool IsStaple { get; set; }
    public string? ImageUrl { get; set; }
    public List<StockLevel> StockLevels { get; set; } = [];
}

/// <summary>PAN.StockLevels: one product in one location.</summary>
public class StockLevel : PantryRow, IProxied
{
    public const string Prefix = "PAST";
    public int StockLevelID { get; set; }
    public int ProductId { get; set; }
    public int LocationId { get; set; }
    public string Proxy { get; set; } = "";
    public string PartKey { get; set; } = "";
    public decimal Quantity { get; set; }
    public string? Status { get; set; }
    public Product? Product { get; set; }
    public Location? Location { get; set; }
}

/// <summary>PAN.StockChanges: history of every stock change. Not exposed by id.</summary>
public class StockChange : PantryRow
{
    public int StockChangeID { get; set; }
    public int StockLevelId { get; set; }
    public string ChangeType { get; set; } = StockChangeTypes.Adjust;
    public decimal? QuantityBefore { get; set; }
    public decimal? QuantityAfter { get; set; }
    public string? StatusAfter { get; set; }
    public string? Note { get; set; }
    public StockLevel? StockLevel { get; set; }
}

/// <summary>Builds Proxy-IDs the same way MAC.fxGeneratePrefixProxy does (see the database repo's
/// .github/ProxyID-PrefixRegistry.md).</summary>
public static class ProxyId
{
    /// <summary>Hex of the UTC year and month: 2026-11 -> 202611 -> "31773" (UTL.fxGetPartKey).</summary>
    public static string PartKey(DateTime utcNow) => ((utcNow.Year * 100) + utcNow.Month).ToString("X8")[3..];

    public static void Assign(IProxied row, string prefix, DateTime utcNow)
    {
        row.PartKey = PartKey(utcNow);
        row.Proxy = $"{prefix}:{row.PartKey}:{Guid.NewGuid().ToString().ToUpperInvariant()}";
    }

    /// <summary>True when <paramref name="proxy"/> is well-formed and has the expected prefix.</summary>
    public static bool IsValid(string? proxy, string prefix) =>
        proxy is not null && proxy.Length <= 100 && proxy.StartsWith(prefix + ":", StringComparison.Ordinal)
        && proxy.Split(':') is [_, { Length: > 0 and <= 6 }, var g] && Guid.TryParse(g, out _);
}
