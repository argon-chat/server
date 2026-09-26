namespace Argon.Grains;

using Argon.Core.Services;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// A user's composer drafts, one per channel. Keyed by the user, so one activation orders that
/// user's saves and the last one wins.
/// </summary>
/// <remarks>
/// Drafts are held in memory and written behind: a save only changes memory and marks the draft
/// dirty, and a timer every <see cref="FlushPeriod"/> and the deactivation write the dirty ones. A
/// burst of keystroke saves therefore costs one row write per channel per period, and the
/// ViewChannel check is remembered per channel for <see cref="AccessCheckedFor"/>.
/// </remarks>
public class MessageDraftsGrain(
    IDbContextFactory<ApplicationDbContext> context,
    IEntitlementChecker entitlementChecker,
    IOptions<MessagesOptions> messageOptions,
    ILogger<MessageDraftsGrain> logger) : Grain, IMessageDraftsGrain
{
    public const int MaxEntities = 512;

    public static readonly TimeSpan FlushPeriod      = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan Lifetime         = TimeSpan.FromDays(30);
    public static readonly TimeSpan AccessCheckedFor = TimeSpan.FromMinutes(1);

    // A clean draft nobody asked for in this long leaves memory; the table still has it.
    private static readonly TimeSpan IdleSlot = TimeSpan.FromMinutes(10);

    private sealed class Slot
    {
        public bool                Loaded;
        public MessageDraftEntity? Draft;
        public bool                Dirty;
        public Guid                CheckedSpaceId;
        public DateTimeOffset      CheckedAt;
        public DateTimeOffset      TouchedAt;
    }

    private readonly Dictionary<Guid, Slot> slots = new();

    private IDisposable? flushTimer;

    private Guid UserId => this.GetPrimaryKey();

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        flushTimer = this.RegisterGrainTimer(FlushAsync, new GrainTimerCreationOptions(FlushPeriod, FlushPeriod));
        return Task.CompletedTask;
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        flushTimer?.Dispose();
        await FlushAsync(cancellationToken);
    }

    public async Task SaveDraftAsync(Guid spaceId, Guid channelId, string text, List<IMessageEntity> entities)
    {
        var userId = CallerIsOwner();
        text     ??= "";
        entities ??= [];

        if (text.Length > messageOptions.Value.MaxTextLength)
            throw new InvalidOperationException($"Draft text is longer than {messageOptions.Value.MaxTextLength} characters");
        if (entities.Count > MaxEntities)
            throw new InvalidOperationException($"A draft carries at most {MaxEntities} entities");

        var now  = DateTimeOffset.UtcNow;
        var slot = SlotOf(channelId, now);

        if (string.IsNullOrWhiteSpace(text))
        {
            if (slot is { Loaded: true, Draft: null })
                return;

            slot.Loaded = true;
            slot.Draft  = null;
            slot.Dirty  = true;
            return;
        }

        if (slot.CheckedSpaceId != spaceId || now - slot.CheckedAt >= AccessCheckedFor)
        {
            if (!await entitlementChecker.HasChannelAccessAsync(spaceId, channelId, userId, ArgonEntitlement.ViewChannel))
                throw new UnauthorizedAccessException("No access to this channel");

            slot.CheckedSpaceId = spaceId;
            slot.CheckedAt      = now;
        }

        slot.Loaded = true;
        slot.Draft = new MessageDraftEntity
        {
            UserId    = userId,
            ChannelId = channelId,
            SpaceId   = spaceId,
            Text      = text,
            Entities  = entities,
            UpdatedAt = now,
            ExpireAt  = now + Lifetime
        };
        slot.Dirty = true;
    }

    public async Task<MessageDraft?> GetDraftAsync(Guid spaceId, Guid channelId)
    {
        var userId = CallerIsOwner();
        var slot   = SlotOf(channelId, DateTimeOffset.UtcNow);

        if (!slot.Loaded)
        {
            await using var ctx = await context.CreateDbContextAsync();

            var now = DateTimeOffset.UtcNow;
            slot.Draft = await ctx.MessageDrafts.AsNoTracking()
               .FirstOrDefaultAsync(d => d.UserId == userId && d.ChannelId == channelId && d.ExpireAt > now);
            slot.Loaded = true;
        }

        return slot.Draft is { } draft && draft.SpaceId == spaceId && draft.ExpireAt > DateTimeOffset.UtcNow
            ? draft.ToDto()
            : null;
    }

    public async Task EraseAsync()
    {
        slots.Clear();

        await using var ctx = await context.CreateDbContextAsync();

        var userId = UserId;
        await ctx.MessageDrafts.Where(d => d.UserId == userId).ExecuteDeleteAsync();

        this.DeactivateOnIdle();
    }

    private Slot SlotOf(Guid channelId, DateTimeOffset now)
    {
        if (!slots.TryGetValue(channelId, out var slot))
            slots[channelId] = slot = new Slot();

        slot.TouchedAt = now;
        return slot;
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        var dirty = slots.Where(s => s.Value.Dirty).ToList();

        if (dirty.Count > 0)
        {
            await using var ctx = await context.CreateDbContextAsync(ct);

            foreach (var (channelId, slot) in dirty)
            {
                try
                {
                    await WriteAsync(ctx, channelId, slot.Draft, ct);
                    slot.Dirty = false;
                }
                catch (Exception e)
                {
                    // Still dirty, so the next flush tries again.
                    logger.LogWarning(e, "Draft of {UserId} in channel {ChannelId} was not written", UserId, channelId);
                }
            }
        }

        var idle = DateTimeOffset.UtcNow - IdleSlot;
        foreach (var (channelId, _) in slots.Where(s => !s.Value.Dirty && s.Value.TouchedAt < idle).ToList())
            slots.Remove(channelId);
    }

    private async Task WriteAsync(ApplicationDbContext ctx, Guid channelId, MessageDraftEntity? draft, CancellationToken ct)
    {
        var userId = UserId;

        if (draft is null)
        {
            await ctx.MessageDrafts
               .Where(d => d.UserId == userId && d.ChannelId == channelId)
               .ExecuteDeleteAsync(ct);
            return;
        }

        var updated = await ctx.MessageDrafts
           .Where(d => d.UserId == userId && d.ChannelId == channelId)
           .ExecuteUpdateAsync(s => s
               .SetProperty(d => d.SpaceId, draft.SpaceId)
               .SetProperty(d => d.Text, draft.Text)
               .SetProperty(d => d.Entities, draft.Entities)
               .SetProperty(d => d.UpdatedAt, draft.UpdatedAt)
               .SetProperty(d => d.ExpireAt, draft.ExpireAt), ct);

        if (updated > 0)
            return;

        ctx.MessageDrafts.Add(draft with { });
        try
        {
            await ctx.SaveChangesAsync(ct);
        }
        finally
        {
            ctx.ChangeTracker.Clear();
        }
    }

    private Guid CallerIsOwner()
    {
        var userId = this.GetUserId();
        if (userId != this.GetPrimaryKey())
            throw new UnauthorizedAccessException("Drafts belong to their author");
        return userId;
    }
}
