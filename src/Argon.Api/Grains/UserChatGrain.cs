namespace Argon.Grains;

using Argon.Api.Grains.Interfaces;
using Argon.Core.Features.Logic;
using Argon.Core.Grains.Interfaces;
using Argon.Core.Services;
using Argon.Features.Expressions;
using Argon.Features.Integrations.Crawler;
using Argon.Features.Storage;
using Argon.Services.L1L2;
using Argon.Grains.Interfaces;
using Orleans.Concurrency;
using Core.Entities.Data;
using Argon.Features.EF;

[StatelessWorker]
public class UserChatGrain(
    IDbContextFactory<ApplicationDbContext> context,
    ILogger<IUserChatGrain> logger,
    IUserSessionDiscoveryService sessionDiscovery,
    IUserSessionNotifier notifier,
    IConversationService conversationService,
    ILinkPreviewService linkPreviews,
    IOptions<CrawlerOptions> crawlerOptions,
    IPermissionCache permissionCache,
    IEntitlementChecker entitlementChecker,
    IOptions<ExpressionsOptions> expressionsOptions) : Grain, IUserChatGrain
{
    private Guid Me => this.GetUserId();

    public async Task<List<UserChat>> GetRecentChatsAsync(int limit, int offset, CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        var result = await ctx.UserConversations
            .AsNoTracking()
            .Where(x => x.UserId == Me && !x.IsArchived)
            .OrderByDescending(x => x.IsPinned)
            .ThenByDescending(x => x.PinnedAt)
            .ThenByDescending(x => x.LastMessageAt)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

        // Fixation: echo chat at all time top pinned
        var echoChat = result.FirstOrDefault(x => x.PeerId == UserEntity.EchoUser);

        if (echoChat is null)
        {
            var echoConversationId = ConversationEntity.GenerateConversationId(Me, UserEntity.EchoUser);
            result.Add(new UserConversationEntity
            {
                PeerId = UserEntity.EchoUser,
                IsPinned = true,
                PinnedAt = DateTimeOffset.UtcNow.AddDays(900),
                UserId = Me,
                ConversationId = echoConversationId,
                LastMessageAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            echoChat.IsPinned = true;
            echoChat.PinnedAt = DateTimeOffset.UtcNow.AddDays(900);
        }

        return result.Select(x => x.ToDto()).ToList();
    }

    public async Task PinChatAsync(Guid peerId, CancellationToken ct = default)
    {
        // Not allowed to pin echo
        if (peerId == UserEntity.EchoUser)
            return;

        logger.LogInformation("PinChat: {Me} -> {Peer}", Me, peerId);

        await using var ctx = await context.CreateDbContextAsync(ct);

        var now = DateTimeOffset.UtcNow;

        await ExecuteInTransactionAsync(ctx, async () =>
        {
            var conversationId = ConversationEntity.GenerateConversationId(Me, peerId);

            var record = await ctx.UserConversations
                .FirstOrDefaultAsync(x => x.UserId == Me && x.ConversationId == conversationId, ct);

            if (record is null)
            {
                record = new UserConversationEntity
                {
                    UserId = Me,
                    ConversationId = conversationId,
                    PeerId = peerId,
                    LastMessageAt = now,
                    IsPinned = true,
                    PinnedAt = now,
                    LastMessageText = null
                };
                ctx.UserConversations.Add(record);
            }
            else
            {
                record.IsPinned = true;
                record.PinnedAt = now;
                ctx.UserConversations.Update(record);
            }

            await ctx.SaveChangesAsync(ct);
        }, ct);

        await NotifyAsync(Me, new ChatPinnedEvent(peerId, now.UtcDateTime));
    }

    public async Task UnpinChatAsync(Guid peerId, CancellationToken ct = default)
    {
        // Not allowed to unpin echo
        if (peerId == UserEntity.EchoUser)
            return;

        logger.LogInformation("UnpinChat: {Me} -> {Peer}", Me, peerId);

        await using var ctx = await context.CreateDbContextAsync(ct);

        await ExecuteInTransactionAsync(ctx, async () =>
        {
            var conversationId = ConversationEntity.GenerateConversationId(Me, peerId);

            var record = await ctx.UserConversations
                .FirstOrDefaultAsync(x => x.UserId == Me && x.ConversationId == conversationId, ct);

            if (record is null)
                return;

            record.IsPinned = false;
            record.PinnedAt = null;

            ctx.UserConversations.Update(record);

            await ctx.SaveChangesAsync(ct);
        }, ct);

        await NotifyAsync(Me, new ChatUnpinnedEvent(peerId));
    }

    public async Task MarkChatReadAsync(Guid peerId, CancellationToken ct = default)
    {
        logger.LogInformation("MarkChatRead: {Me} -> {Peer}", Me, peerId);

        await using var ctx = await context.CreateDbContextAsync(ct);

        var hadUnread = false;

        await ExecuteInTransactionAsync(ctx, async () =>
        {
            var conversationId = ConversationEntity.GenerateConversationId(Me, peerId);

            var record = await ctx.UserConversations
                .FirstOrDefaultAsync(x => x.UserId == Me && x.ConversationId == conversationId, ct);

            if (record is null)
                return;

            hadUnread = record.UnreadCount > 0;
            record.UnreadCount = 0;
            ctx.UserConversations.Update(record);

            await ctx.SaveChangesAsync(ct);
        }, ct);

        // The caller's other windows still show the badge.
        if (hadUnread)
            await NotifyAsync(Me, new ChatReadEvent(peerId));
    }

    /// <summary>
    /// Hides the conversation on this side only. The messages are the peer's as much as mine, so
    /// nothing is destroyed: the row is archived, and the next message either side sends brings it
    /// back (<see cref="UpdateUserConversationAsync"/> clears the flag).
    /// </summary>
    public async Task DeleteChatAsync(Guid peerId, CancellationToken ct = default)
    {
        // The echo chat is the fixture every list has; it cannot be removed.
        if (peerId == UserEntity.EchoUser)
            return;

        logger.LogInformation("DeleteChat: {Me} -> {Peer}", Me, peerId);

        await using var ctx = await context.CreateDbContextAsync(ct);

        await ExecuteInTransactionAsync(ctx, async () =>
        {
            var conversationId = ConversationEntity.GenerateConversationId(Me, peerId);

            var record = await ctx.UserConversations
                .FirstOrDefaultAsync(x => x.UserId == Me && x.ConversationId == conversationId, ct);

            if (record is null)
                return;

            record.IsArchived  = true;
            record.IsPinned    = false;
            record.PinnedAt    = null;
            record.UnreadCount = 0;
            ctx.UserConversations.Update(record);

            await ctx.SaveChangesAsync(ct);
        }, ct);

        await NotifyAsync(Me, new ChatDeletedEvent(peerId));
    }

    public async ValueTask<Either<UploadTicket, UploadFileError>> BeginUploadAttachmentAsync(Guid peerId, CancellationToken ct = default)
    {
        try
        {
            var userId = Me;
            await using var ctx = await context.CreateDbContextAsync(ct);

            // The same wall that stops the message the file is for.
            var blocked = await ctx.UserBlocklist.AnyAsync(x => x.UserId == peerId && x.BlockedId == userId, ct);
            if (blocked)
                return UploadFileError.NOT_AUTHORIZED;

            var fileGrain = GrainFactory.GetGrain<IFileStorageGrain>(userId);
            var response = await fileGrain.RequestUploadAsync(await DirectTargetAsync(userId, peerId, ct), ct);
            return new UploadTicket(response.BlobId, response.Url, response.Fields, response.TtlSeconds);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to begin upload attachment for direct chat {Me} -> {Peer}", Me, peerId);
            return UploadFileError.INTERNAL_ERROR;
        }
    }

    public async ValueTask<AttachmentInfo> CompleteUploadAttachmentAsync(Guid blobId, CancellationToken ct = default)
    {
        var fileGrain = GrainFactory.GetGrain<IFileStorageGrain>(Me);
        var fileInfo  = await fileGrain.FinalizeUploadAsync(blobId, ct);

        return new AttachmentInfo(fileInfo.FileId, fileInfo.FileName ?? "", fileInfo.FileSize, fileInfo.ContentType ?? "",
            fileInfo.DownloadUrl);
    }

    public async ValueTask<Either<AttachmentInfo, AttachExistingFileError>> AttachExistingFileAsync(Guid peerId, Guid sourceFileId, string? fileName,
        CancellationToken ct = default)
    {
        var userId = Me;

        try
        {
            await using var ctx = await context.CreateDbContextAsync(ct);

            // The same wall that stops the message the file is for.
            var blocked = await ctx.UserBlocklist.AnyAsync(x => x.UserId == peerId && x.BlockedId == userId, ct);
            if (blocked)
                return AttachExistingFileError.NOT_AUTHORIZED;

            switch (await AttachmentSources.CheckAsync(ctx, entitlementChecker, userId, sourceFileId, ct))
            {
                case SourceAccess.NotFound: return AttachExistingFileError.SOURCE_NOT_FOUND;
                case SourceAccess.Denied:   return AttachExistingFileError.NOT_AUTHORIZED;
            }

            var linked = await GrainFactory.GetGrain<IFileStorageGrain>(userId).LinkAsync(sourceFileId,
                await DirectTargetAsync(userId, peerId, ct), fileName, ct);

            if (!linked.IsSuccess)
                return linked.Error;

            var file = linked.Value;
            return new AttachmentInfo(file.FileId, file.FileName ?? "", file.FileSize, file.ContentType ?? "", file.DownloadUrl);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to attach existing file {SourceId} into direct chat {Me} -> {Peer}", sourceFileId, userId, peerId);
            return AttachExistingFileError.INTERNAL_ERROR;
        }
    }

    public async ValueTask<Either<PreparedUpload, PrepareUploadError>> PrepareUploadAttachmentAsync(Guid peerId, byte[] sha256, long size,
        string contentType, string fileName, CancellationToken ct = default)
    {
        var userId = Me;

        try
        {
            await using var ctx = await context.CreateDbContextAsync(ct);

            var blocked = await ctx.UserBlocklist.AnyAsync(x => x.UserId == peerId && x.BlockedId == userId, ct);
            if (blocked)
                return PrepareUploadError.NOT_AUTHORIZED;

            var target = await DirectTargetAsync(userId, peerId, ct);

            return await GrainFactory.GetGrain<IFileStorageGrain>(userId).PrepareUploadAsync(
                target with { ContentType = contentType, FileSize = size, FileName = fileName, ClaimedSha256 = sha256 }, ct);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to prepare an upload for direct chat {Me} -> {Peer}", userId, peerId);
            return PrepareUploadError.INTERNAL_ERROR;
        }
    }

    public async Task<IVideoUploadResult> PrepareVideoUploadAsync(Guid callerId, Guid peerId, VideoUploadDeclaration declaration,
        CancellationToken ct = default)
    {
        try
        {
            if (await IsBlockedByAsync(peerId, callerId, ct))
                return new FailedVideoUpload(VideoUploadError.NOT_AUTHORIZED);

            var target = await DirectTargetAsync(callerId, peerId, ct);

            return await GrainFactory.GetGrain<IFileStorageGrain>(callerId).PrepareVideoUploadAsync(
                new VideoUploadRequest(null, target.ChannelId, FilePurpose.Video, declaration), ct);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to prepare a video upload for direct chat {Me} -> {Peer}", callerId, peerId);
            return new FailedVideoUpload(VideoUploadError.INTERNAL_ERROR);
        }
    }

    public async Task<IVideoUploadResult> CompleteVideoUploadAsync(Guid callerId, Guid peerId, Guid ticketId, UploadedPart[] parts,
        CancellationToken ct = default)
    {
        try
        {
            if (await IsBlockedByAsync(peerId, callerId, ct))
                return new FailedVideoUpload(VideoUploadError.NOT_AUTHORIZED);

            return await GrainFactory.GetGrain<IFileStorageGrain>(callerId).CompleteVideoUploadAsync(ticketId, parts, ct);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to complete video upload {TicketId} for direct chat {Me} -> {Peer}", ticketId, callerId, peerId);
            return new FailedVideoUpload(VideoUploadError.INTERNAL_ERROR);
        }
    }

    public Task AbortVideoUploadAsync(Guid callerId, Guid peerId, Guid ticketId, CancellationToken ct = default)
        => GrainFactory.GetGrain<IFileStorageGrain>(callerId).AbortVideoUploadAsync(ticketId, ct);

    public Task<UploadLimits> GetUploadLimitsAsync(Guid callerId, Guid peerId, CancellationToken ct = default)
        => GrainFactory.GetGrain<IFileStorageGrain>(callerId).GetUploadLimitsAsync(null, ct);

    private async Task<bool> IsBlockedByAsync(Guid peerId, Guid userId, CancellationToken ct)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);
        return await ctx.UserBlocklist.AnyAsync(x => x.UserId == peerId && x.BlockedId == userId, ct);
    }

    // A direct-chat file records its conversation as its channel, which is what lets the other side
    // copy it later; the conversation is created here if the first thing sent into it is a file.
    private async Task<FileUploadRequest> DirectTargetAsync(Guid userId, Guid peerId, CancellationToken ct)
    {
        var conversation = await conversationService.GetOrCreateConversationAsync(userId, peerId, ct);
        return new FileUploadRequest(FilePurpose.DirectAttachment, "", 0, null, conversation.Id);
    }

    /// <summary>
    /// The card for a link in a direct message, settled before the insert: the client's stub is
    /// reduced to its URL, the crawler gets the send budget, and the stub is filled or dropped. No
    /// deferred path here — direct messages have no MessageUpdated counterpart yet, so a page the
    /// crawler had not cached in time goes without a card. The composer's lookup while typing is
    /// what makes that rare.
    /// </summary>
    private async Task SettleLinkPreviewAsync(List<IMessageEntity> entities, string text)
    {
        var stub = LinkPreviewEntities.TakeStub(entities, text);
        if (stub is null)
            return;

        var outcome = await linkPreviews.ResolveAsync(stub.url, crawlerOptions.Value.SendBudget);
        if (outcome.Status == LinkPreviewStatus.Ready)
            entities[entities.IndexOf(stub)] = LinkPreviewEntities.Fill(stub, outcome.Preview!);
        else
            entities.Remove(stub);
    }

    /// <summary>
    /// Stickers and custom emoji from spaces the sender is a member of, rewritten from the live items.
    /// Each is held to the space its entity names; anything else is dropped.
    /// </summary>
    private async Task<List<IMessageEntity>> ResolveExpressionsAsync(Guid senderId, string text, List<IMessageEntity> entities)
    {
        if (!ExpressionEntities.Any(entities))
            return entities;

        var items = new Dictionary<Guid, ExpressionItem>();

        foreach (var (spaceId, ids) in ExpressionEntities.ClaimedItems(entities))
        {
            if (spaceId == Guid.Empty || await permissionCache.GetMemberWithArchetypesAsync(spaceId, senderId) is null)
                continue;

            try
            {
                foreach (var (id, item) in await GrainFactory.GetGrain<ISpaceExpressionsGrain>(spaceId).ResolveLiveItemsAsync(ids))
                    items[id] = item;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "could not resolve stickers and emoji of space {SpaceId} for a direct message", spaceId);
            }
        }

        return ExpressionEntities.Resolve(text, entities, items, null, expressionsOptions.Value.MaxCustomEmojiPerMessage);
    }

    /// <summary>
    /// Video entities rewritten from their files' media records. One that is not a video the sender
    /// uploaded into this conversation is dropped: a direct send has no error to answer with.
    /// </summary>
    private async Task<List<IMessageEntity>> ResolveVideosAsync(Guid senderId, Guid conversationId, List<IMessageEntity> entities,
        CancellationToken ct)
    {
        var claimed = entities.OfType<MessageEntityVideo>().Select(v => v.fileId).Distinct().ToList();
        if (claimed.Count == 0)
            return entities;

        var found = await GrainFactory.GetGrain<IFileStorageGrain>(senderId).GetSendableVideosAsync(conversationId, claimed, ct);

        var resolved = new List<IMessageEntity>(entities.Count);
        foreach (var entity in entities)
        {
            if (entity is not MessageEntityVideo video)
                resolved.Add(entity);
            else if (found.TryGetValue(video.fileId, out var info))
                resolved.Add(VideoMedia.FromRecord(video, info));
            else
                logger.LogWarning("Video {FileId} dropped from a direct message: not one {SenderId} uploaded into conversation {ConversationId}",
                    video.fileId, senderId, conversationId);
        }

        return resolved;
    }

    public async Task<long> SendDirectMessageAsync(
        Guid receiverId,
        string text,
        List<IMessageEntity> entities,
        long randomId,
        long? replyTo,
        CancellationToken ct = default)
    {
        var senderId = Me;

        logger.LogInformation(
            "SendDirectMessage: {SenderId} -> {ReceiverId}, TextLength={TextLength}, RandomId={RandomId}",
            senderId, receiverId, text?.Length ?? 0, randomId);

        entities ??= [];

        // The channel's cap on files per message.
        if (entities.Count(e => e is MessageEntityAttachment or MessageEntityGif or MessageEntityVideo) > 10)
            throw new InvalidOperationException("too many attachments in one message");

        var expressions = ExpressionEntities.Any(entities);
        entities = await ResolveExpressionsAsync(senderId, text ?? "", entities);

        // A sticker that did not resolve would leave an empty message.
        if (expressions && string.IsNullOrEmpty(text) && entities.Count == 0)
            throw new InvalidOperationException("the sticker is not one the sender can use");

        // Get or create conversation
        var conversation = await conversationService.GetOrCreateConversationAsync(senderId, receiverId, ct);

        var videos = entities.Any(e => e is MessageEntityVideo);
        entities = await ResolveVideosAsync(senderId, conversation.Id, entities, ct);

        if (videos && string.IsNullOrEmpty(text) && entities.Count == 0)
            throw new InvalidOperationException("the video is not one the sender uploaded into this chat");

        await SettleLinkPreviewAsync(entities, text ?? "");

        await using var ctx = await context.CreateDbContextAsync(ct);

        // A blocked sender is not told: the message is kept for them and never reaches the receiver.
        var blockedByReceiver = await ctx.UserBlocklist
            .AnyAsync(x => x.UserId == receiverId && x.BlockedId == senderId, ct);

        var now         = DateTimeOffset.UtcNow;
        var previewText = text?.Length > 200 ? text[..200] : text;

        DirectMessageV2Entity  message;
        DirectMessageV2Entity? inserted = null;

        try
        {
            // MessageId comes back from the insert, so a commit that failed ambiguously is checked for
            // by that id instead of being retried into a second copy of the message.
            message = await ctx.Database.CreateExecutionStrategy().ExecuteInTransactionAsync(
                async token =>
                {
                    ctx.ChangeTracker.Clear();

                    var entity = new DirectMessageV2Entity
                    {
                        ConversationId = conversation.Id,
                        SenderId = senderId,
                        Text = text ?? "",
                        Entities = entities ?? [],
                        CreatedAt = now,
                        CreatorId = senderId,
                        ReplyTo = replyTo,
                        IsHiddenFromReceiver = blockedByReceiver
                    };

                    ctx.DirectMessages.Add(entity);

                    if (blockedByReceiver)
                    {
                        await UpdateUserConversationAsync(ctx, senderId, receiverId, conversation, previewText, now, false, token);
                        await ctx.SaveChangesAsync(token);
                        inserted = entity;
                        return entity;
                    }

                    await ctx.Conversations.Where(x => x.Id == conversation.Id)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(x => x.LastMessageAt, now)
                            .SetProperty(x => x.LastMessageText, previewText)
                            .SetProperty(x => x.LastMessageSenderId, senderId), token);

                    // An ignored sender still gets through — the chat stays honest on both sides — but
                    // they do not raise the receiver's unread count.
                    var ignoredByReceiver = await ctx.UserIgnorelist
                        .AnyAsync(x => x.UserId == receiverId && x.IgnoredId == senderId, token);

                    // Both metadata rows share one round trip, in the same order for either send direction.
                    await UserConversationWrites.RecordParticipantsAsync(ctx, senderId, receiverId,
                        conversation.Id, previewText, now, !ignoredByReceiver, token);

                    await ctx.SaveChangesAsync(token);

                    inserted = entity;
                    return entity;
                },
                token => ctx.DirectMessages
                    .AsNoTracking()
                    .AnyAsync(m => m.ConversationId == conversation.Id && m.MessageId == inserted!.MessageId, token),
                ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send direct message from {SenderId} to {ReceiverId}", senderId, receiverId);
            throw;
        }

        var messageDto = message.ToDto(receiverId);

        var dmEvent = new DirectMessageSent(senderId, receiverId, messageDto);

        await NotifyAsync(senderId, dmEvent);
        if (!blockedByReceiver)
            await NotifyAsync(receiverId, dmEvent);

        logger.LogInformation("DirectMessage sent: MessageId={MessageId}, ConversationId={ConversationId}",
            message.MessageId, conversation.Id);

        var statsGrain = GrainFactory.GetGrain<IUserStatsGrain>(senderId);
        _ = statsGrain.IncrementMessagesAsync();

        return message.MessageId;
    }

    public async Task<List<DirectMessage>> QueryDirectMessagesAsync(
        Guid peerId,
        long? from,
        int limit,
        CancellationToken ct = default)
    {
        var me = Me;

        logger.LogDebug("QueryDirectMessages: {Me} <-> {Peer}, From={From}, Limit={Limit}", me, peerId, from, limit);

        var conversationId = ConversationEntity.GenerateConversationId(me, peerId);

        await using var ctx = await context.CreateDbContextAsync(ct);

        // Direct messages are not ArgonEntity rows, so the global soft-delete filter does not
        // cover them; a message moderation took down has to be left out here by hand.
        var query = ctx.DirectMessages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId && !m.IsDeleted
                     && (!m.IsHiddenFromReceiver || m.SenderId == me));

        if (from.HasValue)
        {
            query = query.Where(m => m.MessageId < from.Value);
        }

        var messages = await query
            .OrderByDescending(m => m.MessageId)
            .Take(limit)
            .ToListAsync(ct);

        // Determine receiver for each message
        return messages.Select(m =>
        {
            var receiverId = m.SenderId == me ? peerId : me;
            return m.ToDto(receiverId);
        }).ToList();
    }

    public async Task UpdateChatForAsync(
        Guid userId,
        Guid peerId,
        string? previewText,
        DateTimeOffset timestamp,
        CancellationToken ct = default)
    {
        logger.LogDebug("UpdateChatForAsync: {UserId} <-> {Peer}", userId, peerId);

        var conversation = await conversationService.GetOrCreateConversationAsync(userId, peerId, ct);

        await using var ctx = await context.CreateDbContextAsync(ct);

        await ExecuteInTransactionAsync(ctx, async () =>
        {
            await ctx.Conversations.Where(x => x.Id == conversation.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.LastMessageAt, timestamp)
                    .SetProperty(x => x.LastMessageText, previewText), ct);

            // Update user conversation
            await UpdateUserConversationAsync(ctx, userId, peerId, conversation, previewText, timestamp, false, ct);

            await ctx.SaveChangesAsync(ct);
        }, ct);

        await NotifyAsync(userId, new RecentChatUpdatedEvent(
            peerId,
            userId,
            previewText,
            timestamp.UtcDateTime
        ));
    }

    private static Task UpdateUserConversationAsync(
        ApplicationDbContext ctx,
        Guid userId,
        Guid peerId,
        ConversationEntity conversation,
        string? previewText,
        DateTimeOffset timestamp,
        bool incrementUnread,
        CancellationToken ct)
        => UserConversationWrites.RecordMessageAsync(ctx, userId, peerId, conversation.Id,
            previewText, timestamp, incrementUnread, ct);

    private async Task NotifyAsync<T>(Guid userId, T payload) where T : IArgonEvent
    {
        var sessions = await sessionDiscovery.GetUserSessionsAsync(userId);

        if (sessions.Count == 0) return;

        await notifier.NotifySessionsAsync(sessions, payload);
    }

    private static async Task ExecuteInTransactionAsync(
        ApplicationDbContext ctx,
        Func<Task> action,
        CancellationToken ct)
    {
        var strategy = ctx.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async token =>
        {
            ctx.ChangeTracker.Clear();
            await using var transaction = await ctx.Database.BeginTransactionAsync(token);
            await action();
            await transaction.CommitAsync(token);
        }, ct);
    }
}