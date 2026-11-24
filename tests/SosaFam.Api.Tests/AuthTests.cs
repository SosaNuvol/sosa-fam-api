using System.Net;
using System.Net.Http.Json;
using SosaFam.Api.Auth;

namespace SosaFam.Api.Tests;

public class AuthTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [DbFact]
    public async Task Requests_without_a_token_are_rejected()
    {
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/households")).StatusCode);
    }

    [DbFact]
    public async Task Login_with_wrong_password_fails()
    {
        var client = factory.CreateClient();
        var email = $"test-{Guid.NewGuid():N}@example.test";
        (await client.PostAsJsonAsync("/auth/register", new { email, password = "correct-horse-battery", displayName = "Dana" })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/auth/login", new { email, password = "wrong-password-123" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/auth/login", new { email, password = "correct-horse-battery" })).StatusCode);
    }

    [DbFact]
    public async Task Refresh_tokens_work_once()
    {
        var (client, tokens) = await factory.NewUserAsync("Dana");

        var first = await client.PostAsJsonAsync("/auth/refresh", new { refreshToken = tokens.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var renewed = (await first.Content.ReadFromJsonAsync<TokenResponse>())!;
        Assert.NotEqual(tokens.RefreshToken, renewed.RefreshToken);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/auth/refresh", new { refreshToken = tokens.RefreshToken })).StatusCode);

        (await client.PostAsJsonAsync("/auth/logout", new { refreshToken = renewed.RefreshToken })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/auth/refresh", new { refreshToken = renewed.RefreshToken })).StatusCode);
    }
}
