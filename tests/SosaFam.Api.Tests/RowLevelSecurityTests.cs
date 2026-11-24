using System.Net.Http.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SosaFam.Api.Auth;
using SosaFam.Api.Data;
using SosaFam.Api.Households;
using SosaFam.Api.Tenancy;

namespace SosaFam.Api.Tests;

/// <summary>
/// Proves the database keeps households apart on its own: these queries skip the
/// DbContext's filters, so only SQL Server row-level security stands in the way.
/// </summary>
public class RowLevelSecurityTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [DbFact]
    public async Task Database_hides_and_blocks_other_households_without_EF_filters()
    {
        var (alice, _) = await factory.NewUserAsync("Alice");
        var (bob, _) = await factory.NewUserAsync("Bob");
        var a = (await (await alice.PostAsJsonAsync("/households", new { name = "A" })).Content.ReadFromJsonAsync<HouseholdDto>())!;
        var b = (await (await bob.PostAsJsonAsync("/households", new { name = "B" })).Content.ReadFromJsonAsync<HouseholdDto>())!;
        var bobMe = (await bob.GetFromJsonAsync<MeResponse>("/me"))!;

        using var scope = factory.Services.CreateScope();
        var tenant = scope.ServiceProvider.GetRequiredService<TenantContext>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        tenant.UserId = bobMe.Id;
        tenant.EnterHousehold(b.Id, bobMe.Households.Single().MemberId, "Owner");

        Assert.Equal(0, await db.Households.IgnoreQueryFilters().CountAsync(h => h.Id == a.Id));
        Assert.Equal(0, await db.Members.IgnoreQueryFilters().CountAsync(m => m.HouseholdId == a.Id));
        Assert.Equal(0, await db.Invites.IgnoreQueryFilters().CountAsync(i => i.HouseholdId == a.Id));
        Assert.Equal(1, await db.Households.IgnoreQueryFilters().CountAsync(h => h.Id == b.Id));

        // Raw SQL writes into household A are refused by the security policy.
        var ex = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO core.Members (HouseholdId, UserId, Role, DisplayName) VALUES ({a.Id}, {bobMe.Id}, N'Owner', N'Intruder')"));
        Assert.Contains("block predicate", ex.Message);

        Assert.Equal(0, await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE core.Households SET Name = N'hacked' WHERE Id = {a.Id}"));
    }

    [DbFact]
    public async Task Database_shows_nothing_without_a_signed_in_user()
    {
        var (alice, _) = await factory.NewUserAsync("Alice");
        (await alice.PostAsJsonAsync("/households", new { name = "A" })).EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.Households.IgnoreQueryFilters().CountAsync());
        Assert.Equal(0, await db.Members.IgnoreQueryFilters().CountAsync());
    }
}
