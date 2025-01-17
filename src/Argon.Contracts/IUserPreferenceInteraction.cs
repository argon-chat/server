namespace Argon;

[TsInterface]
public interface IUserPreferenceInteraction : IArgonService
{
    Task         SavePreferences(string scope, string state);
    Task<string> LoadPreferences(string scope);
}