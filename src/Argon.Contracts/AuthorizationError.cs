namespace Argon;

using MemoryPack;
using MessagePack;
using Orleans;
using System.Runtime.Serialization;

public enum AuthorizationError
{
    BAD_CREDENTIALS,
    REQUIRED_OTP,
    BAD_OTP
}