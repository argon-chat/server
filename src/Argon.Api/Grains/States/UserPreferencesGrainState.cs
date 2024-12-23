namespace Argon.Grains.Persistence.States;

[DataContract, MemoryPackable(GenerateType.VersionTolerant), MessagePackObject(true), Serializable, GenerateSerializer]
public sealed partial record UserPreferencesGrainState
{
    [DataMember(Order = 0), MemoryPackOrder(0), Id(0)]
    public Dictionary<Symbol, string> UserPreferences { get; set; } = new();
}