namespace Argon.Api.Grains;

using ActualLab.Text;
using Interfaces;
using Persistence.States;

public class UserPreferenceGrain([PersistentState("userPreferences", "OrleansStorage")]
    IPersistentState<UserPreferencesGrainState> state) : Grain, IUserPreferenceGrain
{
    public override Task OnActivateAsync(CancellationToken cancellationToken)
        => state.ReadStateAsync();

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        => state.WriteStateAsync();

    public Task SavePreferences(Symbol scope, string value)
    {
        state.State.UserPreferences[scope] = value;
        return Task.CompletedTask;
    }

    public Task<string> LoadPreferences(Symbol scope)
    {
        if (state.State.UserPreferences.TryGetValue(scope, out var result))
            return Task.FromResult(result);
        return Task.FromResult(string.Empty);
    }
}