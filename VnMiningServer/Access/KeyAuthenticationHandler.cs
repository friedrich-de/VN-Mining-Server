using System;
using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace VnMiningServer.Access;

/// <summary>
/// Lets a request in that carries the API key (<c>Authorization: Bearer</c>, as the game sends it) or the
/// cookie of a browser that showed the key before.
/// </summary>
internal sealed class KeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>The scheme's name.</summary>
    internal const string SchemeName = "Key";

    /// <summary>The name every caller with the key has. There is one owner.</summary>
    internal const string OwnerName = "owner";

    private const string Bearer = "Bearer ";
    private const string LockedItem = "vnm.locked";

    private readonly ApiAccess _access;
    private readonly FailureLimiter _limiter;

    /// <summary>
    /// Initializes a new instance of the <see cref="KeyAuthenticationHandler"/> class.
    /// </summary>
    /// <param name="options">The scheme's options.</param>
    /// <param name="logger">The logger factory.</param>
    /// <param name="encoder">The URL encoder.</param>
    /// <param name="access">The key.</param>
    /// <param name="limiter">Counts wrong keys.</param>
    public KeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ApiAccess access,
        FailureLimiter limiter)
        : base(options, logger, encoder)
    {
        _access = access;
        _limiter = limiter;
    }

    /// <summary>
    /// Whether a request that changes something and came with the cookie was sent by the page itself.
    /// A browser names the site a request comes from, and the cookie is not for other sites.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns><c>true</c> when the cookie may count.</returns>
    internal static bool SameSite(HttpRequest request)
    {
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method))
        {
            return true;
        }

        var origin = request.Headers.Origin.ToString();
        if (origin.Length == 0)
        {
            return true;
        }

        return Uri.TryCreate(origin, UriKind.Absolute, out var from)
            && string.Equals(from.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var address = ClientAddress.Of(Context);
        var header = Request.Headers.Authorization.ToString();
        if (header.StartsWith(Bearer, StringComparison.OrdinalIgnoreCase))
        {
            if (_limiter.IsLocked(address, out var wait))
            {
                Context.Items[LockedItem] = wait;
                return Task.FromResult(AuthenticateResult.Fail("Too many wrong keys."));
            }

            if (_access.IsKey(header[Bearer.Length..].Trim()))
            {
                return Task.FromResult(Owner());
            }

            _limiter.Failed(address);
            return Task.FromResult(AuthenticateResult.Fail("The key is wrong."));
        }

        if (Request.Cookies.TryGetValue(ApiAccess.CookieName, out var cookie) && _access.IsSession(cookie) && SameSite(Request))
        {
            return Task.FromResult(Owner());
        }

        return Task.FromResult(AuthenticateResult.NoResult());
    }

    /// <inheritdoc />
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (Context.Items.TryGetValue(LockedItem, out var locked) && locked is TimeSpan wait)
        {
            Response.StatusCode = StatusCodes.Status429TooManyRequests;
            Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            await Response.WriteAsJsonAsync(new { title = "Too Many Requests", status = 429, detail = "Too many wrong keys from this address. Try again in a minute." }, (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json").ConfigureAwait(false);
            return;
        }

        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers[HeaderNames.WWWAuthenticate] = "Bearer";
        await Response.WriteAsJsonAsync(new { title = "Unauthorized", status = 401, detail = "Sign in with the server's API key." }, (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json").ConfigureAwait(false);
    }

    private static AuthenticateResult Owner()
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, OwnerName)], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
