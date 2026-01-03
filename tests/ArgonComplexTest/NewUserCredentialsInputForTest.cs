namespace ArgonComplexTest;

public sealed record NewUserCredentialsInputForTest
{
    public string   email                { get; init; }
    public string   username             { get; init; }
    public string   password             { get; init; }
    public string   displayName          { get; init; }
    public bool     argreeTos            { get; init; }
    public DateOnly birthDate            { get; init; }
    public bool     argreeOptionalEmails { get; init; }
    public string?  captchaToken         { get; init; }
}