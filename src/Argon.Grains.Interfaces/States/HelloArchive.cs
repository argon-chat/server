namespace Argon.Grains.Interfaces.States;

[GenerateSerializer]
[Alias(nameof(HelloArchive))]
public sealed record class HelloArchive
{
    [Id(0)] public List<string> Hellos { get; private set; } = new();
}