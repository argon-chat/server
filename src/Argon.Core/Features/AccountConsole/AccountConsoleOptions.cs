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

    /// <summary>
    /// Directory the developer console is served from, and the <c>index.html</c> a client-routed
    /// path falls back to. Empty serves no static files at all, which is what a deployment that
    /// puts the console behind its own CDN wants — and what a local run wants, since the directory
    /// only exists inside the image.
    /// </summary>
    /// <remarks>
    /// The shipped image builds it into <c>/app/console</c>; see the <c>widget</c> stage in
    /// <c>src/Argon.Api/Dockerfile</c> and <c>src/Frontend/Console</c>.
    /// </remarks>
    public string StaticRoot { get; set; } = "";
}
