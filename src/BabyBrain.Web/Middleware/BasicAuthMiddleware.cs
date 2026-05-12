using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace BabyBrain.Web.Middleware;

public sealed class BasicAuthMiddleware
{
    private const string Realm = "BabyBrain Admin";

    private readonly RequestDelegate _next;
    private readonly string? _user;
    private readonly string? _password;
    private readonly ILogger<BasicAuthMiddleware> _logger;

    public BasicAuthMiddleware(RequestDelegate next, IConfiguration config, ILogger<BasicAuthMiddleware> logger)
    {
        _next = next;
        _logger = logger;
        _user = config["BABYBRAIN_ADMIN_USER"];
        _password = config["BABYBRAIN_ADMIN_PASSWORD"];

        if (string.IsNullOrEmpty(_user) || string.IsNullOrEmpty(_password))
        {
            _logger.LogWarning("BABYBRAIN_ADMIN_USER or BABYBRAIN_ADMIN_PASSWORD not set — /Admin will reject all requests");
        }
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (string.IsNullOrEmpty(_user) || string.IsNullOrEmpty(_password))
        {
            await Challenge(ctx);
            return;
        }

        var header = ctx.Request.Headers.Authorization.ToString();
        if (!AuthenticationHeaderValue.TryParse(header, out var parsed)
            || !string.Equals(parsed.Scheme, "Basic", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(parsed.Parameter))
        {
            await Challenge(ctx);
            return;
        }

        string decoded;
        try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(parsed.Parameter)); }
        catch (FormatException) { await Challenge(ctx); return; }

        var sep = decoded.IndexOf(':');
        if (sep < 0) { await Challenge(ctx); return; }

        var providedUser = decoded[..sep];
        var providedPassword = decoded[(sep + 1)..];

        if (!FixedTimeEquals(providedUser, _user) || !FixedTimeEquals(providedPassword, _password))
        {
            await Challenge(ctx);
            return;
        }

        await _next(ctx);
    }

    private static Task Challenge(HttpContext ctx)
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        ctx.Response.Headers.WWWAuthenticate = $"Basic realm=\"{Realm}\", charset=\"UTF-8\"";
        return Task.CompletedTask;
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var ab = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(ab, bb);
    }
}
