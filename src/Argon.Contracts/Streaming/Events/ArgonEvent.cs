namespace Argon.Streaming.Events;

[TsInterface, MessagePackObject(true)]
public record ArgonEvent<T> : IArgonEvent where T : ArgonEvent<T>, IArgonEvent
{
    public string EventKey { get; init; } = typeof(T).Name;
    public long?  Sequence { get; set; }
    public int?   EventId  { get; set; }
}