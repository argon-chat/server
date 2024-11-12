namespace Argon.Api.Grains.Interfaces;

#if DEBUG
[DataContract, MemoryPackable(GenerateType.VersionTolerant), MessagePackObject, Serializable, GenerateSerializer, Alias(nameof(SomeInput))]
public sealed partial record SomeInput(
    [property: DataMember(Order = 0), MemoryPackOrder(0), Key(0), Id(0)]
    int a,
    [property: DataMember(Order = 1), MemoryPackOrder(1), Key(1), Id(1)]
    string b);

[Alias("Argon.Api.Grains.Interfaces.ITestGrain")]
public interface ITestGrain : IGrainWithGuidKey
{
    [Alias("CreateSomeInput")]
    Task<SomeInput> CreateSomeInput(SomeInput input);

    [Alias("UpdateSomeInput")]
    Task<SomeInput> UpdateSomeInput(SomeInput input);

    [Alias("DeleteSomeInput")]
    Task<SomeInput> DeleteSomeInput();

    [Alias("GetSomeInput")]
    Task<SomeInput> GetSomeInput();
}
#endif