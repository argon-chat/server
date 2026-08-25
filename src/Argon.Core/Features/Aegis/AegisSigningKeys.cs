namespace Argon.Features.Aegis;

using Argon.Features.Jwt;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Server;

/// <summary>
/// Replaces OpenIddict's placeholder keys with Argon's real ones.
/// </summary>
/// <remarks>
/// Post-configuration rather than configuration, and that is the whole point: the keys come out of
/// Vault through <see cref="WrapperForSignKey"/>, which cannot be resolved while the container is
/// still being built. The provider is registered with ephemeral keys so it is well-formed at
/// startup, and this swaps them for the ones tokens are actually signed with the first time the
/// options are read.
/// <para>
/// Signing and encryption are different keys for different audiences: a resource server verifies the
/// signature, so that key's public half is published; the encryption key protects what only this
/// server reads back — authorization codes, refresh tokens, identity tokens — and is never shared.
/// </para>
/// </remarks>
public sealed class AegisSigningKeys(IServiceProvider serviceProvider) : IPostConfigureOptions<OpenIddictServerOptions>
{
    public void PostConfigure(string? name, OpenIddictServerOptions options)
    {
        using var scope = serviceProvider.CreateScope();

        var signing    = scope.ServiceProvider.GetRequiredService<WrapperForSignKey>();
        var encryption = scope.ServiceProvider.GetRequiredService<WrapperForEncryptionKey>();

        options.SigningCredentials.Clear();
        options.SigningCredentials.Add(new SigningCredentials(signing.PrivateKey, signing.Algorithm));

        options.EncryptionCredentials.Clear();
        options.EncryptionCredentials.Add(new EncryptingCredentials(
            encryption.PrivateKey,
            SecurityAlgorithms.RsaOAEP,
            SecurityAlgorithms.Aes256CbcHmacSha512));
    }
}
