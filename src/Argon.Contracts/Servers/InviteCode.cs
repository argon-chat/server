namespace Argon.Shared.Servers;

using Orleans;

public enum AcceptInviteError
{
    NONE,
    NOT_FOUND,
    EXPIRED,
    YOU_ARE_BANNED
}


[MessagePackObject(true)]
public readonly record struct InviteCode(string inviteCode);


[MessagePackObject(true), Alias("Argon.Shared.Servers.InviteCodeEntity")]
public record struct InviteCodeEntity(InviteCode code, Guid serverId, Guid issuerId, DateTime expireTime, long used)
{
    public bool HasExpired() => DateTime.UtcNow > expireTime;
}