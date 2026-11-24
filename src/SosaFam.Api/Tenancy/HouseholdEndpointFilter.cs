using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SosaFam.Api.Data;

namespace SosaFam.Api.Tenancy;

/// <summary>
/// For routes under /households/{householdId}: checks the caller is a member of that
/// household and enters it. Non-members get 404, so household ids cannot be probed.
/// </summary>
public class HouseholdEndpointFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        if (!Guid.TryParse(http.Request.RouteValues["householdId"]?.ToString(), out var householdId))
            return Results.NotFound();

        var tenant = http.RequestServices.GetRequiredService<TenantContext>();
        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        if (tenant.UserId is not { } userId) return Results.Unauthorized();

        // Allowed by RLS before a household is chosen: users can always see their own memberships.
        var member = await db.Members.AsNoTracking()
            .Where(m => m.HouseholdId == householdId && m.UserId == userId)
            .Select(m => new { m.Id, m.Role })
            .FirstOrDefaultAsync(http.RequestAborted);
        if (member is null) return Results.NotFound();

        tenant.EnterHousehold(householdId, member.Id, member.Role);
        return await next(ctx);
    }
}

public static class TenantExtensions
{
    /// <summary>Fills TenantContext.UserId from the signed-in user's token.</summary>
    public static IApplicationBuilder UseTenantContext(this IApplicationBuilder app) =>
        app.Use(async (http, next) =>
        {
            var sub = http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.User.FindFirstValue("sub");
            if (Guid.TryParse(sub, out var userId))
                http.RequestServices.GetRequiredService<TenantContext>().UserId = userId;
            await next();
        });

    public static RouteGroupBuilder MapHouseholdGroup(this IEndpointRouteBuilder app, string pattern = "/households/{householdId:guid}") =>
        app.MapGroup(pattern).RequireAuthorization().AddEndpointFilter<HouseholdEndpointFilter>();

    /// <summary>403 unless the caller's role in the current household is one of <paramref name="roles"/>.</summary>
    public static IResult? RequireRole(this TenantContext tenant, params string[] roles) =>
        tenant.Role is { } r && roles.Contains(r) ? null : Results.Problem(statusCode: 403, title: "Your role in this household cannot do that.");
}
