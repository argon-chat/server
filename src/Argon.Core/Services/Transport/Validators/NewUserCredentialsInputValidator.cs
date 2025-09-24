namespace Argon.Services.Validators;

using FluentValidation;

public class NewUserCredentialsInputValidator : AbstractValidator<NewUserCredentialsInput>
{
    // Minimum registration age per country.
    // Source: COPPA (US), GDPR (EU) national implementations, LGPD (Brazil), and local privacy laws.
    // If country not found, default = 13 (COPPA baseline).
    private static readonly Dictionary<string, int> CountryAgeRestrictions = new(StringComparer.OrdinalIgnoreCase)
    {
        // --- North America ---
        { "US", 13 }, // United States – COPPA requires minimum 13
        { "CA", 13 }, // Canada – aligned with COPPA
        { "MX", 13 }, // Mexico – no strict law, COPPA baseline

        // --- Europe (GDPR countries) ---
        { "UK", 13 }, // United Kingdom – post-Brexit, kept 13
        { "IE", 16 }, // Ireland – GDPR allows 13–16, Ireland set 16
        { "DE", 16 }, // Germany – set maximum (16)
        { "FR", 15 }, // France – set 15
        { "ES", 14 }, // Spain – set 14
        { "IT", 14 }, // Italy – set 14
        { "NL", 16 }, // Netherlands – set 16
        { "SE", 13 }, // Sweden – 13
        { "NO", 13 }, // Norway (not EU, harmonized) – 13
        { "DK", 13 }, // Denmark – 13
        { "FI", 13 }, // Finland – 13
        { "PL", 16 }, // Poland – 16
        { "CZ", 15 }, // Czech Republic – 15
        { "SK", 16 }, // Slovakia – 16
        { "HU", 16 }, // Hungary – 16
        { "RO", 16 }, // Romania – 16
        { "BG", 14 }, // Bulgaria – 14
        { "GR", 15 }, // Greece – 15
        { "PT", 13 }, // Portugal – 13

        // --- Eastern Europe / CIS ---
        { "RU", 14 }, // Russia – Data protection law, minimum consent age 14
        { "UA", 14 }, // Ukraine – aligned with RU/EU practice
        { "BY", 14 }, // Belarus – aligned with RU
        { "KZ", 14 }, // Kazakhstan – aligned with RU
        { "AM", 14 }, // Armenia – aligned with RU
        { "GE", 16 }, // Georgia – aligned with GDPR max
        { "TR", 13 }, // Turkey – COPPA baseline

        // --- Asia ---
        { "JP", 13 }, // Japan – no strict law, de facto 13
        { "KR", 14 }, // South Korea – Youth Protection Law, 14
        { "CN", 18 }, // China – strict regulations, often 18
        { "IN", 18 }, // India – Data Protection Bill, 18
        { "SG", 13 }, // Singapore – COPPA baseline
        { "HK", 13 }, // Hong Kong – COPPA baseline
        { "TW", 13 }, // Taiwan – COPPA baseline

        // --- Latin America ---
        { "BR", 12 }, // Brazil – LGPD, minimum 12
        { "AR", 13 }, // Argentina – COPPA baseline
        { "CL", 14 }, // Chile – local laws, 14
        { "CO", 14 }, // Colombia – 14

        // --- Africa ---
        { "ZA", 13 }, // South Africa – POPIA, COPPA baseline
        { "NG", 13 }, // Nigeria – COPPA baseline

        // --- Oceania ---
        { "AU", 13 }, // Australia – no strict law, COPPA baseline
        { "NZ", 13 }, // New Zealand – COPPA baseline
    };


    public NewUserCredentialsInputValidator(string countryCode)
    {
        RuleFor(x => x.email)
           .NotEmpty().WithMessage("Email is required.")
           .EmailAddress().WithMessage("Invalid email address.");

        RuleFor(x => x.username)
           .NotEmpty().WithMessage("Username is required.")
           .MinimumLength(4).WithMessage("Username must be at least 4 characters.")
           .MaximumLength(32).WithMessage("Username must be no more than 32 characters.")
           .Matches("^[a-zA-Z0-9_]*$").WithMessage("Username contains invalid characters.");

        RuleFor(x => x.password)
           .NotEmpty().WithMessage("Password is required.")
           .MinimumLength(8).WithMessage("Password must be at least 8 characters.");

        RuleFor(x => x.displayName)
           .NotEmpty().WithMessage("Display name is required.")
           .MaximumLength(64).WithMessage("Display name must be no more than 64 characters.");

        RuleFor(x => x.argreeTos)
           .Equal(true).WithMessage("You must agree to the terms of service.");

        var minAge = CountryAgeRestrictions.TryGetValue(countryCode, out var age) ? age : 13;

        RuleFor(x => x.birthDate)
           .Must(date => date <= DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-minAge))
           .WithMessage($"You must be at least {minAge} years old.");
    }
}