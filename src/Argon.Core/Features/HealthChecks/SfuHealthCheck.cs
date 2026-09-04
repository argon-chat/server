namespace Argon.HealthChecks;

using Argon.Sfu;
using Livekit.Server.Sdk.Dotnet;
using Microsoft.Extensions.Diagnostics.HealthChecks;

/// <summary>
/// Does LiveKit answer at the command URL with the credentials this process holds?
/// </summary>
/// <remarks>
/// <para>A room listing filtered to one name that no room has. Filtered, because an unfiltered
/// listing walks every active room on the server and a region can hold thousands of them — a probe
/// that runs every five seconds cannot be the most expensive call the server sees. With names given,
/// LiveKit looks those rooms up individually and answers with the empty set, and the answer still
/// proves what the probe is for: the URL, the API key, the secret the request is signed with, and
/// the server's willingness to talk to this key. A wrong secret is a <c>401</c> here and a silent
/// failure to join a call otherwise.</para>
///
/// <para>The client takes no cancellation token, so the base class's <c>WaitAsync</c> is what bounds
/// this one.</para>
/// </remarks>
public sealed class SfuHealthCheck(
    RoomServiceClient       rooms,
    IOptions<CallKitOptions> callKit,
    IOptions<ProbeOptions>   options) : DependencyHealthCheck(options)
{
    /// <summary>A name no real room carries, so the lookup is one miss rather than a scan.</summary>
    public const string ProbeRoom = "argon-probe-no-such-room";

    protected override async Task<HealthCheckResult> ProbeAsync(CancellationToken ct)
    {
        var sfu      = callKit.Value.Sfu;
        var response = await rooms.ListRooms(new ListRoomsRequest { Names = { ProbeRoom } });

        return HealthCheckResult.Healthy(
            $"LiveKit at {sfu.CommandUrl} accepted the API key",
            new Dictionary<string, object>
            {
                ["commandUrl"] = sfu.CommandUrl,
                ["region"]     = sfu.Region,
                ["matched"]    = response.Rooms.Count
            });
    }
}
