using System.Linq.Expressions;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SosaFam.Api.Tenancy;

namespace SosaFam.Api.Data;

/// <summary>
/// The schema is owned by the sosa-fam-database repository (migrations in its Migrations/ folder). This context
/// only maps to it: never call EnsureCreated or EF migrations against a real database.
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options, TenantContext tenant)
    : IdentityUserContext<AppUser, Guid>(options)
{
    // Read by the query filters below, per query, so each request sees its own household.
    private Guid? CurrentHouseholdId => tenant.HouseholdId;
    private Guid? CurrentUserId => tenant.UserId;
    /// <summary>The signed-in user, for audit columns set by bulk updates.</summary>
    public Guid? CurrentUser => tenant.UserId;
    private bool IsSystem => tenant.IsSystem;

    public DbSet<Household> Households => Set<Household>();
    public DbSet<Member> Members => Set<Member>();
    public DbSet<Invite> Invites => Set<Invite>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Store> Stores => Set<Store>();
    public DbSet<GroceryList> GroceryLists => Set<GroceryList>();
    public DbSet<GroceryItem> GroceryItems => Set<GroceryItem>();

    // Food storage app (PAN schema)
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<StockLevel> StockLevels => Set<StockLevel>();
    public DbSet<StockChange> StockChanges => Set<StockChange>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        b.HasDefaultSchema("auth");

        b.Entity<AppUser>().Property(u => u.DisplayName).HasMaxLength(100);

        b.Entity<RefreshToken>(e =>
        {
            e.ToTable("RefreshTokens", "auth");
            e.Property(x => x.Id).HasDefaultValueSql("NEWSEQUENTIALID()");
            e.Property(x => x.TokenHash).HasColumnType("binary(32)");
            e.Property(x => x.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
        });

        b.Entity<Household>(e =>
        {
            e.ToTable("Households", "core");
            e.Property(x => x.Id).HasDefaultValueSql("NEWSEQUENTIALID()");
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
            // Visible: the current household. Listing all of a user's households goes through
            // their memberships and IgnoreQueryFilters; SQL Server RLS still applies there.
            e.HasQueryFilter(h => IsSystem || h.Id == CurrentHouseholdId);
        });

        b.Entity<Member>(e =>
        {
            e.ToTable("Members", "core");
            e.Property(x => x.Id).HasDefaultValueSql("NEWSEQUENTIALID()");
            e.Property(x => x.Role).HasMaxLength(10);
            e.Property(x => x.DisplayName).HasMaxLength(100);
            e.Property(x => x.JoinedAt).HasDefaultValueSql("SYSUTCDATETIME()");
            // Visible: the current household's members, plus your own memberships.
            e.HasQueryFilter(m => IsSystem || m.HouseholdId == CurrentHouseholdId || m.UserId == CurrentUserId);
        });

        b.Entity<Invite>(e =>
        {
            e.ToTable("Invites", "core");
            e.Property(x => x.Id).HasDefaultValueSql("NEWSEQUENTIALID()");
            e.Property(x => x.CodeHash).HasColumnType("binary(32)");
            e.Property(x => x.Role).HasMaxLength(10);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
        });

        b.Entity<Store>(e =>
        {
            e.ToTable("Stores", "core");
            e.Property(x => x.Id).HasDefaultValueSql("NEWSEQUENTIALID()");
            e.Property(x => x.Name).HasMaxLength(60);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
        });

        b.Entity<GroceryList>(e =>
        {
            e.ToTable("GroceryLists", "family");
            e.Property(x => x.Id).HasDefaultValueSql("NEWSEQUENTIALID()");
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
        });

        b.Entity<GroceryItem>(e =>
        {
            e.ToTable("GroceryItems", "family");
            e.Property(x => x.Id).HasDefaultValueSql("NEWSEQUENTIALID()");
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.Quantity).HasPrecision(10, 2);
            e.Property(x => x.Unit).HasMaxLength(20);
            e.Property(x => x.Note).HasMaxLength(200);
            e.Property(x => x.Source).HasMaxLength(10);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
        });

        MapPantry(b);

        // Every other household-scoped entity gets the standard household filter.
        foreach (var type in b.Model.GetEntityTypes())
        {
            if (!typeof(IHouseholdScoped).IsAssignableFrom(type.ClrType)) continue;
            if (type.ClrType == typeof(Member)) continue; // custom filter above
            b.Entity(type.ClrType).HasQueryFilter(BuildHouseholdFilter(type.ClrType));
        }
    }

    private static void MapPantry(ModelBuilder b)
    {
        b.Entity<Location>(e =>
        {
            e.ToTable("Locations", "PAN");
            e.HasKey(x => x.LocationID);
            e.Property(x => x.Proxy).HasMaxLength(100).IsUnicode(false);
            e.Property(x => x.PartKey).HasMaxLength(6).IsUnicode(false);
            e.Property(x => x.Name).HasMaxLength(60);
            e.Property(x => x.TrackingMode).HasMaxLength(10);
        });

        b.Entity<Product>(e =>
        {
            e.ToTable("Products", "PAN");
            e.HasKey(x => x.ProductID);
            e.Property(x => x.Proxy).HasMaxLength(100).IsUnicode(false);
            e.Property(x => x.PartKey).HasMaxLength(6).IsUnicode(false);
            e.Property(x => x.ProxyGuid).HasComputedColumnSql("CONVERT(UNIQUEIDENTIFIER, RIGHT([Proxy], 36))", stored: true);
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.Barcode).HasMaxLength(32);
            e.Property(x => x.Category).HasMaxLength(60);
            e.Property(x => x.Unit).HasMaxLength(20);
            e.Property(x => x.MinQuantity).HasPrecision(10, 2);
            e.Property(x => x.TargetQuantity).HasPrecision(10, 2);
            e.Property(x => x.ImageUrl).HasMaxLength(500);
            e.HasMany(x => x.StockLevels).WithOne(x => x.Product).HasForeignKey(x => x.ProductId);
        });

        b.Entity<StockLevel>(e =>
        {
            e.ToTable("StockLevels", "PAN");
            e.HasKey(x => x.StockLevelID);
            e.Property(x => x.Proxy).HasMaxLength(100).IsUnicode(false);
            e.Property(x => x.PartKey).HasMaxLength(6).IsUnicode(false);
            e.Property(x => x.Quantity).HasPrecision(10, 2);
            e.Property(x => x.Status).HasMaxLength(10);
            e.HasOne(x => x.Location).WithMany().HasForeignKey(x => x.LocationId);
        });

        b.Entity<StockChange>(e =>
        {
            e.ToTable("StockChanges", "PAN");
            e.HasKey(x => x.StockChangeID);
            e.Property(x => x.ChangeType).HasMaxLength(20);
            e.Property(x => x.QuantityBefore).HasPrecision(10, 2);
            e.Property(x => x.QuantityAfter).HasPrecision(10, 2);
            e.Property(x => x.StatusAfter).HasMaxLength(10);
            e.Property(x => x.Note).HasMaxLength(200);
            e.HasOne(x => x.StockLevel).WithMany().HasForeignKey(x => x.StockLevelId);
        });
    }

    private LambdaExpression BuildHouseholdFilter(Type entityType)
    {
        // e => IsSystem || e.HouseholdId == CurrentHouseholdId
        var e = Expression.Parameter(entityType, "e");
        var ctx = Expression.Constant(this);
        var isSystem = Expression.Property(ctx, nameof(IsSystem));
        var current = Expression.Property(ctx, nameof(CurrentHouseholdId));
        var householdId = Expression.Convert(Expression.Property(e, nameof(IHouseholdScoped.HouseholdId)), typeof(Guid?));
        return Expression.Lambda(Expression.OrElse(isSystem, Expression.Equal(householdId, current)), e);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampHousehold();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        StampHousehold();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
    }

    /// <summary>Fills the audit tail (DATABASE_CONSTITUTION.md, Article VI) for tracked changes.
    /// Bulk updates (ExecuteUpdate) set ModifiedDate, ModifiedById and DEX_ROW_TS themselves.</summary>
    private void StampAudit()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in ChangeTracker.Entries<IAudited>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedDate = now;
                entry.Entity.DEX_ROW_TS = now.UtcDateTime;
                if (entry.Entity.CreatedById == Guid.Empty)
                    entry.Entity.CreatedById = CurrentUserId ?? throw new InvalidOperationException("Audited rows need a signed-in user.");
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.ModifiedDate = now;
                entry.Entity.ModifiedById = CurrentUserId;
                entry.Entity.DEX_ROW_TS = now.UtcDateTime;
            }
        }
    }

    /// <summary>New household-scoped rows default to the current household, and
    /// outside system scope may not name a different one.</summary>
    private void StampHousehold()
    {
        StampAudit();
        foreach (var entry in ChangeTracker.Entries<IHouseholdScoped>())
        {
            if (entry.State != EntityState.Added && entry.State != EntityState.Modified) continue;
            if (entry.State == EntityState.Added && entry.Entity.HouseholdId == Guid.Empty && CurrentHouseholdId is { } hid)
                entry.Entity.HouseholdId = hid;
            if (!IsSystem && entry.Entity.HouseholdId != CurrentHouseholdId)
                throw new InvalidOperationException("Attempted to write a row outside the current household.");
        }
    }
}
