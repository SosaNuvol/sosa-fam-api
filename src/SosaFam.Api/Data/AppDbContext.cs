using System.Linq.Expressions;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SosaFam.Api.Tenancy;

namespace SosaFam.Api.Data;

/// <summary>
/// The schema is owned by the sosa-fam-database repository (DbUp scripts). This context
/// only maps to it: never call EnsureCreated or EF migrations against a real database.
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options, TenantContext tenant)
    : IdentityUserContext<AppUser, Guid>(options)
{
    // Read by the query filters below, per query, so each request sees its own household.
    private Guid? CurrentHouseholdId => tenant.HouseholdId;
    private Guid? CurrentUserId => tenant.UserId;
    private bool IsSystem => tenant.IsSystem;

    public DbSet<Household> Households => Set<Household>();
    public DbSet<Member> Members => Set<Member>();
    public DbSet<Invite> Invites => Set<Invite>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

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

        // Every other household-scoped entity gets the standard household filter.
        foreach (var type in b.Model.GetEntityTypes())
        {
            if (!typeof(IHouseholdScoped).IsAssignableFrom(type.ClrType)) continue;
            if (type.ClrType == typeof(Member)) continue; // custom filter above
            b.Entity(type.ClrType).HasQueryFilter(BuildHouseholdFilter(type.ClrType));
        }
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

    /// <summary>New household-scoped rows default to the current household, and
    /// outside system scope may not name a different one.</summary>
    private void StampHousehold()
    {
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
