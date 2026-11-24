using Microsoft.EntityFrameworkCore;
using SosaFam.Api.Auth;
using SosaFam.Api.Data;
using SosaFam.Api.Tenancy;

namespace SosaFam.Api.Households;

public record CreateHouseholdRequest(string Name, string? DisplayName);
public record RenameHouseholdRequest(string Name);
public record HouseholdDto(Guid Id, string Name, DateTime CreatedAt, string MyRole);
public record MemberDto(Guid Id, Guid UserId, string DisplayName, string Role, DateTime JoinedAt);
public record UpdateMemberRequest(string? Role, string? DisplayName);
public record CreateInviteRequest(string Role, int? ExpiresInDays);
public record CreatedInviteDto(Guid Id, string Code, string Role, DateTime ExpiresAt);
public record InviteDto(Guid Id, string Role, DateTime CreatedAt, DateTime ExpiresAt);
public record AcceptInviteRequest(string Code, string? DisplayName);

public static class HouseholdEndpoints
{
    public const int DefaultInviteDays = 7;
    public const int MaxInviteDays = 30;

    public static void MapHouseholdEndpoints(this IEndpointRouteBuilder app)
    {
        var root = app.MapGroup("/households").RequireAuthorization().WithTags("Households");

        root.MapGet("/", async (TenantContext tenant, AppDbContext db, CancellationToken ct) =>
            Results.Ok(await AuthEndpoints.MyHouseholds(db, tenant.UserId!.Value, ct)));

        root.MapPost("/", async (CreateHouseholdRequest req, TenantContext tenant, AppDbContext db, CancellationToken ct) =>
        {
            var name = req.Name?.Trim() ?? "";
            if (name.Length is 0 or > 100)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["Name is required (100 characters max)."] });

            var userId = tenant.UserId!.Value;
            var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, ct);
            var household = new Household { Id = Guid.NewGuid(), Name = name, CreatedByUserId = userId };

            // A new household has no members yet, so creating it and its Owner needs system scope.
            using (tenant.BeginSystemScope())
            {
                db.Households.Add(household);
                db.Members.Add(new Member
                {
                    HouseholdId = household.Id,
                    UserId = userId,
                    Role = Roles.Owner,
                    DisplayName = Clean(req.DisplayName) ?? user.DisplayName ?? user.Email ?? "Owner",
                });
                await db.SaveChangesAsync(ct);
            }

            return Results.Created($"/households/{household.Id}",
                new HouseholdDto(household.Id, household.Name, household.CreatedAt, Roles.Owner));
        });

        app.MapPost("/invites/accept", AcceptInvite).RequireAuthorization().RequireRateLimiting("invites").WithTags("Households");

        var hh = app.MapHouseholdGroup().WithTags("Households");

        hh.MapGet("/", async (Guid householdId, TenantContext tenant, AppDbContext db, CancellationToken ct) =>
        {
            var h = await db.Households.AsNoTracking().FirstAsync(x => x.Id == householdId, ct);
            return Results.Ok(new HouseholdDto(h.Id, h.Name, h.CreatedAt, tenant.Role!));
        });

        hh.MapPatch("/", async (Guid householdId, RenameHouseholdRequest req, TenantContext tenant, AppDbContext db, CancellationToken ct) =>
        {
            if (tenant.RequireRole(Roles.Owner, Roles.Adult) is { } denied) return denied;
            var name = req.Name?.Trim() ?? "";
            if (name.Length is 0 or > 100)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["Name is required (100 characters max)."] });
            var h = await db.Households.FirstAsync(x => x.Id == householdId, ct);
            h.Name = name;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new HouseholdDto(h.Id, h.Name, h.CreatedAt, tenant.Role!));
        });

        hh.MapDelete("/", async (Guid householdId, TenantContext tenant, AppDbContext db, CancellationToken ct) =>
        {
            if (tenant.RequireRole(Roles.Owner) is { } denied) return denied;
            await db.Households.Where(x => x.Id == householdId).ExecuteDeleteAsync(ct);
            return Results.NoContent();
        });

        // ---- Members ----

        hh.MapGet("/members", async (AppDbContext db, TenantContext tenant, CancellationToken ct) =>
            Results.Ok(await db.Members.AsNoTracking()
                .Where(m => m.HouseholdId == tenant.HouseholdId)
                .OrderBy(m => m.DisplayName)
                .Select(m => new MemberDto(m.Id, m.UserId, m.DisplayName, m.Role, m.JoinedAt))
                .ToListAsync(ct)));

        hh.MapPatch("/members/{memberId:guid}", async (Guid memberId, UpdateMemberRequest req, TenantContext tenant, AppDbContext db, CancellationToken ct) =>
        {
            var member = await db.Members.FirstOrDefaultAsync(m => m.Id == memberId && m.HouseholdId == tenant.HouseholdId, ct);
            if (member is null) return Results.NotFound();

            if (req.DisplayName is not null)
            {
                // Your own name, or anyone's if you manage members.
                if (member.Id != tenant.MemberId && !Roles.CanManageMembers(tenant.Role!))
                    return Results.Problem(statusCode: 403, title: "You can only rename yourself.");
                member.DisplayName = Clean(req.DisplayName) ?? member.DisplayName;
            }

            if (req.Role is not null && req.Role != member.Role)
            {
                if (!Roles.Invitable.Contains(req.Role))
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["role"] = ["Role must be Adult, Member or Child."] });
                if (member.Role == Roles.Owner)
                    return Results.Problem(statusCode: 409, title: "The owner's role cannot be changed.");
                if (tenant.RequireRole(Roles.Owner, Roles.Adult) is { } denied) return denied;
                member.Role = req.Role;
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok(new MemberDto(member.Id, member.UserId, member.DisplayName, member.Role, member.JoinedAt));
        });

        hh.MapDelete("/members/{memberId:guid}", async (Guid memberId, TenantContext tenant, AppDbContext db, CancellationToken ct) =>
        {
            var member = await db.Members.FirstOrDefaultAsync(m => m.Id == memberId && m.HouseholdId == tenant.HouseholdId, ct);
            if (member is null) return Results.NotFound();

            var leavingSelf = member.Id == tenant.MemberId;
            if (member.Role == Roles.Owner)
                return Results.Problem(statusCode: 409, title: "The owner cannot leave or be removed. Delete the household instead.");
            if (!leavingSelf && tenant.RequireRole(Roles.Owner, Roles.Adult) is { } denied) return denied;

            db.Members.Remove(member);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // ---- Invites ----

        hh.MapGet("/invites", async (TenantContext tenant, AppDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (tenant.RequireRole(Roles.Owner, Roles.Adult) is { } denied) return denied;
            var now = clock.GetUtcNow().UtcDateTime;
            return Results.Ok(await db.Invites.AsNoTracking()
                .Where(i => i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now)
                .OrderByDescending(i => i.CreatedAt)
                .Select(i => new InviteDto(i.Id, i.Role, i.CreatedAt, i.ExpiresAt))
                .ToListAsync(ct));
        });

        hh.MapPost("/invites", async (CreateInviteRequest req, TenantContext tenant, AppDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (tenant.RequireRole(Roles.Owner, Roles.Adult) is { } denied) return denied;
            if (!Roles.Invitable.Contains(req.Role))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["role"] = ["Role must be Adult, Member or Child."] });
            var days = req.ExpiresInDays ?? DefaultInviteDays;
            if (days is < 1 or > MaxInviteDays)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["expiresInDays"] = [$"Between 1 and {MaxInviteDays} days."] });

            var code = InviteCodes.Generate();
            var invite = new Invite
            {
                CodeHash = InviteCodes.Hash(code),
                Role = req.Role,
                CreatedByUserId = tenant.UserId!.Value,
                ExpiresAt = clock.GetUtcNow().UtcDateTime.AddDays(days),
            };
            db.Invites.Add(invite);
            await db.SaveChangesAsync(ct);
            // The code is shown once; only its hash is stored.
            return Results.Ok(new CreatedInviteDto(invite.Id, code, invite.Role, invite.ExpiresAt));
        });

        hh.MapDelete("/invites/{inviteId:guid}", async (Guid inviteId, TenantContext tenant, AppDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (tenant.RequireRole(Roles.Owner, Roles.Adult) is { } denied) return denied;
            var invite = await db.Invites.FirstOrDefaultAsync(i => i.Id == inviteId, ct);
            if (invite is null) return Results.NotFound();
            invite.RevokedAt ??= clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    private static async Task<IResult> AcceptInvite(AcceptInviteRequest req, TenantContext tenant, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var invalid = Results.Problem(statusCode: 404, title: "That invite code is not valid or has expired.");
        if (string.IsNullOrWhiteSpace(req.Code) || InviteCodes.Normalize(req.Code).Length != 8) return invalid;

        var userId = tenant.UserId!.Value;
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, ct);
        var hash = InviteCodes.Hash(req.Code);
        var now = clock.GetUtcNow().UtcDateTime;

        // The caller is not in the household yet, so looking up the invite needs system scope.
        // Everything done here is checked explicitly: the code must match an open invite.
        using (tenant.BeginSystemScope())
        {
            var invite = await db.Invites.FirstOrDefaultAsync(i => i.CodeHash == hash, ct);
            if (invite is null || invite.AcceptedAt != null || invite.RevokedAt != null || invite.ExpiresAt <= now)
                return invalid;

            var alreadyMember = await db.Members.AnyAsync(m => m.HouseholdId == invite.HouseholdId && m.UserId == userId, ct);
            if (alreadyMember)
                return Results.Problem(statusCode: 409, title: "You are already in this household.");

            invite.AcceptedAt = now;
            invite.AcceptedByUserId = userId;
            db.Members.Add(new Member
            {
                HouseholdId = invite.HouseholdId,
                UserId = userId,
                Role = invite.Role,
                DisplayName = Clean(req.DisplayName) ?? user.DisplayName ?? user.Email ?? "Member",
            });
            await db.SaveChangesAsync(ct);

            var household = await db.Households.AsNoTracking().FirstAsync(h => h.Id == invite.HouseholdId, ct);
            return Results.Ok(new HouseholdDto(household.Id, household.Name, household.CreatedAt, invite.Role));
        }
    }

    private static string? Clean(string? name)
    {
        var n = name?.Trim();
        return string.IsNullOrEmpty(n) ? null : n.Length > 100 ? n[..100] : n;
    }
}
