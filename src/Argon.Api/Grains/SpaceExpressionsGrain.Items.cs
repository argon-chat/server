namespace Argon.Grains;

using Argon.Api.Grains.Interfaces;
using Argon.Features.Expressions;
using Argon.Features.Storage;
using ion.runtime;

public partial class SpaceExpressionsGrain
{
    public async Task<IUploadFileResult> BeginUploadExpression(ExpressionKind kind, ExpressionFormat format, string contentType, long size)
    {
        var callerId = this.GetUserId();

        if (!(await RightsAsync(callerId)).MayCreate)
            return new FailedUploadFile(UploadFileError.NOT_AUTHORIZED);

        // A thumbnail is asked for the same way: Static, image/webp, within the static cap.
        if (!kind.IsKnown() || !format.IsKnown() || !ExpressionUploads.Accepts(format, contentType)
         || size <= 0 || size > ExpressionUploads.MaxUploadBytes(kind, format))
            return new FailedUploadFile(UploadFileError.NOT_AUTHORIZED);

        await using (var ctx = await context.CreateDbContextAsync())
        {
            var used = (await SnapshotAsync()).Value.Where(p => p.kind == kind).Sum(p => p.items.Count);
            if (used >= Limits.SlotsFor(kind, await BoostLevelAsync(ctx)))
                return new FailedUploadFile(UploadFileError.NOT_AUTHORIZED);
        }

        try
        {
            var ticket = await GrainFactory.GetGrain<IFileStorageGrain>(callerId).RequestUploadAsync(
                new FileUploadRequest(ExpressionUploads.PurposeFor(kind), ExpressionUploads.MediaType(contentType), size, SpaceId));

            return new SuccessUploadFile(ticket.BlobId, ticket.Url,
                new IonArray<FormField>(ticket.Fields.Select(f => new FormField(f.Key, f.Value)).ToList()), ticket.TtlSeconds);
        }
        catch (Exception e)
        {
            logger.LogError(e, "could not start an expression upload in space {SpaceId}", SpaceId);
            return new FailedUploadFile(UploadFileError.INTERNAL_ERROR);
        }
    }

    public async Task<IItemResult> AddItem(Guid packId, Guid blobId, Guid? thumbBlobId, string name, List<string> emoji,
        List<string> keywords, byte[]? outline)
    {
        var callerId = this.GetUserId();

        if (!(await RightsAsync(callerId)).MayCreate)
            return new FailedItem(ExpressionError.FORBIDDEN);

        await using var ctx = await context.CreateDbContextAsync();

        var pack = await ctx.ExpressionPacks.FirstOrDefaultAsync(p => p.Id == packId && p.SpaceId == SpaceId);
        if (pack is null)
            return new FailedItem(ExpressionError.NOT_FOUND);

        emoji    ??= [];
        keywords ??= [];

        if (!ExpressionLimits.IsValidName(pack.Kind, name) || !ExpressionLimits.AreValidAssociatedEmoji(emoji)
         || !ExpressionLimits.AreValidKeywords(keywords))
            return new FailedItem(ExpressionError.INVALID_FORMAT);

        if (pack.ItemCount >= Limits.ItemsPerPack(pack.Kind))
            return new FailedItem(ExpressionError.QUOTA_EXCEEDED);

        var used = await ctx.ExpressionItems.CountAsync(i => i.SpaceId == SpaceId && i.Kind == pack.Kind);
        if (used >= Limits.SlotsFor(pack.Kind, await BoostLevelAsync(ctx)))
            return new FailedItem(ExpressionError.QUOTA_EXCEEDED);

        if (pack.Kind == ExpressionKind.Emoji && await EmojiNameTakenAsync(ctx, name, null))
            return new FailedItem(ExpressionError.NAME_TAKEN);

        if (!await TakeMutationAsync())
            return new FailedItem(ExpressionError.RATE_LIMITED);

        var stored = await StoreUploadAsync(callerId, pack.Kind, blobId, thumbBlobId, outline);
        if (stored.Error is { } refused)
            return new FailedItem(refused);

        var before = (await SnapshotAsync()).Version;

        var item = new ExpressionItemEntity
        {
            Id          = ArgonId.New(),
            PackId      = pack.Id,
            SpaceId     = SpaceId,
            Kind        = pack.Kind,
            Format      = stored.Format,
            Name        = name,
            FileId      = stored.FileId,
            ThumbFileId = stored.ThumbFileId,
            Width       = stored.Width,
            Height      = stored.Height,
            FileSize    = stored.FileSize,
            Emoji       = emoji.ToList(),
            Keywords    = keywords.ToList(),
            Outline     = stored.Outline,
            CreatorId   = callerId,
            SortOrder   = await ctx.ExpressionItems.Where(i => i.PackId == pack.Id).Select(i => (int?)i.SortOrder).MaxAsync() + 1 ?? 0
        };

        ctx.ExpressionItems.Add(item);
        pack.ItemCount++;
        pack.Version++;

        try
        {
            await ctx.SaveChangesAsync();
        }
        catch (DbUpdateException e) when (e.IsUniqueViolation())
        {
            await ReleaseAsync(callerId, stored.Files);
            return new FailedItem(ExpressionError.NAME_TAKEN);
        }

        var after = await RefreshAsync();
        var dto   = item.ToDto(s3.GetFileDownloadUrl);

        await FireAsync(after, before, new ItemUpserted(dto));
        return new SuccessItem(dto);
    }

    public async Task<IItemResult> UpdateItem(Guid itemId, IonPartial<ExpressionItem> patch)
    {
        var callerId = this.GetUserId();
        var rights   = await RightsAsync(callerId);

        if (!rights.MayCreate)
            return new FailedItem(ExpressionError.FORBIDDEN);

        await using var ctx = await context.CreateDbContextAsync();

        var item = await ctx.ExpressionItems.FirstOrDefaultAsync(i => i.Id == itemId && i.SpaceId == SpaceId);
        var pack = item is null ? null : await ctx.ExpressionPacks.FirstOrDefaultAsync(p => p.Id == item.PackId);

        if (item is null || pack is null)
            return new FailedItem(ExpressionError.NOT_FOUND);
        if (!rights.MayChange(item.CreatorId, callerId))
            return new FailedItem(ExpressionError.FORBIDDEN);

        var name      = patch.GetField(x => x.name);
        var emoji     = patch.GetField(x => x.emoji);
        var keywords  = patch.GetField(x => x.keywords);
        var textColor = patch.GetField(x => x.textColor);
        var outline   = patch.GetField(x => x.outline);

        if (name.IsRemoved || (name.HasValue && !ExpressionLimits.IsValidName(item.Kind, name.Value)))
            return new FailedItem(ExpressionError.INVALID_FORMAT);
        if (emoji.IsRemoved || (emoji.HasValue && !ExpressionLimits.AreValidAssociatedEmoji(emoji.Value.Values)))
            return new FailedItem(ExpressionError.INVALID_FORMAT);
        if (keywords.HasValue && !ExpressionLimits.AreValidKeywords(keywords.Value.Values))
            return new FailedItem(ExpressionError.INVALID_FORMAT);

        var newName      = name.HasValue ? name.Value! : item.Name;
        var newEmoji     = emoji.HasValue ? emoji.Value.ToList() : item.Emoji;
        var newKeywords  = keywords.HasValue ? keywords.Value.ToList() : keywords.IsRemoved ? [] : item.Keywords;
        var newTextColor = textColor.HasValue ? textColor.Value : !textColor.IsRemoved && item.TextColor;

        // A static item's outline is traced from its pixels; only an animated one takes the client's.
        var newOutline = item.Format == ExpressionFormat.Static || !(outline.HasValue || outline.IsRemoved)
            ? item.Outline
            : ExpressionUploads.AcceptOutline(outline.Value?.ToArray());

        var unchanged = newName == item.Name
                     && newEmoji.SequenceEqual(item.Emoji)
                     && newKeywords.SequenceEqual(item.Keywords)
                     && newTextColor == item.TextColor
                     && (newOutline is null ? item.Outline is null : item.Outline is not null && newOutline.AsSpan().SequenceEqual(item.Outline));

        if (unchanged)
            return new SuccessItem(item.ToDto(s3.GetFileDownloadUrl));

        if (item.Kind == ExpressionKind.Emoji && newName != item.Name && await EmojiNameTakenAsync(ctx, newName, item.Id))
            return new FailedItem(ExpressionError.NAME_TAKEN);

        if (!await TakeMutationAsync())
            return new FailedItem(ExpressionError.RATE_LIMITED);

        var before = (await SnapshotAsync()).Version;

        item.Name      = newName;
        item.Emoji     = newEmoji.ToList();
        item.Keywords  = newKeywords.ToList();
        item.TextColor = newTextColor;
        item.Outline   = newOutline;
        pack.Version++;

        try
        {
            await ctx.SaveChangesAsync();
        }
        catch (DbUpdateException e) when (e.IsUniqueViolation())
        {
            return new FailedItem(ExpressionError.NAME_TAKEN);
        }

        var after = await RefreshAsync();
        var dto   = item.ToDto(s3.GetFileDownloadUrl);

        await FireAsync(after, before, new ItemUpserted(dto));
        return new SuccessItem(dto);
    }

    public async Task<IItemResult> DeleteItem(Guid itemId)
    {
        var callerId = this.GetUserId();
        var rights   = await RightsAsync(callerId);

        if (!rights.MayCreate)
            return new FailedItem(ExpressionError.FORBIDDEN);

        await using var ctx = await context.CreateDbContextAsync();

        var item = await ctx.ExpressionItems.FirstOrDefaultAsync(i => i.Id == itemId && i.SpaceId == SpaceId);
        var pack = item is null ? null : await ctx.ExpressionPacks.FirstOrDefaultAsync(p => p.Id == item.PackId);

        if (item is null || pack is null)
            return new FailedItem(ExpressionError.NOT_FOUND);
        if (!rights.MayChange(item.CreatorId, callerId))
            return new FailedItem(ExpressionError.FORBIDDEN);

        if (!await TakeMutationAsync())
            return new FailedItem(ExpressionError.RATE_LIMITED);

        var before = (await SnapshotAsync()).Version;

        // Soft: the row and its file reference stay, so messages that carry it still render.
        item.IsDeleted = true;
        item.DeletedAt = DateTimeOffset.UtcNow;
        pack.ItemCount = Math.Max(0, pack.ItemCount - 1);
        pack.Version++;
        if (pack.CoverItemId == itemId)
            pack.CoverItemId = null;

        await ctx.SaveChangesAsync();

        var after = await RefreshAsync();

        await FireAsync(after, before, new ItemDeleted(pack.Id, itemId));
        return new SuccessItem(item.ToDto(s3.GetFileDownloadUrl));
    }

    public async Task<IReorderResult> ReorderItems(Guid packId, List<Guid> ordered)
    {
        if (!(await RightsAsync(this.GetUserId())).Manage)
            return new FailedReorder(ExpressionError.FORBIDDEN);

        await using var ctx = await context.CreateDbContextAsync();

        var pack = await ctx.ExpressionPacks.FirstOrDefaultAsync(p => p.Id == packId && p.SpaceId == SpaceId);
        if (pack is null)
            return new FailedReorder(ExpressionError.NOT_FOUND);

        var items = await ctx.ExpressionItems.Where(i => i.PackId == packId).ToDictionaryAsync(i => i.Id);

        if (!IsPermutation(ordered, items.Keys))
            return new FailedReorder(ExpressionError.INVALID_FORMAT);

        var moved = ordered.Select((id, index) => (Item: items[id], Index: index)).Where(x => x.Item.SortOrder != x.Index).ToList();
        if (moved.Count == 0)
            return new SuccessReorder(new IonArray<Guid>(ordered));

        if (!await TakeMutationAsync())
            return new FailedReorder(ExpressionError.RATE_LIMITED);

        var before = (await SnapshotAsync()).Version;

        foreach (var (item, index) in moved)
            item.SortOrder = index;
        pack.Version++;

        await ctx.SaveChangesAsync();

        var after = await RefreshAsync();

        await FireAsync(after, before, new ItemsReordered(packId, new IonArray<Guid>(ordered.ToList())));
        return new SuccessReorder(new IonArray<Guid>(ordered));
    }

    public async Task OnSpaceDeletedAsync()
    {
        await using var ctx = await context.CreateDbContextAsync();

        var now = DateTimeOffset.UtcNow;

        // Deleted items' files too: they kept their references for old messages, and those go with the space.
        var files = await ctx.ExpressionItems
           .IgnoreQueryFilters()
           .AsNoTracking()
           .Where(i => i.SpaceId == SpaceId)
           .Select(i => new { i.FileId, i.ThumbFileId })
           .ToListAsync();

        await ctx.ExpressionItems
           .Where(i => i.SpaceId == SpaceId)
           .ExecuteUpdateAsync(s => s.SetProperty(i => i.IsDeleted, true).SetProperty(i => i.DeletedAt, now));

        await ctx.ExpressionPacks
           .Where(p => p.SpaceId == SpaceId)
           .ExecuteUpdateAsync(s => s.SetProperty(p => p.IsDeleted, true).SetProperty(p => p.DeletedAt, now));

        // The uploaders own the files, so this is the grain-side counter rather than their storage grains.
        foreach (var fileId in files.SelectMany(f => f.ThumbFileId is { } thumb ? [f.FileId, thumb] : new[] { f.FileId }).Distinct())
        {
            try
            {
                await refCount.DecrementAsync(fileId);
            }
            catch (KeyNotFoundException)
            {
                // Never finalized, or collected already.
            }
        }

        await cache.RemoveAsync(CacheKey(SpaceId));

        generation++;
        Remember(new Versioned<IonArray<ExpressionPack>>(ExpressionsVersion.Empty, IonArray<ExpressionPack>.Empty));
    }

    // ── upload ──────────────────────────────────────────────────────────────────────────────────

    private sealed record StoredUpload(
        ExpressionError?  Error,
        ExpressionFormat  Format = ExpressionFormat.Static,
        Guid              FileId = default,
        Guid?             ThumbFileId = null,
        int               Width = 0,
        int               Height = 0,
        int               FileSize = 0,
        byte[]?           Outline = null,
        List<Guid>?       Files = null);

    /// <summary>
    /// Finalizes the upload (and the thumbnail of an animated one), checks the bytes, stores the
    /// re-encoded form and runs moderation. On a refusal every reference taken here is released.
    /// </summary>
    private async Task<StoredUpload> StoreUploadAsync(Guid callerId, ExpressionKind kind, Guid blobId, Guid? thumbBlobId, byte[]? clientOutline)
    {
        var files   = GrainFactory.GetGrain<IFileStorageGrain>(callerId);
        var purpose = ExpressionUploads.PurposeFor(kind);
        var held    = new List<Guid>();

        try
        {
            if (await FinalizeAsync(files, blobId) is not { } main)
                return new StoredUpload(ExpressionError.NOT_FOUND);

            held.Add(main.FileId);

            var bytes = main.Purpose == purpose ? await s3.GetObjectStreamAsync(main.S3Key) : null;
            if (bytes is null)
                return await RefuseAsync(ExpressionError.INVALID_FORMAT);

            byte[] data;
            await using (bytes)
            {
                using var copy = new MemoryStream();
                await bytes.CopyToAsync(copy);
                data = copy.ToArray();
            }

            if (ExpressionUploads.Sniff(data) is not { } format || (format == ExpressionFormat.Static) != (thumbBlobId is null))
                return await RefuseAsync(ExpressionError.INVALID_FORMAT);

            var file = await validator.ValidateAsync(new MemoryStream(data, writable: false), kind, format, CancellationToken.None);
            if (!file.Ok)
                return await RefuseAsync(file.Error);

            // PNG is stored as WEBP and JSON as TGS; everything is stored under the type it really is.
            if (file.Reencoded is not null || ExpressionUploads.MediaType(main.ContentType) != file.StoredContentType)
                await OverwriteAsync(main, file.Reencoded ?? data, file.StoredContentType);

            FileInfoResponse? thumb = null;

            if (format != ExpressionFormat.Static)
            {
                thumb = await FinalizeAsync(files, thumbBlobId!.Value);
                if (thumb is null)
                    return await RefuseAsync(ExpressionError.NOT_FOUND);

                held.Add(thumb.FileId);

                var thumbBytes = thumb.Purpose == purpose ? await s3.GetObjectStreamAsync(thumb.S3Key) : null;
                if (thumbBytes is null)
                    return await RefuseAsync(ExpressionError.INVALID_FORMAT);

                await using (thumbBytes)
                {
                    var checkedThumb = await validator.ValidateThumbAsync(thumbBytes, file.Width, file.Height, CancellationToken.None);
                    if (!checkedThumb.Ok)
                        return await RefuseAsync(ExpressionError.INVALID_FORMAT);
                }
            }

            // Animated items are judged by their first frame.
            var judged    = thumb ?? main;
            var moderated = await GrainFactory.GetGrain<IContentModerationGrain>(Guid.Empty).EvaluateAsync(judged.S3Key, purpose);

            if (moderated.Action == ContentAction.Deny)
            {
                await RecordViolationAsync(callerId, judged.FileId, purpose, moderated);

                logger.LogWarning("Expression rejected in space {SpaceId} for user {UserId}, file {FileId}, stages={Stages}",
                    SpaceId, callerId, judged.FileId, moderated.StagesUsed);

                return await RefuseAsync(ExpressionError.CONTENT_REJECTED);
            }

            return new StoredUpload(null, format, main.FileId, thumb?.FileId, file.Width, file.Height, file.FileSize,
                format == ExpressionFormat.Static ? file.Outline : ExpressionUploads.AcceptOutline(clientOutline), held);
        }
        catch (Exception)
        {
            await ReleaseAsync(callerId, held);
            throw;
        }

        async Task<StoredUpload> RefuseAsync(ExpressionError error)
        {
            await ReleaseAsync(callerId, held);
            return new StoredUpload(error);
        }
    }

    private async Task<FileInfoResponse?> FinalizeAsync(IFileStorageGrain files, Guid blobId)
    {
        try
        {
            return await files.FinalizeUploadAsync(blobId);
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException)
        {
            // Unknown, expired, of a refused type or over the purpose's size.
            logger.LogInformation(e, "expression upload {BlobId} in space {SpaceId} could not be finalized", blobId, SpaceId);
            return null;
        }
    }

    private async Task OverwriteAsync(FileInfoResponse file, byte[] data, string contentType)
    {
        using (var content = new MemoryStream(data, writable: false))
            if (!await s3.PutObjectAsync(file.S3Key, content, contentType))
                throw new InvalidOperationException($"could not store expression file {file.FileId}");

        await using var ctx = await context.CreateDbContextAsync();
        await ctx.Files
           .Where(f => f.Id == file.FileId)
           .ExecuteUpdateAsync(s => s
               .SetProperty(f => f.ContentType, contentType)
               .SetProperty(f => f.FileSize, (long)data.Length)
               .SetProperty(f => f.UpdatedAt, DateTimeOffset.UtcNow));
    }

    private async Task ReleaseAsync(Guid callerId, List<Guid>? fileIds)
    {
        if (fileIds is null)
            return;

        var files = GrainFactory.GetGrain<IFileStorageGrain>(callerId);
        foreach (var fileId in fileIds)
        {
            try
            {
                await files.DecrementRefAsync(fileId);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "could not release expression file {FileId}", fileId);
            }
        }
    }

    private async Task<bool> EmojiNameTakenAsync(ApplicationDbContext ctx, string name, Guid? except)
        => await ctx.ExpressionItems.AnyAsync(i => i.SpaceId == SpaceId && i.Kind == ExpressionKind.Emoji && i.Name == name
                                                && (except == null || i.Id != except));

    private async Task RecordViolationAsync(Guid userId, Guid fileId, FilePurpose purpose, ContentModerationResult result)
    {
        try
        {
            await using var ctx = await context.CreateDbContextAsync();
            ctx.ContentViolations.Add(new ContentViolationEntity
            {
                Id            = ArgonId.New(),
                UserId        = userId,
                FileId        = fileId,
                FilePurpose   = purpose,
                StagesUsed    = result.StagesUsed,
                PrimaryScores = result.Scores,
                RefinedScores = result.RefinedScores,
                CreatedAt     = DateTimeOffset.UtcNow,
                UpdatedAt     = DateTimeOffset.UtcNow
            });
            await ctx.SaveChangesAsync();

            ModerationInstruments.ViolationsRecorded.Add(1, new KeyValuePair<string, object?>("purpose", purpose.ToString()));
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to record content violation for user {UserId}", userId);
        }
    }
}
