namespace VnMiningServer.Api;

/// <summary>
/// What the page learns when it opens: what it is talking to and how full the stream is.
/// </summary>
public sealed record StatusView
{
    /// <summary>Gets the server's version.</summary>
    public required string Version { get; init; }

    /// <summary>Gets how many lines the stream holds.</summary>
    public required int Lines { get; init; }

    /// <summary>Gets how many lines the stream keeps before the oldest go.</summary>
    public required int MaxLines { get; init; }
}
