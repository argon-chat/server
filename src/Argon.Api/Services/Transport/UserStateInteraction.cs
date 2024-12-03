namespace Argon.Services;

public class UserPreferenceInteraction(IGrainFactory grainFactory) : IUserPreferenceInteraction
{
    public async Task SavePreferences(Symbol scope, string state)
    {
        var user = this.GetUser();
        await grainFactory.GetGrain<IUserPreferenceGrain>(user.id).SavePreferences(scope, state);
    }

    public async Task<string> LoadPreferences(Symbol scope)
    {
        var user = this.GetUser();
        return await grainFactory.GetGrain<IUserPreferenceGrain>(user.id).LoadPreferences(scope);
    }
}