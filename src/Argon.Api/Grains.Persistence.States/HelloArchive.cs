namespace Argon.Api.Grains.Persistence.States;

[GenerateSerializer]
[Alias(nameof(HelloArchive))]
public sealed record class HelloArchive
{
    [Id(0)] public List<string> Hellos { get; private set; } = new();
    [Id(1)] public List<int> Ints { get; private set; } = new();
}