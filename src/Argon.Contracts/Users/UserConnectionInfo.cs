namespace Argon.Users;

[MessagePackObject(true), TsInterface]
public sealed record UserConnectionInfo(
    string Region,
    string IpAddress,
    string ClientName,
    string HostName,
    Guid machineId);