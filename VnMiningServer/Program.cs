using System;
using System.Threading.Tasks;

namespace VnMiningServer;

/// <summary>
/// Starts the server.
/// </summary>
internal static class Program
{
    private static async Task Main()
    {
        var app = ServerHost.Build(ServerOptions.FromEnvironment(Environment.GetEnvironmentVariable));
        await using (app.ConfigureAwait(false))
        {
            await app.RunAsync().ConfigureAwait(false);
        }
    }
}
