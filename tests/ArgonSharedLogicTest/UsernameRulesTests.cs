namespace ArgonSharedLogicTest;

using Argon.Core.Services.Validators;
using ArgonContracts;

/// <summary>
/// The one username shape people and bots share.
/// </summary>
/// <remarks>
/// Registration and the bot console both read <see cref="UsernameRules"/>, so a rule changed here
/// changes both; these pin the boundaries and that registration still says what it said before the
/// rule moved out of <see cref="NewUserCredentialsInputValidator"/>.
/// </remarks>
[TestFixture]
public class UsernameRulesTests
{
    [TestCase("abcd")]
    [TestCase("Helper_Bot_2")]
    [TestCase("0123456789_abcdefghijklmnopqrstu")]
    public void A_well_formed_username_is_accepted(string username)
        => Assert.That(UsernameRules.IsWellFormed(username), Is.True);

    [TestCase(null)]
    [TestCase("")]
    [TestCase("abc")]
    [TestCase("0123456789_abcdefghijklmnopqrstuv")]
    [TestCase("a b bot")]
    [TestCase("dot.bot")]
    [TestCase("../../bot")]
    [TestCase("аdmin_bot")]
    public void A_malformed_username_is_refused(string? username)
        => Assert.That(UsernameRules.IsWellFormed(username), Is.False);

    [Test]
    public void Registration_still_names_the_username_rule_it_broke()
    {
        var input = new NewUserCredentialsInput("someone@test.local", "no spaces allowed", "Password1!", "Someone",
            true, new DateOnly(1990, 1, 1), false, null, "1.0", "1.0");

        var result = new NewUserCredentialsInputValidator("US").Validate(input);

        Assert.That(result.Errors.Select(e => (e.PropertyName, e.ErrorMessage)),
            Is.EqualTo(new[] { ("username", "Username contains invalid characters.") }));
    }
}
