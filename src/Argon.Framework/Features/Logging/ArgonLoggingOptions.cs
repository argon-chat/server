namespace Argon.Features.Logging;

/// <summary>
/// How this process logs. Serilog's own sinks and levels stay under <c>Serilog:</c>, which
/// <c>ReadFrom.Configuration</c> reads; this covers the choices the host makes around it.
/// </summary>
public sealed class ArgonLoggingOptions
{
    /// <summary>
    /// Whether to install Serilog at all and emit one structured line per request. Turning it off
    /// leaves the default console logger, which is what a person debugging locally usually wants.
    /// </summary>
    /// <remarks>Replaces the <c>NO_STRUCTURED_LOGS</c> environment variable.</remarks>
    public bool Structured { get; set; } = true;

    /// <summary>Whether the per-request summary line is emitted. Only meaningful when structured.</summary>
    public bool RequestLogging { get; set; } = true;
}
