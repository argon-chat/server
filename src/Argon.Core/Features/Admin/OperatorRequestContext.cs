namespace Argon.Features.Admin;

public static class OperatorRequestContext
{
    private static readonly AsyncLocal<OperatorRequestContextData?> _current = new();

    public static OperatorRequestContextData Current
        => _current.Value ?? throw new InvalidOperationException("No active operator request context");

    public static OperatorRequestContextData? CurrentOrDefault => _current.Value;

    public static void Set(OperatorRequestContextData data) => _current.Value = data;
    internal static void Clear() => _current.Value = null;
}

public sealed class OperatorRequestContextData
{
    public required Guid   UserId                { get; init; }
    public required Guid   OperatorId            { get; init; }
    public required string Email                 { get; init; }
    public required string CertificateThumbprint { get; init; }
}
