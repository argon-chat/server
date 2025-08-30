namespace Argon.Features.Vault;

using Microsoft.Extensions.DependencyInjection;
using Serilog;
using VaultSharp;
using VaultSharp.V1.AuthMethods.Token;

public static class VaultFeature
{
    public static IConfigurationBuilder AddVaultConfiguration(this WebApplicationBuilder builder, bool includeWatcher = true)
    {
        if (Environment.GetEnvironmentVariable("USE_VAULT") is null)
            return builder.Configuration;
        var url   = Environment.GetEnvironmentVariable("ARGON_VAULT_URL");
        var space = Environment.GetEnvironmentVariable("ARGON_VAULT_SPACE") ?? "argon";
        var token = ReadToken();

        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(token) || string.IsNullOrEmpty(space))
            throw new Exception($"No url or token for vault defined");
        var cfg = builder.AddVaultConfiguration(
            () => new VaultOptions(
                url, token, insecureConnection: false),
            "@", space);

        if (includeWatcher)
            builder.Services.AddHostedService<VaultChangeWatcher>();

        return cfg;
    }

    public static IServiceCollection AddVaultClient(this WebApplicationBuilder builder)
    {
        if (Environment.GetEnvironmentVariable("USE_VAULT") is null)
            return builder.Services;

        var url   = Environment.GetEnvironmentVariable("ARGON_VAULT_URL");
        var token = ReadToken();

        builder.Services.AddSingleton<IVaultClient>(x =>
        {
            var vaultClientSettings = new VaultClientSettings(url, new TokenAuthMethodInfo(token))
            {
                UseVaultTokenHeaderInsteadOfAuthorizationHeader = true,

                PostProcessHttpClientHandlerAction = handler =>
                {
                    if (handler is not HttpClientHandler clientHandler) return;
                    clientHandler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
                }
            };
            return new VaultClient(vaultClientSettings);
        });
        return builder.Services;
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