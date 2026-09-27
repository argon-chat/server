namespace Argon.Grains;

using Argon.Entities;
using Argon.Features.Expressions;
using Argon.Grains.Interfaces;
using Argon.Services.L1L2;
using Microsoft.Extensions.Caching.Hybrid;
using Orleans.Concurrency;

/// <inheritdoc cref="IExpressionItemDirectoryGrain"/>
[StatelessWorker]
public sealed class ExpressionItemDirectoryGrain(
    IDbContextFactory<ApplicationDbContext> context,
    HybridCache cache,
    ILogger<ExpressionItemDirectoryGrain> logger) : Grain, IExpressionItemDirectoryGrain
{
    public async Task<IReadOnlyDictionary<Guid, StatusEmoji>> ResolveEmojiAsync(IReadOnlyCollection<Guid> itemIds)
        => itemIds.Count == 0 ? new Dictionary<Guid, StatusEmoji>() : await ExpressionItemCache.ResolveAsync(cache, itemIds, LoadAsync);

    public async Task ForgetStatusIconsAsync(Dictionary<Guid, string> staleIconByUser)
    {
        var userIds = staleIconByUser.Keys.ToList();
        var icons   = staleIconByUser.Values.Distinct().ToList();

        try
        {
            await using var ctx = await context.CreateDbContextAsync();

            // Every icon here names a gone item, so matching them across the users is harmless.
            await ctx.UserProfiles
               .Where(p => userIds.Contains(p.UserId) && p.CustomStatusIconId != null && icons.Contains(p.CustomStatusIconId))
               .ExecuteUpdateAsync(s => s.SetProperty(p => p.CustomStatusIconId, (string?)null));
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "could not forget {Count} stale status icons", userIds.Count);
        }
    }

    private async Task<Dictionary<Guid, StatusEmoji>> LoadAsync(List<Guid> itemIds)
    {
        await using var ctx = await context.CreateDbContextAsync();

        return await ctx.ExpressionItems
           .AsNoTracking()
           .Where(i => itemIds.Contains(i.Id) && !i.IsDeleted && i.Kind == ExpressionKind.Emoji)
           .Select(i => new StatusEmoji(i.Id, i.SpaceId, i.FileId, i.Format, i.Name))
           .ToDictionaryAsync(e => e.itemId);
    }
}

public static class StatusEmojiProfiles
{
    public static async Task<ArgonUserProfile> WithStatusEmojiAsync(this IGrainFactory grains, ArgonUserProfile profile)
    {
        if (StatusIcon.Parse(profile.customStatusIconId) is not (StatusIconKind.CustomEmoji, _))
            return profile;

        var one = new List<ArgonUserProfile>(1) { profile };
        await grains.WithStatusEmojiAsync(one);
        return one[0];
    }

    /// <summary>
    /// Fills <c>customStatusEmoji</c> in place, in one lookup. A reference to a gone item reads as no icon and is
    /// nulled in the background.
    /// </summary>
    public static async Task WithStatusEmojiAsync(this IGrainFactory grains, List<ArgonUserProfile> profiles)
    {
        List<Guid>? itemIds = null;

        foreach (var profile in profiles)
            if (StatusIcon.Parse(profile.customStatusIconId) is (StatusIconKind.CustomEmoji, var itemId))
                (itemIds ??= []).Add(itemId);

        if (itemIds is null)
            return;

        var directory = grains.GetGrain<IExpressionItemDirectoryGrain>(Guid.Empty);
        var found     = await directory.ResolveEmojiAsync(itemIds);

        Dictionary<Guid, string>? stale = null;

        for (var i = 0; i < profiles.Count; i++)
        {
            var profile = profiles[i];
            if (StatusIcon.Parse(profile.customStatusIconId) is not (StatusIconKind.CustomEmoji, var itemId))
                continue;

            if (found.TryGetValue(itemId, out var emoji))
            {
                profiles[i] = profile with { customStatusEmoji = emoji };
                continue;
            }

            (stale ??= [])[profile.userId] = profile.customStatusIconId!;
            profiles[i] = profile with { customStatusIconId = null, customStatusEmoji = null };
        }

        if (stale is not null)
            await directory.ForgetStatusIconsAsync(stale);
    }
}
