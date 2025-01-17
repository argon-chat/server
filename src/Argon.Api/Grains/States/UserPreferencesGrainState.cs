namespace Argon.Grains.Persistence.States;

[DataContract, MessagePackObject(true), Serializable, GenerateSerializer]
public sealed partial record UserPreferencesGrainState
{
    [DataMember(Order = 0), Id(0)]
    public Dictionary<string, string> UserPreferences { get; set; } = new();
}