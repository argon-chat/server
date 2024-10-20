namespace Argon.Api.Grain.Persistence.States;

[GenerateSerializer]
[Alias(nameof(HelloArchive))]
public sealed record class HelloArchive
{
    [Id(0)] public List<string> Hellos { get; private set; } = new();
}