namespace Argon.Features.Vault;

using Env;

public static class VaultFeature
{
    public static void AddVaultConfiguration(this WebApplicationBuilder builder)
    {
        if (builder.Environment.IsSingleInstance())
            return;
        var url   = Environment.GetEnvironmentVariable("ARGON_VAULT_URL");
        var token = Environment.GetEnvironmentVariable("ARGON_VAULT_TOKEN");


        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(token))
            throw new Exception($"No url or token for vault defined");

        builder.AddVaultConfiguration(
            () => new VaultOptions(
            url, token, insecureConnection: false),
            "@", "argon");

        builder.Services.AddHostedService<VaultChangeWatcher>();
    }
}