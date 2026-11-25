using System.Net.Mime;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using VnMiningServer.Access;

namespace VnMiningServer.Api;

/// <summary>
/// Hands the signed-in page the small text file a game reads to find this server.
/// </summary>
[ApiController]
[Route("Api/GameSettings")]
[Authorize]
public class GameSettingsController : ControllerBase
{
    /// <summary>The name a game looks for in its folder.</summary>
    internal const string FileName = "mining.txt";

    private readonly AppServices _services;

    /// <summary>
    /// Initializes a new instance of the <see cref="GameSettingsController"/> class.
    /// </summary>
    /// <param name="services">The server's services.</param>
    public GameSettingsController(AppServices services)
    {
        _services = services;
    }

    /// <summary>
    /// Builds the file for a game's folder: the address the page was opened at, and the API key.
    /// </summary>
    /// <returns>The file, as a download.</returns>
    [HttpGet]
    [Produces(MediaTypeNames.Text.Plain)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult> GetGameSettings()
    {
        var caller = await _services.Callers.ResolveAsync(HttpContext).ConfigureAwait(false);
        if (caller is null)
        {
            return Problem(StatusController.NoUser, statusCode: StatusCodes.Status403Forbidden);
        }

        var address = (ClientAddress.IsHttps(HttpContext) ? "https" : "http") + "://" + Request.Host.Value + Request.PathBase.Value;
        Response.Headers.CacheControl = "no-store";
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(Encoding.UTF8.GetBytes(Write(address, _services.Access.Key)), "text/plain; charset=utf-8", FileName);
    }

    /// <summary>
    /// The file's text.
    /// </summary>
    internal static string Write(string address, string key) =>
        "# Put this file into the game's folder. The game then sends every line it shows,\n"
        + "# with its voice and a screenshot, to the mining server below.\n"
        + "# Whoever has the key can read and fill the server's stream, so keep this file to yourself.\n"
        + "server = " + address + "\n"
        + "key = " + key + "\n";
}
