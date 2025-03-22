namespace Argon.Features;

public record ArgonDcClusterInfo(
    string dc,
    float effectivity,
    IServiceProvider serviceProvider,
    DateTime lastSeen,
    ArgonDataCenterStatus status,
    CancellationTokenSource ctSource);