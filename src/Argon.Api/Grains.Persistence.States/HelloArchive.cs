namespace Argon.Api.Grains.Persistence.States;

using MemoryPack;

[GenerateSerializer]
[MemoryPackable]
[Alias(nameof(HelloArchive))]
public sealed partial record HelloArchive
{
    [Id(0)] public List<string> Hellos { get; private set; } = new();
    [Id(1)] public List<int> Ints { get; private set; } = new();
}