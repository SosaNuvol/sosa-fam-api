namespace SosaFam.Api.Auth;

public class JwtOptions
{
    public string Issuer { get; set; } = "sosa-fam-api";
    public string Audience { get; set; } = "sosa-fam-apps";
    /// <summary>HMAC-SHA256 key, at least 32 bytes. Keep it in user secrets or an environment
    /// variable (Jwt__SigningKey), never in source control.</summary>
    public string SigningKey { get; set; } = "";
    public int AccessTokenMinutes { get; set; } = 30;
    public int RefreshTokenDays { get; set; } = 30;
}
