//namespace Argon.Grains;

//using Persistence.States;

//public class UserPreferenceGrain : Grain<UserPreferencesGrainState>, IUserPreferenceGrain
//{
//    private const int MaxSymbolSize = 1024 * 64;

//    public override Task OnActivateAsync(CancellationToken cancellationToken)
//        => ReadStateAsync();

//    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
//        => WriteStateAsync();

//    public Task SavePreferences(string scope, string value)
//    {
//        if (value.Length * 2 > MaxSymbolSize)
//            return Task.CompletedTask; // ignore
//        if (State.UserPreferences.Count > 10)
//            return Task.CompletedTask;
//        State.UserPreferences[scope] = value;
//        return Task.CompletedTask;
//    }

//    public Task<string> LoadPreferences(string scope)
//    {
//        if (State.UserPreferences.TryGetValue(scope, out var result))
//            return Task.FromResult(result);
//        return Task.FromResult(string.Empty);
//    }
//}