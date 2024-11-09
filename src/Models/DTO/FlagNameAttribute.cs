namespace Argon.Api.Features.Sfu;

[AttributeUsage(AttributeTargets.Field)]
public class FlagNameAttribute(string flagName) : Attribute
{
    public string FlagName { get; } = flagName;
}