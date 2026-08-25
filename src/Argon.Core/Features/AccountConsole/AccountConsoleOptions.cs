namespace Argon.Features.AccountConsole;

/// <summary>
/// Where the developer account console listens, and how long it trusts a team-membership answer.
/// </summary>
public sealed class AccountConsoleOptions
{
    [Range(1, 65535)]
    public int Port { get; set; } = 8930;

    /// <summary>
    /// How long a membership or ownership check is cached in process. A revoked membership stays
    /// usable on an already-warm node for this long, so it trades a database round trip per console
    /// call against how quickly removing someone takes effect.
    /// </summary>
    public TimeSpan AccessCacheTtl { get; set; } = TimeSpan.FromMinutes(50);
}
