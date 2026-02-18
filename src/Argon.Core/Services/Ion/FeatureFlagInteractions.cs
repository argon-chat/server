namespace Argon.Services.Ion;

using Argon.Core.Entities.Data;
using Grains.Interfaces;
using ion.runtime;

public class FeatureFlagInteractions : IFeatureFlagInteractions
{
    public async Task<IonArray<FeatureFlagData>> GetMyFeatureFlags(CancellationToken ct = default)
    {
        var grain = this.GetGrain<IFeatureFlagGrain>(Guid.Empty);

        var flags = await grain.EvaluateAllAsync(FeatureFlagEvaluationContext.ForUser(this.GetUserId(), this.GetUserCountry(), this.GetClientId()));

        return flags.Select(x => x.Value).Select(x => x.ToDto()).ToList();
    }
}