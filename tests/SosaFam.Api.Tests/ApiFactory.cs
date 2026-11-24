using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using SosaFam.Api.Auth;

namespace SosaFam.Api.Tests;

/// <summary>
/// Runs the API in memory against a real SQL Server database that already has the
/// sosa-fam-database scripts applied. Set SOSAFAM_TEST_DB to its connection string;
/// without it, these tests are skipped.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    public static string? ConnectionString => Environment.GetEnvironmentVariable("SOSAFAM_TEST_DB");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:SosaFam", ConnectionString);
        builder.UseSetting("Jwt:SigningKey", "test-signing-key-that-is-at-least-32-bytes-long");
    }

    /// <summary>Registers a new user with a unique email and returns a client signed in as them.</summary>
    public async Task<(HttpClient Client, TokenResponse Tokens)> NewUserAsync(string displayName)
    {
        var client = CreateClient();
        var email = $"test-{Guid.NewGuid():N}@example.test";
        var res = await client.PostAsJsonAsync("/auth/register", new { email, password = "correct-horse-battery", displayName });
        res.EnsureSuccessStatusCode();
        var tokens = (await res.Content.ReadFromJsonAsync<TokenResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return (client, tokens);
    }
}

/// <summary>A test that needs SQL Server; skipped when SOSAFAM_TEST_DB is not set.</summary>
public sealed class DbFactAttribute : FactAttribute
{
    public DbFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(ApiFactory.ConnectionString))
            Skip = "Set SOSAFAM_TEST_DB to a migrated SQL Server database to run integration tests.";
    }
}
