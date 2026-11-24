using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SosaFam.Api.Data;
using SosaFam.Api.Tenancy;

namespace SosaFam.Api.Realtime;

/// <summary>
/// Live updates for a household, at /hubs/household. A client connects with its access token
/// (the access_token query string, as SignalR clients send it), then calls
/// JoinHousehold(householdId) for each household screen it shows.
///
/// Events sent to the household group:
///   groceryListsChanged()
///   groceryItemUpserted(listId, item)
///   groceryItemDeleted(listId, itemId)
/// Other apps add their own events through IHubContext&lt;HouseholdHub&gt; and HouseholdHub.Group(id).
/// </summary>
[Authorize]
public class HouseholdHub(AppDbContext db, TenantContext tenant) : Hub
{
    public static string Group(Guid householdId) => $"household:{householdId}";

    public async Task JoinHousehold(Guid householdId)
    {
        if (!Guid.TryParse(Context.UserIdentifier, out var userId))
            throw new HubException("Not signed in.");
        tenant.UserId = userId;

        var isMember = await db.Members.AsNoTracking()
            .AnyAsync(m => m.HouseholdId == householdId && m.UserId == userId, Context.ConnectionAborted);
        if (!isMember) throw new HubException("Household not found.");

        await Groups.AddToGroupAsync(Context.ConnectionId, Group(householdId), Context.ConnectionAborted);
    }

    public Task LeaveHousehold(Guid householdId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(householdId));
}

/// <summary>SignalR's user id is the token's "sub" claim (the user id).</summary>
public class SubUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) => connection.User.FindFirst("sub")?.Value;
}
