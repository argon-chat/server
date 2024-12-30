namespace Argon.Streaming;

[TsInterface]
public interface IArgonEvent
{
    [TsIgnore]
    public static string ProviderId => "argon.cluster.events";
    [TsIgnore]
    public static string Namespace  => $"@";
    [TsIgnore]
    public static string Broadcast  => $"argon.cluster.events.broadcast";

    public string EventKey { get; init; }
}