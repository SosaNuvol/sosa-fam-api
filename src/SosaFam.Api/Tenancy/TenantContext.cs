namespace SosaFam.Api.Tenancy;

/// <summary>
/// Who is calling and which household they are acting in, for the current request.
/// The values are copied into SQL Server's SESSION_CONTEXT every time a connection opens,
/// where the row-level security policy reads them.
/// </summary>
public class TenantContext
{
    public Guid? UserId { get; set; }
    public Guid? HouseholdId { get; private set; }
    public Guid? MemberId { get; private set; }
    public string? Role { get; private set; }
    public bool IsSystem { get; private set; }

    public void EnterHousehold(Guid householdId, Guid memberId, string role)
    {
        HouseholdId = householdId;
        MemberId = memberId;
        Role = role;
    }

    /// <summary>
    /// Lets one operation cross households (creating a household, accepting an invite).
    /// Only for code paths that do their own checks. Dispose to leave system scope.
    /// </summary>
    public IDisposable BeginSystemScope()
    {
        if (IsSystem) throw new InvalidOperationException("Already in system scope.");
        IsSystem = true;
        return new Scope(this);
    }

    private sealed class Scope(TenantContext owner) : IDisposable
    {
        public void Dispose() => owner.IsSystem = false;
    }
}
