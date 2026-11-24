using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SosaFam.Api.Data;

namespace SosaFam.Api.Auth;

public record TokenResponse(string AccessToken, DateTime AccessTokenExpiresAt, string RefreshToken, DateTime RefreshTokenExpiresAt);

public class TokenService(IOptions<JwtOptions> options, AppDbContext db, TimeProvider clock)
{
    private readonly JwtOptions _opt = options.Value;

    public async Task<TokenResponse> IssueAsync(AppUser user, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var accessExpires = now.AddMinutes(_opt.AccessTokenMinutes);
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opt.SigningKey));
        var jwt = new JwtSecurityToken(
            issuer: _opt.Issuer,
            audience: _opt.Audience,
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                new Claim("name", user.DisplayName ?? user.UserName ?? ""),
            ],
            notBefore: now,
            expires: accessExpires,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        var refresh = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        var refreshExpires = now.AddDays(_opt.RefreshTokenDays);
        db.RefreshTokens.Add(new RefreshToken { UserId = user.Id, TokenHash = Hash(refresh), ExpiresAt = refreshExpires });
        await db.SaveChangesAsync(ct);

        return new TokenResponse(new JwtSecurityTokenHandler().WriteToken(jwt), accessExpires, refresh, refreshExpires);
    }

    /// <summary>Exchanges a refresh token for new tokens. Each refresh token works once.</summary>
    public async Task<TokenResponse?> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var stored = await FindActiveAsync(refreshToken, ct);
        if (stored is null) return null;
        var user = await db.Users.FindAsync([stored.UserId], ct);
        if (user is null) return null;
        stored.RevokedAt = clock.GetUtcNow().UtcDateTime;
        return await IssueAsync(user, ct);
    }

    public async Task RevokeAsync(string refreshToken, CancellationToken ct)
    {
        var stored = await FindActiveAsync(refreshToken, ct);
        if (stored is null) return;
        stored.RevokedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
    }

    private async Task<RefreshToken?> FindActiveAsync(string refreshToken, CancellationToken ct)
    {
        var hash = Hash(refreshToken);
        var now = clock.GetUtcNow().UtcDateTime;
        return await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash && t.RevokedAt == null && t.ExpiresAt > now, ct);
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
