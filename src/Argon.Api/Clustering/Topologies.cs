namespace Argon.Api.Clustering;

using Argon.Features.Clustering;

public sealed class DistributedTopology : IArgonTopology
{
    public static string Name => "distributed";

    public static ArgonRoleId[] Roles =>
    [
        ArgonRoleId.EntryPoint, ArgonRoleId.BotApi, ArgonRoleId.Admin,
        ArgonRoleId.Core, ArgonRoleId.Voice, ArgonRoleId.Media, ArgonRoleId.Moderation,
        ArgonRoleId.Commerce, ArgonRoleId.Jobs
    ];

    public string Description => "clients and silos scaled independently";
}
