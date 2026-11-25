using System;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http;

namespace VnMiningServer.Access;

/// <summary>
/// What a request says about where it comes from. Behind a tunnel or a reverse proxy the connection comes
/// from the proxy, which names the caller in a header. Only a proxy on this machine or in a private network
/// is believed.
/// </summary>
internal static class ClientAddress
{
    /// <summary>
    /// The address wrong keys are counted by.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns>The caller's address as text.</returns>
    public static string Of(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (IsNear(remote))
        {
            var named = First(context.Request.Headers["CF-Connecting-IP"].ToString());
            if (named is not null && IPAddress.TryParse(named, out var parsed))
            {
                return parsed.ToString();
            }

            var forwarded = Forwarded(context.Request.Headers["X-Forwarded-For"].ToString());
            if (forwarded is not null)
            {
                return forwarded;
            }
        }

        return remote?.ToString() ?? "unknown";
    }

    /// <summary>
    /// Whether the browser reached the server over HTTPS, directly or through a proxy that says so.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns><c>true</c> for HTTPS.</returns>
    public static bool IsHttps(HttpContext context)
    {
        if (context.Request.IsHttps)
        {
            return true;
        }

        return IsNear(context.Connection.RemoteIpAddress)
            && string.Equals(First(context.Request.Headers["X-Forwarded-Proto"].ToString()), "https", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether an address is this machine or in a private network.
    /// </summary>
    /// <param name="address">The address.</param>
    /// <returns><c>true</c> for a near address, and for none at all (a test server has none).</returns>
    internal static bool IsNear(IPAddress? address)
    {
        if (address is null)
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127);
        }

        return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;
    }

    /// <summary>
    /// The caller in an X-Forwarded-For header. Every proxy on the way adds the address it saw to the end, so
    /// the header is read from its end: past the proxies in this machine's own networks, up to the first
    /// address from outside. What stands before that one the caller wrote itself, and it is not believed.
    /// </summary>
    /// <param name="header">The header's value.</param>
    /// <returns>The address, or <c>null</c> when the header names none.</returns>
    internal static string? Forwarded(string header)
    {
        string? found = null;
        var parts = header.Split(',');
        for (var index = parts.Length - 1; index >= 0; index--)
        {
            if (!IPAddress.TryParse(parts[index].Trim(), out var address))
            {
                break;
            }

            found = address.ToString();
            if (!IsNear(address))
            {
                break;
            }
        }

        return found;
    }

    private static string? First(string header)
    {
        var comma = header.IndexOf(',', StringComparison.Ordinal);
        var first = (comma == -1 ? header : header[..comma]).Trim();
        return first.Length == 0 ? null : first;
    }
}
