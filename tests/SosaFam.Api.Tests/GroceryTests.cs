using System.Net;
using System.Net.Http.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using SosaFam.Api.Grocery;
using SosaFam.Api.Households;

namespace SosaFam.Api.Tests;

public class GroceryTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private async Task<(HttpClient Alice, string AliceToken, HttpClient Bob, string BobToken, Guid HouseholdId)> FamilyOfTwo()
    {
        var (alice, aliceTokens) = await factory.NewUserAsync("Alice");
        var (bob, bobTokens) = await factory.NewUserAsync("Bob");
        var h = (await (await alice.PostAsJsonAsync("/households", new { name = "Sosa" })).Content.ReadFromJsonAsync<HouseholdDto>())!;
        var invite = (await (await alice.PostAsJsonAsync($"/households/{h.Id}/invites", new { role = "Member" })).Content.ReadFromJsonAsync<CreatedInviteDto>())!;
        (await bob.PostAsJsonAsync("/invites/accept", new { code = invite.Code })).EnsureSuccessStatusCode();
        return (alice, aliceTokens.AccessToken, bob, bobTokens.AccessToken, h.Id);
    }

    private static async Task<T> Read<T>(Task<HttpResponseMessage> call)
    {
        var res = await call;
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<T>())!;
    }

    private HubConnection Hub(string token) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "/hubs/household"), o =>
            {
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                o.AccessTokenProvider = () => Task.FromResult<string?>(token);
                o.Transports = HttpTransportType.LongPolling;
            })
            .Build();

    [DbFact]
    public async Task Family_shares_a_list_split_by_store()
    {
        var (alice, _, bob, _, h) = await FamilyOfTwo();
        var winco = await Read<StoreDto>(alice.PostAsJsonAsync($"/households/{h}/stores", new { name = "WinCo" }));
        var costco = await Read<StoreDto>(alice.PostAsJsonAsync($"/households/{h}/stores", new { name = "Costco" }));
        Assert.Equal(HttpStatusCode.Conflict, (await bob.PostAsJsonAsync($"/households/{h}/stores", new { name = "WinCo" })).StatusCode);

        var list = await Read<GroceryListDto>(alice.PostAsJsonAsync($"/households/{h}/grocery-lists", new { name = "This week" }));
        await Read<GroceryItemDto>(alice.PostAsJsonAsync($"/households/{h}/grocery-lists/{list.Id}/items", new { name = "Milk", quantity = 2, unit = "gal", storeId = winco.Id }));
        await Read<GroceryItemDto>(alice.PostAsJsonAsync($"/households/{h}/grocery-lists/{list.Id}/items", new { name = "Paper towels", storeId = costco.Id }));
        var any = await Read<GroceryItemDto>(alice.PostAsJsonAsync($"/households/{h}/grocery-lists/{list.Id}/items", new { name = "Bananas" }));
        Assert.Null(any.StoreName);

        // Bob sees the same list and checks off the Costco item.
        var detail = await Read<GroceryListDetailDto>(bob.GetAsync($"/households/{h}/grocery-lists/{list.Id}"));
        Assert.Equal(3, detail.Items.Count);
        var towels = detail.Items.Single(i => i.StoreName == "Costco");
        var checkedOff = await Read<GroceryItemDto>(bob.PutAsJsonAsync($"/households/{h}/grocery-lists/{list.Id}/items/{towels.Id}/checked", new { isChecked = true }));
        Assert.True(checkedOff.IsChecked);
        Assert.Equal("Bob", checkedOff.CheckedByName);
        Assert.Equal("Alice", checkedOff.AddedByName);

        var lists = await Read<List<GroceryListDto>>(alice.GetAsync($"/households/{h}/grocery-lists"));
        var summary = Assert.Single(lists);
        Assert.Equal((3, 2), (summary.ItemCount, summary.OpenCount));

        // Deleting a store keeps its items, now for any store.
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync($"/households/{h}/stores/{winco.Id}")).StatusCode);
        detail = await Read<GroceryListDetailDto>(alice.GetAsync($"/households/{h}/grocery-lists/{list.Id}"));
        Assert.Null(detail.Items.Single(i => i.Name == "Milk").StoreId);

        // Clearing checked items removes only those.
        Assert.Equal(HttpStatusCode.NoContent, (await alice.PostAsync($"/households/{h}/grocery-lists/{list.Id}/clear-checked", null)).StatusCode);
        detail = await Read<GroceryListDetailDto>(alice.GetAsync($"/households/{h}/grocery-lists/{list.Id}"));
        Assert.DoesNotContain(detail.Items, i => i.Name == "Paper towels");
        Assert.Equal(2, detail.Items.Count);
    }

    [DbFact]
    public async Task Food_storage_can_add_low_stock_items()
    {
        var (alice, _, _, _, h) = await FamilyOfTwo();
        var list = await Read<GroceryListDto>(alice.PostAsJsonAsync($"/households/{h}/grocery-lists", new { name = "Restock" }));
        var productId = Guid.NewGuid();
        var item = await Read<GroceryItemDto>(alice.PostAsJsonAsync($"/households/{h}/grocery-lists/{list.Id}/items",
            new { name = "Rice", quantity = 25, unit = "lb", source = "LowStock", productId }));
        Assert.Equal("LowStock", item.Source);
        Assert.Equal(productId, item.ProductId);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync($"/households/{h}/grocery-lists/{list.Id}/items", new { name = "x", source = "Robot" })).StatusCode);
    }

    [DbFact]
    public async Task Other_families_cannot_see_or_touch_the_list()
    {
        var (alice, _, _, _, h) = await FamilyOfTwo();
        var (eve, _) = await factory.NewUserAsync("Eve");
        var eveHome = (await (await eve.PostAsJsonAsync("/households", new { name = "Eve" })).Content.ReadFromJsonAsync<HouseholdDto>())!;
        var list = await Read<GroceryListDto>(alice.PostAsJsonAsync($"/households/{h}/grocery-lists", new { name = "Private" }));
        var item = await Read<GroceryItemDto>(alice.PostAsJsonAsync($"/households/{h}/grocery-lists/{list.Id}/items", new { name = "Milk" }));
        var store = await Read<StoreDto>(alice.PostAsJsonAsync($"/households/{h}/stores", new { name = "WinCo" }));

        Assert.Equal(HttpStatusCode.NotFound, (await eve.GetAsync($"/households/{h}/grocery-lists/{list.Id}")).StatusCode);
        // Through her own household, Alice's ids lead nowhere.
        Assert.Equal(HttpStatusCode.NotFound, (await eve.GetAsync($"/households/{eveHome.Id}/grocery-lists/{list.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await eve.PutAsJsonAsync($"/households/{eveHome.Id}/grocery-lists/{list.Id}/items/{item.Id}/checked", new { isChecked = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await eve.DeleteAsync($"/households/{eveHome.Id}/grocery-lists/{list.Id}/items/{item.Id}")).StatusCode);
        Assert.Empty(await Read<List<GroceryListDto>>(eve.GetAsync($"/households/{eveHome.Id}/grocery-lists")));

        // A store id from another family cannot be used.
        var eveList = await Read<GroceryListDto>(eve.PostAsJsonAsync($"/households/{eveHome.Id}/grocery-lists", new { name = "Mine" }));
        Assert.Equal(HttpStatusCode.BadRequest, (await eve.PostAsJsonAsync($"/households/{eveHome.Id}/grocery-lists/{eveList.Id}/items", new { name = "x", storeId = store.Id })).StatusCode);

        var still = await Read<GroceryListDetailDto>(alice.GetAsync($"/households/{h}/grocery-lists/{list.Id}"));
        Assert.False(Assert.Single(still.Items).IsChecked);
    }

    [DbFact]
    public async Task Check_offs_reach_everyone_watching_in_real_time()
    {
        var (alice, _, bob, bobToken, h) = await FamilyOfTwo();
        var list = await Read<GroceryListDto>(alice.PostAsJsonAsync($"/households/{h}/grocery-lists", new { name = "Live" }));
        var item = await Read<GroceryItemDto>(alice.PostAsJsonAsync($"/households/{h}/grocery-lists/{list.Id}/items", new { name = "Eggs" }));

        var events = Channel.CreateUnbounded<GroceryItemDto>();
        await using var hub = Hub(bobToken);
        hub.On<Guid, GroceryItemDto>("groceryItemUpserted", (_, dto) => events.Writer.TryWrite(dto));
        await hub.StartAsync();
        await hub.InvokeAsync("JoinHousehold", h);

        await Read<GroceryItemDto>(alice.PutAsJsonAsync($"/households/{h}/grocery-lists/{list.Id}/items/{item.Id}/checked", new { isChecked = true }));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = await events.Reader.ReadAsync(timeout.Token);
        Assert.Equal(item.Id, received.Id);
        Assert.True(received.IsChecked);
        Assert.Equal("Alice", received.CheckedByName);
    }

    [DbFact]
    public async Task Outsiders_cannot_listen_to_a_family()
    {
        var (_, _, _, _, h) = await FamilyOfTwo();
        var (_, eveTokens) = await factory.NewUserAsync("Eve");
        await using var hub = Hub(eveTokens.AccessToken);
        await hub.StartAsync();
        await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync("JoinHousehold", h));
    }

    [DbFact]
    public async Task Hub_requires_sign_in()
    {
        await using var hub = Hub("not-a-token");
        await Assert.ThrowsAnyAsync<Exception>(() => hub.StartAsync());
    }
}
