using System.Net.Mime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace VnMiningServer.Api;

/// <summary>
/// Serves the page at the server's address. The page's files are the same for everyone and hold no data,
/// so they need no key. Everything the page then asks for does.
/// </summary>
[ApiController]
[AllowAnonymous]
public class PageController : ControllerBase
{
    /// <summary>
    /// What the page may do: run its own script and styles, show pictures and play sound from this server or
    /// made in the page, and call this server and an AnkiConnect on the user's own device. Nothing from
    /// elsewhere, and no other site may frame it. An AnkiConnect anywhere else is called by the server.
    /// </summary>
    internal const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' blob: data:; media-src 'self' blob:; "
        + "font-src 'self'; connect-src 'self' " + DeviceAddresses + "; manifest-src 'self'; worker-src 'self'; "
        + "frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

    /// <summary>
    /// The addresses of the user's own device, on any port: where desktop Anki's AnkiConnect, and the Android
    /// app's, listen. Not [::1]: browsers throw an IPv6 address out of a policy as a source they cannot read.
    /// </summary>
    internal const string DeviceAddresses =
        "http://127.0.0.1:* http://localhost:* https://127.0.0.1:* https://localhost:*";

    private readonly AppServices _services;

    /// <summary>
    /// Initializes a new instance of the <see cref="PageController"/> class.
    /// </summary>
    /// <param name="services">The server's services.</param>
    public PageController(AppServices services)
    {
        _services = services;
    }

    /// <summary>
    /// Serves the page. It asks for the API key once and then calls the server's endpoints.
    /// </summary>
    /// <returns>The page.</returns>
    [HttpGet("/")]
    [Produces(MediaTypeNames.Text.Html)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetPage()
    {
        if (!_services.Page.HasPage)
        {
            return Problem("This build of the server holds no page. Build it with \"npm run build\" in web/ first.", statusCode: StatusCodes.Status404NotFound);
        }

        return GetFile(string.Empty);
    }

    /// <summary>
    /// Serves one of the page's files: its script, styles, icons, app manifest or service worker.
    /// </summary>
    /// <param name="path">The file's path below the server's address.</param>
    /// <returns>The file, or 404 for anything that is not one of the page's files.</returns>
    [HttpGet("/{**path}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetFile([FromRoute] string? path)
    {
        var file = _services.Page.Find(path);
        var stream = file is null ? null : _services.Page.Open(file);
        if (file is null || stream is null)
        {
            return NotFound();
        }

        // A file with a hash in its name never changes. The others change with the server's version, so the
        // browser should always ask again.
        Response.Headers.CacheControl = file.Immutable ? "public, max-age=31536000, immutable" : "no-cache";
        Response.Headers.XContentTypeOptions = "nosniff";

        // The page holds the user's sign-in, so it must not load anything from elsewhere, sit inside
        // another site's frame, or tell another site where it was opened from.
        Response.Headers.ContentSecurityPolicy = ContentSecurityPolicy;
        Response.Headers["Referrer-Policy"] = "no-referrer";
        return File(stream, file.ContentType);
    }
}
