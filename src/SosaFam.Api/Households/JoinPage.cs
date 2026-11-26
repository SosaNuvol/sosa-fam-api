using System.Net;
using System.Text;

namespace SosaFam.Api.Households;

/// <summary>Where invite links point. See <see cref="JoinPage"/>.</summary>
public class InviteLinkOptions
{
    /// <summary>The installed app's URL scheme (app.json "scheme").</summary>
    public string AppScheme { get; set; } = "sosafam";

    /// <summary>Optional Expo Go address of the dev server, e.g. exps://family-dev.nuvol9.com. Empty hides the button.</summary>
    public string? ExpoGoUrl { get; set; }
}

/// <summary>
/// GET /join/{code}: the web page an invite link opens. Messaging apps only make https links tappable,
/// so the app shares this page, and the page opens the app at join?code=… (which then has the person
/// create an account or sign in, and joins them to the family).
/// The page looks nothing up: it does not say whether the code is valid or whose family it is.
/// </summary>
public static class JoinPage
{
    public static void MapJoinPage(this IEndpointRouteBuilder app)
    {
        app.MapGet("/join/{code}", (string code, IConfiguration config, HttpResponse response) =>
        {
            // No scripts, no outside resources: only the inline styles below.
            response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
            response.Headers.XContentTypeOptions = "nosniff";
            response.Headers["Referrer-Policy"] = "no-referrer";
            response.Headers.CacheControl = "no-store";
            var options = config.GetSection("InviteLinks").Get<InviteLinkOptions>() ?? new InviteLinkOptions();
            var clean = InviteCodes.Normalize(code);
            if (clean.Length != 8 || !clean.All(char.IsAsciiLetterOrDigit))
                return Results.Content(Render(null, options), "text/html; charset=utf-8", statusCode: 404);
            return Results.Content(Render(clean, options), "text/html; charset=utf-8");
        }).ExcludeFromDescription();
    }

    public static string Render(string? code, InviteLinkOptions options)
    {
        static string H(string s) => WebUtility.HtmlEncode(s);
        var body = new StringBuilder();
        if (code is null)
        {
            body.Append("<h1>This invite link is not valid</h1><p>Ask for a new invite.</p>");
        }
        else
        {
            var shown = $"{code[..4]}-{code[4..]}";
            var appUrl = $"{options.AppScheme}://join?code={code}";
            body.Append("<h1>You're invited to Sosa Fam</h1>")
                .Append("<p>Open the app to create your account, or sign in. You'll join the family automatically.</p>")
                .Append($"<a class=\"btn\" href=\"{H(appUrl)}\">Open the Sosa Fam app</a>");
            if (!string.IsNullOrWhiteSpace(options.ExpoGoUrl))
            {
                var expoUrl = $"{options.ExpoGoUrl.TrimEnd('/')}/--/join?code={code}";
                body.Append($"<a class=\"btn secondary\" href=\"{H(expoUrl)}\">Open in Expo Go</a>");
            }
            body.Append($"<p class=\"muted\">If the app doesn't open, open it yourself and enter this code:</p><p class=\"code\">{H(shown)}</p>");
        }

        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="robots" content="noindex">
            <title>Join a family on Sosa Fam</title>
            <style>
            body { font-family: system-ui, sans-serif; margin: 0; padding: 32px 24px; color: #1B1F24; max-width: 480px; margin-inline: auto; }
            h1 { font-size: 26px; }
            p { font-size: 16px; line-height: 1.4; }
            .muted { color: #5E6670; }
            .btn { display: block; text-align: center; padding: 14px; margin: 12px 0; border-radius: 10px; background: #2F6FED; color: #fff; font-weight: 600; text-decoration: none; font-size: 16px; }
            .btn.secondary { background: #fff; color: #2F6FED; border: 1px solid #2F6FED; }
            .code { font-size: 32px; font-weight: 700; letter-spacing: 4px; text-align: center; }
            </style>
            </head>
            <body>
            {{body}}
            </body>
            </html>
            """;
    }
}
