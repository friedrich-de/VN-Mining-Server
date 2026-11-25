using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using VnMiningServer.Access;

namespace VnMiningServer.Api;

/// <summary>
/// Who is calling, as far as the server's rules need to know.
/// </summary>
/// <param name="UserId">The id the caller's settings are kept under.</param>
/// <param name="Name">The caller's name.</param>
/// <param name="IsAdministrator">Whether the caller may change the server's dictionaries and sources. The owner may.</param>
internal sealed record Caller(Guid UserId, string Name, bool IsAdministrator);

/// <summary>
/// Finds out who sent a request.
/// </summary>
internal interface ICallerResolver
{
    /// <summary>
    /// The user behind the request, or <c>null</c> when it came without the key.
    /// </summary>
    Task<Caller?> ResolveAsync(HttpContext context);
}

/// <summary>
/// The server has one user, its owner: whoever holds the API key. A request that was let in is the owner's.
/// </summary>
internal sealed class OwnerResolver : ICallerResolver
{
    /// <summary>The owner's id, under which the settings are kept.</summary>
    internal static readonly Guid OwnerId = new("00000000-0000-0000-0000-000000000001");

    /// <summary>The owner.</summary>
    internal static readonly Caller Owner = new(OwnerId, KeyAuthenticationHandler.OwnerName, true);

    /// <inheritdoc />
    public Task<Caller?> ResolveAsync(HttpContext context) =>
        Task.FromResult(context.User.Identity?.IsAuthenticated == true ? Owner : null);
}
