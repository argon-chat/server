namespace Argon;

[TsInterface]
public interface IUserPreferenceInteraction : IArgonService
{
    Task         SavePreferences(Symbol scope, string state);
    Task<string> LoadPreferences(Symbol scope);
}