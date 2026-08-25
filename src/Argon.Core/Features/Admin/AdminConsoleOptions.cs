namespace Argon.Features.Admin;

/// <summary>
/// Where the operator console listens. A port of its own rather than a path on the public listener,
/// so a network policy can reach it without reasoning about routes.
/// </summary>
public sealed class AdminConsoleOptions
{
    [Range(1, 65535)]
    public int Port { get; set; } = 8920;
}
