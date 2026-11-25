using System.Net;
using System.Net.Http.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using SosaFam.Api.Data;
using SosaFam.Api.Grocery;
using SosaFam.Api.Households;
using SosaFam.Api.Pantry;

namespace SosaFam.Api.Tests;

public class PantryTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private async Task<(HttpClient Alice, HttpClient Bob, string BobToken, Guid HouseholdId)> FamilyOfTwo()
    {
        var (alice, _) = await factory.NewUserAsync("Alice");
        var (bob, bobTokens) = await factory.NewUserAsync("Bob");
        var h = (await (await alice.PostAsJsonAsync("/households", new { name = "Sosa" })).Content.ReadFromJsonAsync<HouseholdDto>())!;
        var invite = (await (await alice.PostAsJsonAsync($"/households/{h.Id}/invites", new { role = "Member" })).Content.ReadFromJsonAsync<CreatedInviteDto>())!;
        (await bob.PostAsJsonAsync("/invites/accept", new { code = invite.Code })).EnsureSuccessStatusCode();
        return (alice, bob, bobTokens.AccessToken, h.Id);
    }

    private static async Task<T> Read<T>(Task<HttpResponseMessage> call)
    {
        var res = await call;
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<Dictionary<string, LocationDto>> Locations(HttpClient c, Guid h) =>
        (await Read<List<LocationDto>>(c.GetAsync($"/households/{h}/pantry/locations"))).ToDictionary(l => l.Name);

    [Fact]
    public void Proxy_ids_match_the_database_format()
    {
        Assert.Equal("31773", ProxyId.PartKey(new DateTime(2026, 11, 25)));
        var loc = new Location();
        ProxyId.Assign(loc, Location.Prefix, new DateTime(2026, 11, 25));
        Assert.Matches("^PALO:31773:[0-9A-F-]{36}$", loc.Proxy);
        Assert.True(ProxyId.IsValid(loc.Proxy, Location.Prefix));
        Assert.False(ProxyId.IsValid(loc.Proxy, Product.Prefix));
        Assert.False(ProxyId.IsValid("PALO:31773:not-a-guid", Location.Prefix));
    }

    [DbFact]
    public async Task Family_tracks_stock_and_restocks_from_the_grocery_list()
    {
        var (alice, bob, _, h) = await FamilyOfTwo();
        var locs = await Locations(alice, h);
        Assert.Equal(["Pantry", "Fridge", "Basement"], locs.Keys.ToArray());
        Assert.Equal(TrackingModes.Status, locs["Fridge"].TrackingMode);
        Assert.Equal(3, (await Locations(bob, h)).Count); // created once, not per person

        var winco = await Read<StoreDto>(alice.PostAsJsonAsync($"/households/{h}/stores", new { name = "WinCo" }));
        var rice = await Read<ProductDto>(alice.PostAsJsonAsync($"/households/{h}/pantry/products",
            new { name = "Rice", unit = "lb", minQuantity = 10, targetQuantity = 25, storeId = winco.Id, barcode = "0123456789" }));
        Assert.StartsWith("PAPR:", rice.Id);
        Assert.True(rice.NeedsRestock);
        Assert.Equal(25, rice.SuggestedQuantity);

        // Barcode scan finds it; a second product cannot reuse the barcode.
        Assert.Equal(rice.Id, (await Read<ProductDto>(bob.GetAsync($"/households/{h}/pantry/products/by-barcode/0123456789"))).Id);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/households/{h}/pantry/products/by-barcode/999")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await bob.PostAsJsonAsync($"/households/{h}/pantry/products", new { name = "Other", barcode = "0123456789" })).StatusCode);

        // +/- in two places: pantry 2, basement 5, total 7 (below the minimum of 10).
        await Read<ProductDto>(alice.PostAsJsonAsync($"/households/{h}/pantry/products/{rice.Id}/stock/{locs["Pantry"].Id}/adjust", new { delta = 2 }));
        rice = await Read<ProductDto>(bob.PostAsJsonAsync($"/households/{h}/pantry/products/{rice.Id}/stock/{locs["Basement"].Id}/adjust", new { delta = 5 }));
        Assert.Equal(7, rice.OnHand);
        Assert.True(rice.NeedsRestock);
        Assert.Equal(18, rice.SuggestedQuantity);

        // Never below zero.
        var pantryOnly = await Read<ProductDto>(alice.PostAsJsonAsync($"/households/{h}/pantry/products/{rice.Id}/stock/{locs["Pantry"].Id}/adjust", new { delta = -50 }));
        Assert.Equal(0, pantryOnly.Stock.Single(s => s.LocationName == "Pantry").Quantity);
        await Read<ProductDto>(alice.PutAsJsonAsync($"/households/{h}/pantry/products/{rice.Id}/stock/{locs["Pantry"].Id}", new { quantity = 2 }));

        // Bring 2 up from the basement.
        rice = await Read<ProductDto>(alice.PostAsJsonAsync($"/households/{h}/pantry/products/{rice.Id}/move",
            new { fromLocationId = locs["Basement"].Id, toLocationId = locs["Pantry"].Id, quantity = 2 }));
        Assert.Equal((4m, 3m), (rice.Stock.Single(s => s.LocationName == "Pantry").Quantity, rice.Stock.Single(s => s.LocationName == "Basement").Quantity));
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync($"/households/{h}/pantry/products/{rice.Id}/move",
            new { fromLocationId = locs["Basement"].Id, toLocationId = locs["Pantry"].Id, quantity = 50 })).StatusCode);

        // Fridge: counts are refused, status works; a staple that is Low is a need.
        var milk = await Read<ProductDto>(alice.PostAsJsonAsync($"/households/{h}/pantry/products", new { name = "Milk", isStaple = true, unit = "gal" }));
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync($"/households/{h}/pantry/products/{milk.Id}/stock/{locs["Fridge"].Id}/adjust", new { delta = 1 })).StatusCode);
        milk = await Read<ProductDto>(bob.PutAsJsonAsync($"/households/{h}/pantry/products/{milk.Id}/stock/{locs["Fridge"].Id}", new { status = "Low" }));
        Assert.True(milk.NeedsRestock);

        // A product at its minimum is not a need.
        var beans = await Read<ProductDto>(alice.PostAsJsonAsync($"/households/{h}/pantry/products", new { name = "Beans", minQuantity = 2 }));
        beans = await Read<ProductDto>(alice.PutAsJsonAsync($"/households/{h}/pantry/products/{beans.Id}/stock/{locs["Basement"].Id}", new { quantity = 2 }));
        Assert.False(beans.NeedsRestock);

        var needs = await Read<List<NeedDto>>(alice.GetAsync($"/households/{h}/pantry/needs"));
        Assert.Equal(["Milk", "Rice"], needs.Select(n => n.Product.Name).OrderBy(n => n).ToArray());
        Assert.All(needs, n => Assert.False(n.OnOpenList));

        // Needs go onto the shared grocery list, tagged with the store; running it again adds nothing.
        var list = await Read<GroceryListDto>(alice.PostAsJsonAsync($"/households/{h}/grocery-lists", new { name = "Restock" }));
        var result = await Read<AddNeedsResult>(alice.PostAsJsonAsync($"/households/{h}/pantry/needs/add-to-list", new { listId = list.Id }));
        Assert.Equal(2, result.Added);
        var riceItem = result.Items.Single(i => i.Name == "Rice");
        Assert.Equal(("LowStock", "WinCo", 18m, rice.GroceryProductId), (riceItem.Source, riceItem.StoreName!, riceItem.Quantity!.Value, riceItem.ProductId!.Value));
        Assert.Equal(0, (await Read<AddNeedsResult>(alice.PostAsJsonAsync($"/households/{h}/pantry/needs/add-to-list", new { listId = list.Id }))).Added);
        Assert.All(await Read<List<NeedDto>>(alice.GetAsync($"/households/{h}/pantry/needs")), n => Assert.True(n.OnOpenList));

        // Bob buys the rice and puts it away in the basement.
        await Read<GroceryItemDto>(bob.PutAsJsonAsync($"/households/{h}/grocery-lists/{list.Id}/items/{riceItem.Id}/checked", new { isChecked = true }));
        rice = await Read<ProductDto>(bob.PostAsJsonAsync($"/households/{h}/pantry/put-away", new { groceryItemId = riceItem.Id, locationId = locs["Basement"].Id }));
        Assert.Equal(25, rice.OnHand);
        Assert.False(rice.NeedsRestock);

        var milkItem = result.Items.Single(i => i.Name == "Milk");
        milk = await Read<ProductDto>(bob.PostAsJsonAsync($"/households/{h}/pantry/put-away", new { groceryItemId = milkItem.Id, locationId = locs["Fridge"].Id }));
        Assert.Equal("Have", milk.Stock.Single().Status);
        Assert.Empty(await Read<List<NeedDto>>(alice.GetAsync($"/households/{h}/pantry/needs")));

        // History, newest first, with who did it.
        var activity = await Read<List<ActivityDto>>(alice.GetAsync($"/households/{h}/pantry/activity"));
        Assert.Equal(("Milk", "PutAway", "Bob"), (activity[0].ProductName, activity[0].ChangeType, activity[0].ByName!));
        Assert.Contains(activity, a => a.ChangeType == "MoveOut" && a.LocationName == "Basement");

        // Deleting the store keeps the product, with no preferred store.
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync($"/households/{h}/stores/{winco.Id}")).StatusCode);
        Assert.Null((await Read<ProductDto>(alice.GetAsync($"/households/{h}/pantry/products/{rice.Id}"))).StoreId);

        // Soft delete.
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync($"/households/{h}/pantry/products/{beans.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"/households/{h}/pantry/products/{beans.Id}")).StatusCode);
        Assert.DoesNotContain(await Read<List<ProductDto>>(alice.GetAsync($"/households/{h}/pantry/products")), p => p.Name == "Beans");
    }

    [DbFact]
    public async Task Other_families_cannot_see_or_touch_the_pantry()
    {
        var (alice, _, _, h) = await FamilyOfTwo();
        var (eve, _) = await factory.NewUserAsync("Eve");
        var eveHome = (await (await eve.PostAsJsonAsync("/households", new { name = "Eve" })).Content.ReadFromJsonAsync<HouseholdDto>())!;
        var aliceLocs = await Locations(alice, h);
        var rice = await Read<ProductDto>(alice.PostAsJsonAsync($"/households/{h}/pantry/products", new { name = "Rice", barcode = "42" }));
        await Read<ProductDto>(alice.PostAsJsonAsync($"/households/{h}/pantry/products/{rice.Id}/stock/{aliceLocs["Pantry"].Id}/adjust", new { delta = 3 }));

        Assert.Equal(HttpStatusCode.NotFound, (await eve.GetAsync($"/households/{h}/pantry/products")).StatusCode);
        // Through her own household, Alice's ids lead nowhere.
        var eveLocs = await Locations(eve, eveHome.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await eve.GetAsync($"/households/{eveHome.Id}/pantry/products/{rice.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await eve.PostAsJsonAsync($"/households/{eveHome.Id}/pantry/products/{rice.Id}/stock/{eveLocs["Pantry"].Id}/adjust", new { delta = -3 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await eve.DeleteAsync($"/households/{eveHome.Id}/pantry/products/{rice.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await eve.GetAsync($"/households/{eveHome.Id}/pantry/products/by-barcode/42")).StatusCode);
        Assert.Empty(await Read<List<ProductDto>>(eve.GetAsync($"/households/{eveHome.Id}/pantry/products")));
        Assert.Empty(await Read<List<ActivityDto>>(eve.GetAsync($"/households/{eveHome.Id}/pantry/activity")));

        // Her own barcode 42 is fine: barcodes are unique per family.
        await Read<ProductDto>(eve.PostAsJsonAsync($"/households/{eveHome.Id}/pantry/products", new { name = "Not rice", barcode = "42" }));

        Assert.Equal(3, (await Read<ProductDto>(alice.GetAsync($"/households/{h}/pantry/products/{rice.Id}"))).OnHand);
    }

    [DbFact]
    public async Task Stock_changes_reach_everyone_watching_in_real_time()
    {
        var (alice, _, bobToken, h) = await FamilyOfTwo();
        var locs = await Locations(alice, h);
        var rice = await Read<ProductDto>(alice.PostAsJsonAsync($"/households/{h}/pantry/products", new { name = "Rice" }));

        var events = Channel.CreateUnbounded<ProductDto>();
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "/hubs/household"), o =>
            {
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                o.AccessTokenProvider = () => Task.FromResult<string?>(bobToken);
                o.Transports = HttpTransportType.LongPolling;
            })
            .Build();
        hub.On<ProductDto>("pantryProductUpserted", dto => events.Writer.TryWrite(dto));
        await hub.StartAsync();
        await hub.InvokeAsync("JoinHousehold", h);

        await Read<ProductDto>(alice.PostAsJsonAsync($"/households/{h}/pantry/products/{rice.Id}/stock/{locs["Pantry"].Id}/adjust", new { delta = 4 }));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = await events.Reader.ReadAsync(timeout.Token);
        Assert.Equal((rice.Id, 4m), (received.Id, received.OnHand));
    }
}
