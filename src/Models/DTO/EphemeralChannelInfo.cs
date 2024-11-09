namespace Models.DTO;

using LiveKit.Proto;

public record struct EphemeralChannelInfo(ArgonChannelId channelId, string sid, Room room);