namespace Argon.Streaming;

[TsInterface]
public interface IArgonEvent
{
    public static string ProviderId => "argon.cluster.events";
    public static string Namespace  => $"@";
    public static string Broadcast  => $"argon.cluster.events.broadcast";
}