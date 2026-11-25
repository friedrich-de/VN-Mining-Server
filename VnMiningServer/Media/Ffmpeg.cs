using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace VnMiningServer.Media;

/// <summary>
/// Runs ffmpeg.
/// </summary>
internal interface IFfmpeg
{
    /// <summary>
    /// Runs ffmpeg once and waits for it. It is stopped when the time limit passes or the caller gives up.
    /// </summary>
    /// <param name="arguments">The arguments.</param>
    /// <param name="limit">The longest it may run.</param>
    /// <param name="cancellationToken">Stops the run.</param>
    /// <returns>Whether ffmpeg ended without an error.</returns>
    Task<bool> RunAsync(IReadOnlyList<string> arguments, TimeSpan limit, CancellationToken cancellationToken);

    /// <summary>
    /// Runs ffmpeg once and returns what it printed, for questions about what it can do.
    /// </summary>
    /// <param name="arguments">The arguments.</param>
    /// <param name="cancellationToken">Stops the run.</param>
    /// <returns>The output, or <c>null</c> when ffmpeg could not be run.</returns>
    Task<string?> AskAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

/// <summary>
/// Runs the ffmpeg installed next to the server.
/// </summary>
internal sealed class SystemFfmpeg : IFfmpeg
{
    private readonly string _path;
    private readonly ILogger<SystemFfmpeg> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SystemFfmpeg"/> class.
    /// </summary>
    /// <param name="path">The ffmpeg to run: a path, or a name the system finds.</param>
    /// <param name="logger">The logger.</param>
    public SystemFfmpeg(string path, ILogger<SystemFfmpeg> logger)
    {
        _path = path;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> RunAsync(IReadOnlyList<string> arguments, TimeSpan limit, CancellationToken cancellationToken)
    {
        var (code, _, errors) = await StartAsync(arguments, limit, cancellationToken).ConfigureAwait(false);
        if (code != 0)
        {
            _logger.LogWarning("ffmpeg ended with code {Code}: {Errors}", code, errors);
        }

        return code == 0;
    }

    /// <inheritdoc />
    public async Task<string?> AskAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var (code, output, _) = await StartAsync(arguments, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        return code == 0 ? output : null;
    }

    private async Task<(int? Code, string Output, string Errors)> StartAsync(IReadOnlyList<string> arguments, TimeSpan limit, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(_path)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);
        try
        {
            process.Start();
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return (process.ExitCode, await output.ConfigureAwait(false), await errors.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It ended by itself in the meantime.
            }

            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogWarning("ffmpeg did not finish within {Seconds} seconds and was stopped", limit.TotalSeconds);
            return (null, string.Empty, string.Empty);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            _logger.LogWarning(ex, "ffmpeg could not be run");
            return (null, string.Empty, string.Empty);
        }
    }
}
