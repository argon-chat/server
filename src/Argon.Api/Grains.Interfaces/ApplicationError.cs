namespace Argon.Api.Grains.Interfaces;

using System.Runtime.Serialization;
using MemoryPack;
using MessagePack;

public enum ErrorCode
{
    [EnumMember(Value = "Unknown")]
    Unknown,
    [EnumMember(Value = "InvalidPassword")]
    InvalidPassword,
    [EnumMember(Value = "PasswordMismatch")]
    PasswordMismatch,
    [EnumMember(Value = "InvalidEmail")]
    RecordNotFound
}

[DataContract, MemoryPackable(GenerateType.VersionTolerant), MessagePackObject, Serializable, GenerateSerializer, Alias(nameof(ApplicationError))]
public sealed partial record ApplicationError(
    [property: DataMember(Order = 0), MemoryPackOrder(0), Key(0), Id(0)]
    ErrorCode ErrorCode,
    [property: DataMember(Order = 1), MemoryPackOrder(1), Key(1), Id(1)]
    string Message,
    [property: DataMember(Order = 2), MemoryPackOrder(2), Key(2), Id(2)]
    string? Details);