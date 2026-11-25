using System.Net.Mime;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace VnMiningServer.Api;

/// <summary>
/// Tells the page what it is talking to. Everything under <c>/Api</c> but signing in needs the API key or
/// the cookie of a browser that showed it.
/// </summary>
[ApiController]
[Route("Api")]
[Authorize]
public class StatusController : ControllerBase
{
    /// <summary>What a caller without the key is told.</summary>
    internal const string NoUser = "Sign in with the server's API key to use this page.";

    private readonly AppServices _services;

    /// <summary>
    /// Initializes a new instance of the <see cref="StatusController"/> class.
    /// </summary>
    /// <param name="services">The server's services.</param>
    public StatusController(AppServices services)
    {
        _services = services;
    }

    /// <summary>
    /// Says what the page is talking to. The page also learns from the answer whether it is signed in.
    /// </summary>
    /// <returns>The status.</returns>
    [HttpGet("Status")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<StatusView>> GetStatus()
    {
        var caller = await _services.Callers.ResolveAsync(HttpContext).ConfigureAwait(false);
        if (caller is null)
        {
            return Problem(NoUser, statusCode: StatusCodes.Status403Forbidden);
        }

        Response.Headers.CacheControl = "no-store";
        return new StatusView
        {
            Version = AppServices.Version,
            Lines = _services.Lines.Count(),
            MaxLines = _services.Shared.MaxLines,
        };
    }
}
