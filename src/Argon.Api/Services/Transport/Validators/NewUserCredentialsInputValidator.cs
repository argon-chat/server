namespace Argon.Services.Validators;

using FluentValidation;

public class NewUserCredentialsInputValidator : AbstractValidator<NewUserCredentialsInput>
{
    public NewUserCredentialsInputValidator()
    {
        RuleFor(x => x.Email)
           .NotEmpty().WithMessage("Email is required.")
           .EmailAddress().WithMessage("Invalid email address.");

        RuleFor(x => x.Username)
           .NotEmpty().WithMessage("Username is required.")
           .MinimumLength(4).WithMessage("Username must be at least 4 characters.")
           .MaximumLength(32).WithMessage("Username must be no more than 32 characters.")
           .Matches("^[a-zA-Z0-9_]*$").WithMessage("Username contains invalid characters.");

        RuleFor(x => x.Password)
           .NotEmpty().WithMessage("Password is required.")
           .MinimumLength(8).WithMessage("Password must be at least 8 characters.");

        RuleFor(x => x.DisplayName)
           .NotEmpty().WithMessage("Display name is required.")
           .MaximumLength(64).WithMessage("Display name must be no more than 64 characters.");

        //RuleFor(x => x.BirthDate)
        //   .LessThan(DateTime.UtcNow.AddYears(-18))
        //   .WithMessage("You must be at least 18 years old.");

        RuleFor(x => x.AgreeTos)
           .Equal(true).WithMessage("You must agree to the terms of service.");
    }
}