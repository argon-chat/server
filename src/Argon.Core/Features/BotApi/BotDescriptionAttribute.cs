namespace Argon.Features.BotApi;

/// <summary>
/// Provides a human-readable description for a <see cref="IBotInterface"/> implementation. It
/// becomes the description of the interface's tag in the OpenAPI document.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class BotDescriptionAttribute(string description) : Attribute
{
    public string Description { get; } = description;
}
