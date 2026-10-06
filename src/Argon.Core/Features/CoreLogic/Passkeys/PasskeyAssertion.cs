namespace Argon.Core.Features.CoreLogic.Passkeys;

using System.Buffers.Text;
using Fido2NetLib;
using Fido2NetLib.Objects;

/// <summary>Proving possession of one of the account's passkeys: the options to sign, and the check of what came back.</summary>
public static class PasskeyAssertion
{
    /// <returns>Assertion options JSON, or null when the account holds no passkey.</returns>
    public static async Task<string?> BeginAsync(ApplicationDbContext db, IFido2 fido2, Guid userId, CancellationToken ct)
    {
        var allowed = await db.Passkeys
           .AsNoTracking()
           .Where(p => p.UserId == userId && p.IsCompleted && !p.IsDeleted && p.CredentialId != null)
           .Select(p => new PublicKeyCredentialDescriptor(p.CredentialId!))
           .ToListAsync(ct);

        if (allowed.Count == 0)
            return null;

        return fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = allowed,
            UserVerification   = UserVerificationRequirement.Preferred
        }).ToJson();
    }

    /// <returns>The authenticator's response, or null when the text is not one.</returns>
    public static AuthenticatorAssertionRawResponse? Read(string responseJson)
        => string.IsNullOrWhiteSpace(responseJson)
            ? null
            : System.Text.Json.JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(responseJson);

    /// <returns>The passkey that signed, with its counter moved on and saved; null when the response
    /// names no passkey of this account.</returns>
    /// <exception cref="Fido2VerificationException">The signature does not verify.</exception>
    public static async Task<UserPasskeyEntity?> CompleteAsync(
        ApplicationDbContext db, IFido2 fido2, Guid userId, string optionsJson, AuthenticatorAssertionRawResponse response,
        CancellationToken ct)
    {
        var credentialId = Base64Url.DecodeFromChars(response.Id);
        var passkey = await db.Passkeys.FirstOrDefaultAsync(
            p => p.CredentialId != null && p.CredentialId == credentialId
                 && p.UserId == userId && p.IsCompleted && !p.IsDeleted, ct);

        if (passkey?.PublicKey is null)
            return null;

        var result = await fido2.MakeAssertionAsync(new MakeAssertionParams
        {
            AssertionResponse      = response,
            OriginalOptions        = AssertionOptions.FromJson(optionsJson),
            StoredPublicKey        = passkey.PublicKey,
            StoredSignatureCounter = passkey.SignCount,
            IsUserHandleOwnerOfCredentialIdCallback = (args, token) => db.Passkeys.AnyAsync(
                p => p.CredentialId != null && p.CredentialId == args.CredentialId && p.UserId == userId && !p.IsDeleted,
                token)
        }, ct);

        passkey.SignCount  = result.SignCount;
        passkey.LastUsedAt = DateTimeOffset.UtcNow;
        passkey.UpdatedAt  = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);

        return passkey;
    }
}
