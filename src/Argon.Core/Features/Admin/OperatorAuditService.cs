namespace Argon.Features.Admin;

public interface IOperatorAuditService
{
    Task LogAsync(string action, string? targetType = null, string? targetId = null, string? details = null);
}

/// <summary>
/// Writes the operator audit line for whoever is calling the console.
/// </summary>
/// <remarks>
/// The operator is read here, from the ambient <see cref="OperatorRequestContext"/>, and handed to
/// <see cref="IAdminOperatorsGrain"/> explicitly — the context does not cross a grain call, and the
/// console's role opens no database connection of its own. Reading the log back is the grain's too.
/// </remarks>
public sealed class OperatorAuditService(
    IGrainFactory grainFactory,
    ILogger<OperatorAuditService> logger)
    : IOperatorAuditService
{
    public async Task LogAsync(string action, string? targetType, string? targetId, string? details)
    {
        var caller = OperatorRequestContext.CurrentOrDefault;
        if (caller is null)
        {
            logger.LogWarning("Audit log attempted without operator context for action={Action}", action);
            return;
        }

        await grainFactory.GetGrain<IAdminOperatorsGrain>(Guid.Empty)
           .AppendAuditAsync(caller.OperatorId, caller.Email, action, targetType, targetId, details);
    }
}
