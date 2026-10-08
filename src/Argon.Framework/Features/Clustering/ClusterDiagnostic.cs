namespace Argon.Features.Clustering;

public enum ClusterDiagnosticSeverity
{
    Warning,
    Error
}

/// <summary>
/// One validation finding. <see cref="Code"/> is the rule identifier — E1..E9 for errors,
/// W1..W3 for warnings; the rules themselves are documented on <see cref="ClusterValidator"/>.
/// </summary>
public sealed record ClusterDiagnostic(
    string                    Code,
    ClusterDiagnosticSeverity Severity,
    string                    Message,
    ArgonRoleId?              Role   = null,
    string?                   Target = null)
{
    public static ClusterDiagnostic Error(string code, string message, ArgonRoleId? role = null, string? target = null)
        => new(code, ClusterDiagnosticSeverity.Error, message, role, target);

    public static ClusterDiagnostic Warning(string code, string message, ArgonRoleId? role = null, string? target = null)
        => new(code, ClusterDiagnosticSeverity.Warning, message, role, target);

    public override string ToString()
    {
        var prefix = Severity is ClusterDiagnosticSeverity.Error ? "error" : "warning";
        var where  = Role is { } r ? $" [{r}]" : string.Empty;
        return $"{prefix} {Code}{where}: {Message}";
    }
}
