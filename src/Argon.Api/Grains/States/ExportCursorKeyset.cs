namespace Argon.Grains.Persistence.States;

public sealed partial record ExportCursor
{
    /// <summary>The id of the last message written from the current channel; the next page starts after it.</summary>
    /// <remarks>
    /// Paging by <see cref="ChannelMessageOffset"/> alone made every page re-read all the pages before it.
    /// The offset stays as the page's position, which names the page object. Null at the start of a
    /// channel, and in a cursor persisted before this field existed — that one resumes by the offset once.
    /// </remarks>
    [DataMember(Order = 20), Id(20)]
    public long? ChannelMessageAfter { get; set; }
}
