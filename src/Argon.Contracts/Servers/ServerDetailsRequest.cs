namespace Argon;

[TsInterface, MessagePackObject(true)]
public sealed record ServerDetailsRequest(Guid ServerId);