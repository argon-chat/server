namespace Argon.Api.BotApi.Interfaces;

using Argon.Features.BotApi;
using ion.runtime;

[BotInterface("IExpressions", 1)]
[BotDescription("Read a space's sticker and custom emoji packs and add to them. Bots change only the packs and items they made, and never delete or reorder. Writing takes CreateExpressions among the bot's required entitlements.")]
public sealed class ExpressionsV1(IGrainFactory grains, BotUserCache users) : IBotInterface
{
    public sealed record ListQuery(
        Guid    SpaceId,
        string? Known = null);

    public sealed record ExpressionsListResponse(
        string                     Version,
        List<BotExpressionPackV1>? Packs);

    public sealed record SpaceQuery(
        Guid SpaceId);

    public sealed record GetPackQuery(
        Guid    SpaceId,
        Guid?   PackId = null,
        string? Slug   = null);

    public sealed record CreatePackRequest(
        Guid              SpaceId,
        BotExpressionKind Kind,
        string            Title,
        string            Slug);

    public sealed record UpdatePackRequest(
        Guid    SpaceId,
        Guid    PackId,
        string? Title       = null,
        Guid?   CoverItemId = null);

    public sealed record AddItemRequest(
        Guid          SpaceId,
        Guid          PackId,
        string        Name,
        BotInputFile  File,
        List<string>? Emoji     = null,
        List<string>? Keywords  = null,
        bool?         TextColor = null);

    public sealed record UpdateItemRequest(
        Guid          SpaceId,
        Guid          ItemId,
        string?       Name      = null,
        List<string>? Emoji     = null,
        List<string>? Keywords  = null,
        bool?         TextColor = null);

    private const string NeedsEntitlement = " Needs CreateExpressions.";

    public void MapRoutes(RouteGroupBuilder group)
    {
        group.AddEndpointFilter<BotOrleansPropagationFilter>();
        group.RequireRateLimiting("Bot_IExpressions");

        group.Get<ListQuery, ExpressionsListResponse>("/List")
           .Summary("Lists the space's packs with their items. Pass the version you hold as known: packs is null while it is still current.")
           .RequiresSpaceMembership()
           .Handle(async (_, query) =>
            {
                var snapshot = await Grain(query.SpaceId).GetExpressions(query.Known);

                return new ExpressionsListResponse(snapshot.version,
                    snapshot.packs is { } packs ? await BotEventMapper.FromExpressionPacksAsync(packs, users) : null);
            });

        group.Get<SpaceQuery, BotQuotaV1>("/GetQuota")
           .Summary("How many packs, stickers and emoji the space holds against its limits, which grow with its boost level.")
           .RequiresSpaceMembership()
           .Handle(async (_, query) =>
            {
                var quota = await Grain(query.SpaceId).GetQuota();

                return new BotQuotaV1(
                    new BotQuotaUsageV1(quota.Packs, quota.MaxPacks),
                    new BotQuotaUsageV1(quota.Stickers, quota.MaxStickers),
                    new BotQuotaUsageV1(quota.Emoji, quota.MaxEmoji),
                    new BotItemsPerPackV1(quota.StickersPerPack, quota.EmojiPerPack),
                    quota.BoostLevel);
            });

        group.Get<GetPackQuery, BotExpressionPackV1>("/GetPack")
           .Summary("Gets one pack with its items, by packId or by slug.")
           .RequiresSpaceMembership()
           .Throws(BotErrors.InvalidRequest)
           .Throws(ExpressionBotErrors.NotFound)
           .Handle(async (_, query) =>
            {
                if (query.PackId is null && string.IsNullOrEmpty(query.Slug))
                    throw BotErrors.InvalidRequest.Raise("Pass packId or slug.");

                var snapshot = await Grain(query.SpaceId).GetExpressions(null);
                var pack     = snapshot.packs?.FirstOrDefault(p => query.PackId is { } id ? p.packId == id : p.slug == query.Slug)
                            ?? throw ExpressionBotErrors.NotFound.Raise();

                return (await BotEventMapper.FromExpressionPacksAsync([pack], users))[0];
            });

        group.Post<CreatePackRequest, BotExpressionPackV1>("/CreatePack")
           .Summary("Creates a sticker or emoji pack. Repeating it with the slug of a pack this bot made returns that pack." + NeedsEntitlement)
           .Permission(ArgonEntitlement.CreateExpressions)
           .RequiresSpaceMembership()
           .Throws(ExpressionBotErrors.InsufficientRights)
           .Throws(ExpressionBotErrors.InvalidFormat)
           .Throws(ExpressionBotErrors.QuotaExceeded)
           .Throws(ExpressionBotErrors.NameTaken)
           .Throws(ExpressionBotErrors.SpaceRateLimited)
           .Handle(async (ctx, request) =>
                await PackOf(ctx, await Grain(request.SpaceId).CreatePack((ExpressionKind)(int)request.Kind, request.Title, request.Slug)));

        group.Patch<UpdatePackRequest, BotExpressionPackV1>("/UpdatePack")
           .Summary("Changes the title or the cover item of a pack this bot made. Fields left out are kept." + NeedsEntitlement)
           .Permission(ArgonEntitlement.CreateExpressions)
           .RequiresSpaceMembership()
           .Throws(ExpressionBotErrors.InsufficientRights)
           .Throws(ExpressionBotErrors.NotFound)
           .Throws(ExpressionBotErrors.InvalidFormat)
           .Throws(ExpressionBotErrors.SpaceRateLimited)
           .Handle(async (ctx, request) =>
            {
                var patch = new IonPartial<ExpressionPack>();
                if (request.Title is { } title)
                    patch = patch.Modify(x => x.title, title);
                if (request.CoverItemId is { } cover)
                    patch = patch.Modify(x => x.coverItemId, cover);

                return await PackOf(ctx, await Grain(request.SpaceId).UpdatePack(request.PackId, patch));
            });

        group.Post<AddItemRequest, BotExpressionItemV1>("/AddItem")
           .Summary("Adds a sticker or emoji to a pack (multipart). file is a part, an attach://<part> reference or a fileId from IFiles/Upload. PNG is stored as WEBP. Each call adds a new item; deduplicating repeated uploads is the bot's job." + NeedsEntitlement)
           .Permission(ArgonEntitlement.CreateExpressions)
           .FromForm(BotFileLimits.MaxRequestBytes)
           .RequiresSpaceMembership()
           .Throws(ExpressionBotErrors.InsufficientRights)
           .Throws(ExpressionBotErrors.NotFound)
           .Throws(ExpressionBotErrors.InvalidFormat)
           .Throws(ExpressionBotErrors.TooLarge)
           .Throws(ExpressionBotErrors.QuotaExceeded)
           .Throws(ExpressionBotErrors.ContentRejected)
           .Throws(ExpressionBotErrors.NameTaken)
           .Throws(ExpressionBotErrors.SpaceRateLimited)
           .Throws(ExpressionBotErrors.RenderUnavailable)
           .Handle(async (ctx, request) =>
            {
                var imported = await Grain(request.SpaceId).ImportItem(new ExpressionImport(
                    request.PackId,
                    request.Name,
                    request.Emoji ?? [],
                    request.Keywords ?? [],
                    request.TextColor ?? false,
                    Input(request.File)));

                if (imported.RenderUnavailable)
                    throw ExpressionBotErrors.RenderUnavailable.Raise();

                if (imported is not { Error: ExpressionError.NONE, Item: { } item })
                    throw ExpressionBotErrors.Raise(ctx, imported.Error);

                return await ItemOf(item);
            });

        group.Patch<UpdateItemRequest, BotExpressionItemV1>("/UpdateItem")
           .Summary("Changes the name, associated emoji, keywords or text colouring of an item this bot made. Fields left out are kept." + NeedsEntitlement)
           .Permission(ArgonEntitlement.CreateExpressions)
           .RequiresSpaceMembership()
           .Throws(ExpressionBotErrors.InsufficientRights)
           .Throws(ExpressionBotErrors.NotFound)
           .Throws(ExpressionBotErrors.InvalidFormat)
           .Throws(ExpressionBotErrors.NameTaken)
           .Throws(ExpressionBotErrors.SpaceRateLimited)
           .Handle(async (ctx, request) =>
            {
                var patch = new IonPartial<ExpressionItem>();
                if (request.Name is { } name)
                    patch = patch.Modify(x => x.name, name);
                if (request.Emoji is { } emoji)
                    patch = patch.Modify(x => x.emoji, new IonArray<string>(emoji));
                if (request.Keywords is { } keywords)
                    patch = patch.Modify(x => x.keywords, new IonArray<string>(keywords));
                if (request.TextColor is { } textColor)
                    patch = patch.Modify(x => x.textColor, textColor);

                var result = await Grain(request.SpaceId).UpdateItem(request.ItemId, patch);

                return result switch
                {
                    SuccessItem success => await ItemOf(success.item),
                    FailedItem failure  => throw ExpressionBotErrors.Raise(ctx, failure.error),
                    _                   => throw new InvalidOperationException($"unexpected item result {result}")
                };
            });
    }

    private ISpaceExpressionsGrain Grain(Guid spaceId) => grains.GetGrain<ISpaceExpressionsGrain>(spaceId);

    private static ExpressionFileInput Input(BotInputFile file) => new(file.FileId, file.Data);

    private async Task<BotExpressionPackV1> PackOf(HttpContext ctx, IPackResult result) => result switch
    {
        SuccessPack success => (await BotEventMapper.FromExpressionPacksAsync([success.pack], users))[0],
        FailedPack failure  => throw ExpressionBotErrors.Raise(ctx, failure.error),
        _                   => throw new InvalidOperationException($"unexpected pack result {result}")
    };

    private async Task<BotExpressionItemV1> ItemOf(ExpressionItem item)
    {
        var byBot = await BotEventMapper.BotCreatorsAsync([item.creatorId], users);
        return BotEventMapper.FromExpressionItem(item, byBot(item.creatorId));
    }
}
