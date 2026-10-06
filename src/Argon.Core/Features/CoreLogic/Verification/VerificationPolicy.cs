namespace Argon.Core.Features.CoreLogic.Verification;

public readonly record struct EnrolledFactors(bool Password, bool Totp, bool Passkey);

/// <summary>What a sensitive action asks the account holder to prove, in the order the client walks it.</summary>
/// <remarks>
/// Each requirement is satisfied by any one of its factors. A new action is a new <see cref="SensitiveAction"/>
/// member, a row here and a line in <see cref="Describe"/>; the flow itself does not change.
/// </remarks>
public static class VerificationPolicy
{
    public static List<VerificationFactor[]>? For(SensitiveAction action, EnrolledFactors enrolled) => action switch
    {
        // Possession of the current address, or a factor at least as strong, so a stolen session and
        // password are not enough to move the account's recovery channel.
        SensitiveAction.CHANGE_EMAIL => [..Knowledge(enrolled), Possession(enrolled)],
        _                            => null
    };

    public static string Describe(SensitiveAction action) => action switch
    {
        SensitiveAction.CHANGE_EMAIL => "change the email address of your account",
        _                            => "make a sensitive change to your account"
    };

    private static List<VerificationFactor[]> Knowledge(EnrolledFactors enrolled)
        => enrolled.Password ? [[VerificationFactor.PASSWORD]] : [];

    private static VerificationFactor[] Possession(EnrolledFactors enrolled)
    {
        List<VerificationFactor> factors = [VerificationFactor.EMAIL_CODE];

        if (enrolled.Totp)
            factors.Add(VerificationFactor.TOTP);
        if (enrolled.Passkey)
            factors.Add(VerificationFactor.PASSKEY);

        return [..factors];
    }
}

public static class ContactMask
{
    public static string Email(string email)
    {
        var at = email.LastIndexOf('@');

        return at <= 0 ? "***" : $"{email[0]}***{email[at..]}";
    }
}
