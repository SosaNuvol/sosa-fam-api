using System.Net;
using SosaFam.Api.Households;

namespace SosaFam.Api.Tests;

public class JoinPageTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public void Page_links_to_the_app_and_expo_go_with_the_code()
    {
        var html = JoinPage.Render("K7QM2XPA", new InviteLinkOptions { AppScheme = "sosafam", ExpoGoUrl = "exps://family-dev.example/" });
        Assert.Contains("href=\"sosafam://join?code=K7QM2XPA\"", html);
        Assert.Contains("href=\"exps://family-dev.example/--/join?code=K7QM2XPA\"", html);
        Assert.Contains("K7QM-2XPA", html);
    }

    [Fact]
    public void Expo_go_button_is_hidden_without_an_address()
    {
        var html = JoinPage.Render("K7QM2XPA", new InviteLinkOptions { ExpoGoUrl = "" });
        Assert.DoesNotContain("Expo Go", html);
    }

    [DbFact]
    public async Task Join_page_is_public_and_rejects_malformed_codes()
    {
        var client = factory.CreateClient();

        var ok = await client.GetAsync("/join/k7qm-2xpa");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("text/html", ok.Content.Headers.ContentType?.MediaType);
        Assert.Contains("code=K7QM2XPA", await ok.Content.ReadAsStringAsync());
        Assert.Contains("default-src 'none'", ok.Headers.GetValues("Content-Security-Policy").Single());

        var bad = await client.GetAsync("/join/%3Cscript%3E");
        Assert.Equal(HttpStatusCode.NotFound, bad.StatusCode);
        Assert.DoesNotContain("<script>", await bad.Content.ReadAsStringAsync());
    }
}
