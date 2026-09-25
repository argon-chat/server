namespace Argon.Core.Services.Validators;

using FluentValidation;

/// <summary>
/// The shape of a username: four to thirty-two ASCII letters, digits and underscores.
/// </summary>
/// <remarks>
/// People and bots share one username namespace and one member list, so they share this rule too.
/// </remarks>
public static class UsernameRules
{
    public static IRuleBuilderOptions<T, string> ValidUsername<T>(this IRuleBuilder<T, string> rule)
        => rule
           .NotEmpty().WithMessage("Username is required.")
           .MinimumLength(4).WithMessage("Username must be at least 4 characters.")
           .MaximumLength(32).WithMessage("Username must be no more than 32 characters.")
           .Matches("^[a-zA-Z0-9_]*$").WithMessage("Username contains invalid characters.");

    private static readonly InlineValidator<string> Shape = new() { v => v.RuleFor(x => x).ValidUsername() };

    /// <summary>Whether <paramref name="username"/> has the shape above.</summary>
    public static bool IsWellFormed(string? username)
        => username is not null && Shape.Validate(username).IsValid;
}
