namespace Argon.Api.Services.Fusion;

using ActualLab.Text;
using Contracts;
using Grains.Interfaces;

public class UserPreferenceInteraction(IFusionContext fusionContext, IGrainFactory grainFactory) : IUserPreferenceInteraction
{
    public async Task SavePreferences(Symbol scope, string state)
    {
        var user = await fusionContext.GetUserDataAsync();
        await grainFactory.GetGrain<IUserPreferenceGrain>(user.id).SavePreferences(scope, state);
    }

    public async Task<string> LoadPreferences(Symbol scope)
    {
        var user = await fusionContext.GetUserDataAsync();
        return await grainFactory.GetGrain<IUserPreferenceGrain>(user.id).LoadPreferences(scope);
    }
}