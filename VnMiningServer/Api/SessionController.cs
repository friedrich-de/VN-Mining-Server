using System;
using System.Globalization;
using System.Net.Mime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using VnMiningServer.Access;

namespace VnMiningServer.Api;

/// <summary>
/// Signs a browser in and out. The page asks for the API key once and gets a cookie for it, which script
/// cannot read and which no other site can use.
/// </summary>
[ApiController]
[Route("Api/Session")]
public class SessionController : ControllerBase
{
    private readonly AppServices _services;

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionController"/> class.
    /// </summary>
    /// <param name="services">The server's services.</param>
    public SessionController(AppServices services)
    {
        _services = services;
    }

    /// <summary>
    /// Signs the browser in with the API key.
    /// </summary>
    /// <param name="signIn">The key.</param>
    /// <returns>Nothing: the answer sets the cookie.</returns>
    [HttpPost]
    [AllowAnonymous]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public ActionResult PostSession([FromBody] SignIn signIn)
    {
        var address = ClientAddress.Of(HttpContext);
        if (_services.Limiter.IsLocked(address, out var wait))
        {
            Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            return Problem("Too many wrong keys from this address. Try again in a minute.", statusCode: StatusCodes.Status429TooManyRequests);
        }

        if (!_services.Access.IsKey(signIn.Key?.Trim()))
        {
            _services.Limiter.Failed(address);
            return Problem("That is not this server's API key.", statusCode: StatusCodes.Status401Unauthorized);
        }

        var (value, ends) = _services.Access.NewSession();
        Response.Cookies.Append(ApiAccess.CookieName, value, Cookie(ends));
        Response.Headers.CacheControl = "no-store";
        return NoContent();
    }

    /// <summary>
    /// Signs the browser out.
    /// </summary>
    /// <returns>Nothing: the answer removes the cookie.</returns>
    [HttpDelete]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult DeleteSession()
    {
        Response.Cookies.Delete(ApiAccess.CookieName, Cookie(null));
        Response.Headers.CacheControl = "no-store";
        return NoContent();
    }

    private CookieOptions Cookie(DateTimeOffset? ends) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Secure = ClientAddress.IsHttps(HttpContext),
        Path = "/",
        Expires = ends,
        IsEssential = true,
    };
}

/// <summary>
/// What the page sends to sign in.
/// </summary>
public sealed record SignIn
{
    /// <summary>Gets the server's API key.</summary>
    public string? Key { get; init; }
}
