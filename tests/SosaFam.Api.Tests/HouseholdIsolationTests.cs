using System.Net;
using System.Net.Http.Json;
using SosaFam.Api.Auth;
using SosaFam.Api.Households;

namespace SosaFam.Api.Tests;

public class HouseholdIsolationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static async Task<HouseholdDto> CreateHousehold(HttpClient c, string name)
    {
        var res = await c.PostAsJsonAsync("/households", new { name });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<HouseholdDto>())!;
    }

    private static async Task<CreatedInviteDto> CreateInvite(HttpClient c, Guid householdId, string role)
    {
        var res = await c.PostAsJsonAsync($"/households/{householdId}/invites", new { role });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<CreatedInviteDto>())!;
    }

    [DbFact]
    public async Task Me_lists_only_my_households()
    {
        var (alice, _) = await factory.NewUserAsync("Alice");
        var (bob, _) = await factory.NewUserAsync("Bob");
        var a = await CreateHousehold(alice, "Alice family");
        await CreateHousehold(bob, "Bob family");

        var me = (await alice.GetFromJsonAsync<MeResponse>("/me"))!;
        var only = Assert.Single(me.Households);
        Assert.Equal(a.Id, only.HouseholdId);
        Assert.Equal("Owner", only.Role);
    }

    [DbFact]
    public async Task Other_households_are_not_found()
    {
        var (alice, _) = await factory.NewUserAsync("Alice");
        var (bob, _) = await factory.NewUserAsync("Bob");
        var a = await CreateHousehold(alice, "Alice family");

        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/households/{a.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/households/{a.Id}/members")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsJsonAsync($"/households/{a.Id}/invites", new { role = "Adult" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.DeleteAsync($"/households/{a.Id}")).StatusCode);

        // Still there for Alice.
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync($"/households/{a.Id}")).StatusCode);
    }

    [DbFact]
    public async Task Members_of_one_household_never_appear_in_another()
    {
        var (alice, _) = await factory.NewUserAsync("Alice");
        var (bob, _) = await factory.NewUserAsync("Bob");
        var a = await CreateHousehold(alice, "Alice family");
        var b = await CreateHousehold(bob, "Bob family");

        var members = (await alice.GetFromJsonAsync<List<MemberDto>>($"/households/{a.Id}/members"))!;
        Assert.Equal("Alice", Assert.Single(members).DisplayName);

        // A member id from household B cannot be changed or removed through household A.
        var bobMember = Assert.Single((await bob.GetFromJsonAsync<List<MemberDto>>($"/households/{b.Id}/members"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.PatchAsJsonAsync($"/households/{a.Id}/members/{bobMember.Id}", new { displayName = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.DeleteAsync($"/households/{a.Id}/members/{bobMember.Id}")).StatusCode);
    }

    [DbFact]
    public async Task Invite_lets_someone_join_once_with_the_invited_role()
    {
        var (alice, _) = await factory.NewUserAsync("Alice");
        var (bob, _) = await factory.NewUserAsync("Bob");
        var (carol, _) = await factory.NewUserAsync("Carol");
        var a = await CreateHousehold(alice, "Alice family");

        var invite = await CreateInvite(alice, a.Id, "Member");
        Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}$", invite.Code);

        var join = await bob.PostAsJsonAsync("/invites/accept", new { code = invite.Code.ToLowerInvariant() });
        Assert.Equal(HttpStatusCode.OK, join.StatusCode);
        Assert.Equal("Member", (await join.Content.ReadFromJsonAsync<HouseholdDto>())!.MyRole);

        var members = (await bob.GetFromJsonAsync<List<MemberDto>>($"/households/{a.Id}/members"))!;
        Assert.Equal(2, members.Count);

        // Used codes do not work again.
        Assert.Equal(HttpStatusCode.NotFound, (await carol.PostAsJsonAsync("/invites/accept", new { code = invite.Code })).StatusCode);
    }

    [DbFact]
    public async Task Revoked_and_wrong_codes_are_rejected()
    {
        var (alice, _) = await factory.NewUserAsync("Alice");
        var (bob, _) = await factory.NewUserAsync("Bob");
        var a = await CreateHousehold(alice, "Alice family");
        var invite = await CreateInvite(alice, a.Id, "Adult");

        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync($"/households/{a.Id}/invites/{invite.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsJsonAsync("/invites/accept", new { code = invite.Code })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsJsonAsync("/invites/accept", new { code = "AAAA-AAAA" })).StatusCode);
    }

    [DbFact]
    public async Task Members_and_children_cannot_manage_the_household()
    {
        var (alice, _) = await factory.NewUserAsync("Alice");
        var (kid, _) = await factory.NewUserAsync("Kid");
        var a = await CreateHousehold(alice, "Alice family");
        var invite = await CreateInvite(alice, a.Id, "Child");
        (await kid.PostAsJsonAsync("/invites/accept", new { code = invite.Code })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Forbidden, (await kid.PostAsJsonAsync($"/households/{a.Id}/invites", new { role = "Adult" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await kid.PatchAsJsonAsync($"/households/{a.Id}", new { name = "Kid rules" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await kid.DeleteAsync($"/households/{a.Id}")).StatusCode);

        var members = (await alice.GetFromJsonAsync<List<MemberDto>>($"/households/{a.Id}/members"))!;
        var owner = members.Single(m => m.Role == "Owner");
        Assert.Equal(HttpStatusCode.Conflict, (await kid.DeleteAsync($"/households/{a.Id}/members/{owner.Id}")).StatusCode);

        // A child can leave on their own.
        var me = members.Single(m => m.Role == "Child");
        Assert.Equal(HttpStatusCode.NoContent, (await kid.DeleteAsync($"/households/{a.Id}/members/{me.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await kid.GetAsync($"/households/{a.Id}")).StatusCode);
    }
}
