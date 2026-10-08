namespace Argon.Features;

using Api.Features.Orleans.Client;

public record ArgonDcClusterInfo(
    string dc,
    float effectivity,
    IServiceProvider serviceProvider,
    DateTime lastSeen,
    ArgonDataCenterStatus status,
    CancellationTokenSource ctSource);