using Microsoft.AspNetCore.Identity;

namespace SosaFam.Api.Data;

/// <summary>A person's login. One user can belong to several households.</summary>
public class AppUser : IdentityUser<Guid>
{
    public string? DisplayName { get; set; }
}

/// <summary>Marks a table that belongs to one household. The DbContext filters these
/// by the current household, and SQL Server row-level security enforces it again.</summary>
public interface IHouseholdScoped
{
    Guid HouseholdId { get; set; }
}

public class Household
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public Guid CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public static class Roles
{
    public const string Owner = "Owner";
    public const string Adult = "Adult";
    public const string Member = "Member";
    public const string Child = "Child";

    public static readonly string[] All = [Owner, Adult, Member, Child];
    public static readonly string[] Invitable = [Adult, Member, Child];

    public static bool CanManageMembers(string role) => role is Owner or Adult;
}

public class Member : IHouseholdScoped
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    public Guid UserId { get; set; }
    public string Role { get; set; } = Roles.Member;
    public string DisplayName { get; set; } = "";
    public DateTime JoinedAt { get; set; }
}

public class Invite : IHouseholdScoped
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    public byte[] CodeHash { get; set; } = [];
    public string Role { get; set; } = Roles.Member;
    public Guid CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? AcceptedAt { get; set; }
    public Guid? AcceptedByUserId { get; set; }
    public DateTime? RevokedAt { get; set; }
}

public class RefreshToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public byte[] TokenHash { get; set; } = [];
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}
