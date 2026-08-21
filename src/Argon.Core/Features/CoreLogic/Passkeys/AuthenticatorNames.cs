namespace Argon.Core.Features.CoreLogic.Passkeys;

/// <summary>
/// Maps well-known FIDO2 AAGUIDs to human-readable authenticator names.
/// Source: https://github.com/passkeydeveloper/passkey-authenticator-aaguids
/// </summary>
public static class AuthenticatorNames
{
    private static readonly Dictionary<Guid, string> Known = new()
    {
        // YubiKey Series
        [Guid.Parse("cb69481e-8ff7-4039-93ec-0a2729a154a8")] = "YubiKey 5",
        [Guid.Parse("ee882879-721c-4913-9775-3dfcce97072a")] = "YubiKey 5 NFC",
        [Guid.Parse("2fc0579f-8113-47ea-b116-bb5a8db9202a")] = "YubiKey 5 NFC FIPS",
        [Guid.Parse("73bb0cd4-e502-49b8-9c6f-b59445bf720b")] = "YubiKey 5 Ci FIPS",
        [Guid.Parse("c5ef55ff-ad9a-4b9f-b580-adebafe026d0")] = "YubiKey 5Ci",
        [Guid.Parse("85203421-48f9-4355-9bc8-8a53846e5083")] = "YubiKey 5C",
        [Guid.Parse("f8a011f3-8c0a-4d15-8006-17111f9edc7d")] = "YubiKey 5C NFC",
        [Guid.Parse("b92c3f9a-c014-4056-887f-140a2501163b")] = "YubiKey 5C Nano",
        [Guid.Parse("a4e9fc6d-4cbe-4758-b8ba-37598bb5bbaa")] = "YubiKey Bio - FIDO Edition",
        [Guid.Parse("d8522d9f-575b-4866-88a9-ba99fa02f35b")] = "YubiKey Bio",
        [Guid.Parse("fa2b99dc-9e39-4257-8f92-4a30d23c4118")] = "YubiKey 5 NFC (USB-C)",
        [Guid.Parse("149a2021-8ef6-4133-96b8-81f8d5b7f1f5")] = "Security Key by Yubico (USB-A, NFC)",
        [Guid.Parse("6d44ba9b-f6ec-2e49-b930-0c8fe920cb73")] = "Security Key by Yubico (NFC)",
        [Guid.Parse("a25342c0-3cdc-4414-8e46-f4807fca511c")] = "Security Key by Yubico (USB-C, NFC)",

        // Google
        [Guid.Parse("42b4fb4a-2866-43b2-9bf7-6c6669c2e5d3")] = "Google Titan Security Key (USB-A/NFC)",
        [Guid.Parse("f4c63eff-d26c-4248-801c-3736c7eaa93a")] = "Google Titan Security Key (USB-C/NFC)",
        [Guid.Parse("ea9b8d66-4d01-1d21-3ce4-b6b48cb575d4")] = "Google Password Manager",
        [Guid.Parse("b5397723-31b1-4389-b6f1-0f884e529501")] = "Chrome on Android",
        [Guid.Parse("b5397666-4885-aa6b-cebf-e52262a439a2")] = "Chromium Browser",

        // Apple
        [Guid.Parse("fbfc3007-154e-4ecc-8c0b-6e020557d7bd")] = "Apple Passwords",
        [Guid.Parse("dd4ec289-e01d-41c9-bb89-70fa845d4bf2")] = "iCloud Keychain (Managed)",
        [Guid.Parse("adce0002-35bc-c60a-648b-0b25f1f05503")] = "Chrome on Mac",

        // Microsoft
        [Guid.Parse("08987058-cadc-4b81-b6e1-30de50dcbe96")] = "Windows Hello",
        [Guid.Parse("9ddd1817-af5a-4672-a2b9-3e3dd95000a9")] = "Windows Hello",
        [Guid.Parse("6028b017-b1d4-4c02-b4b3-afcdafc96bb2")] = "Windows Hello",
        [Guid.Parse("771b48fd-d3d4-4f74-9232-fc157ab0507a")] = "Edge on Mac",

        // 1Password
        [Guid.Parse("bada5566-a7aa-401f-bd96-45619a55120d")] = "1Password",
        [Guid.Parse("b84e4048-15dc-4dd0-8640-f4f60813c8af")] = "1Password",

        // Bitwarden
        [Guid.Parse("d548826e-79b4-db40-a3d8-11116f7e8349")] = "Bitwarden",

        // Dashlane
        [Guid.Parse("531126d6-e717-415c-9320-3d9aa6981239")] = "Dashlane",

        // Proton Pass
        [Guid.Parse("50726f74-6f6e-5061-7373-50726f746f6e")] = "Proton Pass",

        // KeePassXC
        [Guid.Parse("fdb141b2-5d84-443e-8a35-4698c205a502")] = "KeePassXC",

        // Keeper
        [Guid.Parse("0ea242b4-43c4-4a1b-8b17-dd6d0b6baec6")] = "Keeper",

        // SoloKeys
        [Guid.Parse("8876631b-d4a0-427f-5773-0ec71c9e0279")] = "SoloKey",
        [Guid.Parse("88bbd2f0-342a-42e7-8689-764f3e8e503b")] = "Solo 2",
    };

    /// <summary>
    /// Returns the human-readable name for a known AAGUID, or null if unknown.
    /// </summary>
    public static string? Lookup(Guid aaGuid)
        => Known.GetValueOrDefault(aaGuid);
}
