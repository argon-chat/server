namespace Argon.Streaming.Events;

[TsInterface, MessagePackObject(true)]
public record ArgonEvent<T> : IArgonEvent where T : ArgonEvent<T>, IArgonEvent
{
    [JsonIgnore]
    public string EventKey { get; set; } = typeof(T).Name;
}