using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SosaFam.Api.Data;
using SosaFam.Api.Tenancy;

namespace SosaFam.Api.Auth;

public record RegisterRequest([Required, EmailAddress] string Email, [Required] string Password, [Required, MaxLength(100)] string DisplayName);
public record LoginRequest(string Email, string Password);
public record RefreshRequest(string RefreshToken);
public record MembershipDto(Guid HouseholdId, string HouseholdName, Guid MemberId, string Role);
public record MeResponse(Guid Id, string Email, string? DisplayName, IReadOnlyList<MembershipDto> Households);

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/auth").WithTags("Auth").RequireRateLimiting("auth");

        auth.MapPost("/register", async (RegisterRequest req, UserManager<AppUser> users, TokenService tokens, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.DisplayName) || req.DisplayName.Length > 100)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["displayName"] = ["Display name is required (100 characters max)."] });

            var user = new AppUser { UserName = req.Email, Email = req.Email, DisplayName = req.DisplayName.Trim() };
            var result = await users.CreateAsync(user, req.Password);
            if (!result.Succeeded)
                return Results.ValidationProblem(result.Errors.GroupBy(e => e.Code).ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray()));

            return Results.Ok(await tokens.IssueAsync(user, ct));
        });

        auth.MapPost("/login", async (LoginRequest req, UserManager<AppUser> users, SignInManager<AppUser> signIn, TokenService tokens, CancellationToken ct) =>
        {
            var user = await users.FindByEmailAsync(req.Email);
            if (user is null) return Results.Problem(statusCode: 401, title: "Email or password is incorrect.");
            var check = await signIn.CheckPasswordSignInAsync(user, req.Password, lockoutOnFailure: true);
            if (check.IsLockedOut) return Results.Problem(statusCode: 423, title: "Too many failed attempts. Try again in a few minutes.");
            if (!check.Succeeded) return Results.Problem(statusCode: 401, title: "Email or password is incorrect.");
            return Results.Ok(await tokens.IssueAsync(user, ct));
        });

        auth.MapPost("/refresh", async (RefreshRequest req, TokenService tokens, CancellationToken ct) =>
            await tokens.RefreshAsync(req.RefreshToken, ct) is { } t ? Results.Ok(t) : Results.Problem(statusCode: 401, title: "Sign in again."));

        auth.MapPost("/logout", async (RefreshRequest req, TokenService tokens, CancellationToken ct) =>
        {
            await tokens.RevokeAsync(req.RefreshToken, ct);
            return Results.NoContent();
        });

        app.MapGet("/me", async (TenantContext tenant, AppDbContext db, CancellationToken ct) =>
        {
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == tenant.UserId, ct);
            if (user is null) return Results.Unauthorized();
            var households = await MyHouseholds(db, user.Id, ct);
            return Results.Ok(new MeResponse(user.Id, user.Email ?? "", user.DisplayName, households));
        }).RequireAuthorization().WithTags("Auth");
    }

    /// <summary>The user's memberships across households (row-level security allows a user
    /// to see their own memberships and the households they belong to).</summary>
    public static Task<List<MembershipDto>> MyHouseholds(AppDbContext db, Guid userId, CancellationToken ct) =>
        (from m in db.Members.AsNoTracking()
         join h in db.Households.IgnoreQueryFilters().AsNoTracking() on m.HouseholdId equals h.Id
         where m.UserId == userId
         orderby h.Name
         select new MembershipDto(h.Id, h.Name, m.Id, m.Role)).ToListAsync(ct);
}
