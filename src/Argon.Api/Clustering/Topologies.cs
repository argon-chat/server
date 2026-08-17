namespace Argon.Api.Clustering;

using Argon.Features.Clustering;

public sealed class DistributedTopology : IArgonTopology
{
    public static string Name => "distributed";

    public static ArgonRoleId[] Roles =>
    [
        ArgonRoleId.EntryPoint, ArgonRoleId.BotApi, ArgonRoleId.Admin, ArgonRoleId.Account,
        ArgonRoleId.Core, ArgonRoleId.Voice, ArgonRoleId.Media, ArgonRoleId.Moderation,
        ArgonRoleId.Commerce, ArgonRoleId.Jobs
    ];

    public string Description => "clients and silos scaled independently";
}

/// <summary>
/// One process. What a developer runs, and what a small self-hosted instance can run.
/// </summary>
public sealed class SingleInstanceTopology : IArgonTopology
{
    public static string Name => "single-instance";

    public static ArgonRoleId[] Roles => [ArgonRoleId.Dev];

    public string Description => "everything co-hosted";
}
