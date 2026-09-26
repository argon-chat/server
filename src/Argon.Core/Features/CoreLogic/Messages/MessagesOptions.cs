namespace Argon.Api.Features.CoreLogic.Messages;

using Argon.Features.Clustering;

/// <summary>
/// What the message path is allowed to do.
/// </summary>
public sealed class MessagesOptions : IValidatableFeatureOptions
{
    /// <summary>
    /// Messages one channel may accept per second, or <c>0</c> to accept whatever arrives.
    /// </summary>
    /// <remarks>
    /// A ceiling put there on purpose, well under what the channel can actually do — measured at
    /// about 340 a second with one grain ordering them. Nothing a room full of people types comes
    /// near two hundred a second; anything that does is a runaway client or someone with a script,
    /// and the useful thing to do with it is refuse rather than to find out how far the node bends.
    /// <para>
    /// Off in tests, which send as fast as they can on purpose and are not what this protects
    /// against.
    /// </para>
    /// </remarks>
    public int PerChannelPerSecond { get; set; } = 200;

    /// <summary>
    /// How many message inserts the process may have in flight at once.
    /// </summary>
    /// <remarks>
    /// This is the node's write capacity, not a channel's, and it is worth knowing what it buys: one
    /// writer measured 786 messages a second across two hundred busy channels, eight measured 1626,
    /// and twenty-four measured 3264. Past that the delivery path gives out first — every message
    /// also appends to the space's replay stream — so raising this further moves the queue rather
    /// than shortening it.
    /// </remarks>
    public int WriteConcurrency { get; set; } = 8;

    /// <summary>
    /// Longest message text, in UTF-16 code units, that a send or an edit accepts.
    /// </summary>
    public int MaxTextLength { get; set; } = 4096;

    /// <summary>
    /// Messages with @everyone, a role mention or a mass of user mentions that ping readers of one
    /// announcement channel per hour, or <c>0</c> for no limit. Past it the message is still posted,
    /// without the ping.
    /// </summary>
    public int AnnouncementMassMentionsPerHour { get; set; } = 3;

    /// <summary>
    /// In an announcement channel, a message that pings more members than this by name counts
    /// against <see cref="AnnouncementMassMentionsPerHour"/> the way @everyone does.
    /// </summary>
    public int AnnouncementMassUserMentions { get; set; } = 10;

    /// <summary>Most entities a send or an edit accepts.</summary>
    public int MaxEntities { get; set; } = 512;

    /// <summary>Most distinct users one message pings by name; the rest stay plain mentions.</summary>
    public int MaxMentionedUsers { get; set; } = 50;

    public void Validate(IFeatureConfigurationReport report)
    {
        if (PerChannelPerSecond < 0)
            report.Invalid($"{nameof(PerChannelPerSecond)} cannot be negative; use 0 to disable the cap");

        report.RequireRange(WriteConcurrency, 1, 64, nameof(WriteConcurrency));
        report.RequireRange(MaxTextLength, 1, 65536, nameof(MaxTextLength));
        report.RequireRange(MaxEntities, 1, 4096, nameof(MaxEntities));
        report.RequireRange(MaxMentionedUsers, 1, 1000, nameof(MaxMentionedUsers));
        report.RequireRange(AnnouncementMassUserMentions, 1, 1000, nameof(AnnouncementMassUserMentions));

        if (AnnouncementMassMentionsPerHour < 0)
            report.Invalid($"{nameof(AnnouncementMassMentionsPerHour)} cannot be negative; use 0 to disable the limit");
    }
}
