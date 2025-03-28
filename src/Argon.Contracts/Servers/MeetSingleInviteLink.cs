namespace Argon.Shared.Servers;

[MessagePackObject(true)]
public record MeetSingleInviteLink : ArgonEntityWithOwnership<ulong>
{
    public Guid? AssociatedChannelId { get; set; }
    public Guid? AssociatedServerId  { get; set; }

    public Guid? NoChannelSharedKey { get; set; }

    public DateTime ExpireDate { get; set; }

    public bool IsNoAssociatedMeeting() 
        => AssociatedChannelId is null && AssociatedServerId is null && NoChannelSharedKey is not null;
    public bool IsAssociatedMeeting()
        => AssociatedChannelId is not null && AssociatedServerId is not null && NoChannelSharedKey is null;
    public string Decode()
        => InviteCodeEntity.DecodeFromUlong(Id);
}

public enum MeetJoinError
{
    OK,
    NO_LINK_EXIST
}


[MessagePackObject(true), TsInterface]
public record MeetAuthorizationData(
    string voiceToken, string accessToken);