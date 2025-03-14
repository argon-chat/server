namespace Argon.Features.Vault;

using System.Diagnostics;

public static class VaultFeature
{
    public static void AddVaultConfiguration(this WebApplicationBuilder builder)
    {
        if (Environment.GetEnvironmentVariable("USE_VAULT") is null)
            return;
        var url   = Environment.GetEnvironmentVariable("ARGON_VAULT_URL");
        var space = Environment.GetEnvironmentVariable("ARGON_VAULT_SPACE") ?? "argon";
        var token = ReadToken();

        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(token) || string.IsNullOrEmpty(space))
            throw new Exception($"No url or token for vault defined");

        builder.AddVaultConfiguration(
            () => new VaultOptions(
            url, token, insecureConnection: false),
            "@", space);

        builder.Services.AddHostedService<VaultChangeWatcher>();
    }


    private static string ReadToken()
    {
        if (Environment.GetEnvironmentVariable("ARGON_VAULT_TOKEN") is { } str)
            return str;
        if (Environment.GetEnvironmentVariable("VAULT_TOKEN_FILE") is { } fileToken)
            return File.ReadAllText(fileToken).TrimEnd();
        throw new NotSupportedException("Vault not defined authorization method");
    }
}