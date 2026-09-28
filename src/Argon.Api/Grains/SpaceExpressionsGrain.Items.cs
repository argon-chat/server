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
        var rights   = await RightsAsync(callerId);

        if (!rights.MayCreate)
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

        if (!await TakeMutationAsync(rights.Bot))
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

    public async Task<ImportedItem> ImportItem(ExpressionImport request)
    {
        var callerId = this.GetUserId();
        var rights   = await RightsAsync(callerId);

        if (!rights.MayCreate)
            return Refused(ExpressionError.FORBIDDEN);

        await using var ctx = await context.CreateDbContextAsync();

        var pack = await ctx.ExpressionPacks.FirstOrDefaultAsync(p => p.Id == request.PackId && p.SpaceId == SpaceId);
        if (pack is null)
            return Refused(ExpressionError.NOT_FOUND);

        var emoji   = request.Emoji ?? [];
        var keywords = request.Keywords ?? [];

        if (!ExpressionLimits.IsValidName(pack.Kind, request.Name) || !ExpressionLimits.AreValidAssociatedEmoji(emoji)
         || !ExpressionLimits.AreValidKeywords(keywords))
            return Refused(ExpressionError.INVALID_FORMAT);

        if (pack.ItemCount >= Limits.ItemsPerPack(pack.Kind))
            return Refused(ExpressionError.QUOTA_EXCEEDED);

        var used = await ctx.ExpressionItems.CountAsync(i => i.SpaceId == SpaceId && i.Kind == pack.Kind);
        if (used >= Limits.SlotsFor(pack.Kind, await BoostLevelAsync(ctx)))
            return Refused(ExpressionError.QUOTA_EXCEEDED);

        if (pack.Kind == ExpressionKind.Emoji && await EmojiNameTakenAsync(ctx, request.Name, null))
            return Refused(ExpressionError.NAME_TAKEN);

        // Uploads are looked up before the budget is spent and read after it.
        var main = await ResolveInputAsync(callerId, request.File);
        if (main.Error is { } missing)
            return Refused(missing);

        if (FormatOf(main) is { } format and not ExpressionFormat.Static && !renderer.IsAvailable(format))
            return ImportedItem.Unrenderable;

        if (!await TakeMutationAsync(rights.Bot))
            return Refused(ExpressionError.RATE_LIMITED);

        var stored = await StoreBytesAsync(callerId, pack.Kind, main);
        if (stored.Error is { } refused)
            return Refused(refused);

        var before = (await SnapshotAsync()).Version;

        var item = new ExpressionItemEntity
        {
            Id          = ArgonId.New(),
            PackId      = pack.Id,
            SpaceId     = SpaceId,
            Kind        = pack.Kind,
            Format      = stored.Format,
            Name        = request.Name,
            FileId      = stored.FileId,
            ThumbFileId = stored.ThumbFileId,
            Width       = stored.Width,
            Height      = stored.Height,
            FileSize    = stored.FileSize,
            Emoji       = emoji.ToList(),
            Keywords    = keywords.ToList(),
            Outline     = stored.Outline,
            TextColor   = request.TextColor,
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
            return Refused(ExpressionError.NAME_TAKEN);
        }

        await ClaimUploadAsync(callerId, request.File);

        var after = await RefreshAsync();
        var dto   = item.ToDto(s3.GetFileDownloadUrl);

        await FireAsync(after, before, new ItemUpserted(dto));
        return new ImportedItem(ExpressionError.NONE, dto);

        static ImportedItem Refused(ExpressionError error) => new(error, null);
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
        if (emoji.HasValue && !ExpressionLimits.AreValidAssociatedEmoji(emoji.Value.Values))
            return new FailedItem(ExpressionError.INVALID_FORMAT);
        if (keywords.HasValue && !ExpressionLimits.AreValidKeywords(keywords.Value.Values))
            return new FailedItem(ExpressionError.INVALID_FORMAT);

        var newName      = name.HasValue ? name.Value! : item.Name;
        var newEmoji     = emoji.HasValue ? emoji.Value.ToList() : emoji.IsRemoved ? [] : item.Emoji;
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

        if (!await TakeMutationAsync(rights.Bot))
            return new FailedItem(ExpressionError.RATE_LIMITED);

        var before  = (await SnapshotAsync()).Version;
        var renamed = newName != item.Name;

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

        if (renamed)
            await ForgetItemsAsync([itemId]);

        var after = await RefreshAsync();
        var dto   = item.ToDto(s3.GetFileDownloadUrl);

        await FireAsync(after, before, new ItemUpserted(dto));
        return new SuccessItem(dto);
    }

    public async Task<IItemResult> DeleteItem(Guid itemId)
    {
        var callerId = this.GetUserId();
        var rights   = await RightsAsync(callerId);

        if (rights.Bot || !rights.MayCreate)
            return new FailedItem(ExpressionError.FORBIDDEN);

        await using var ctx = await context.CreateDbContextAsync();

        var item = await ctx.ExpressionItems.FirstOrDefaultAsync(i => i.Id == itemId && i.SpaceId == SpaceId);
        var pack = item is null ? null : await ctx.ExpressionPacks.FirstOrDefaultAsync(p => p.Id == item.PackId);

        if (item is null || pack is null)
            return new FailedItem(ExpressionError.NOT_FOUND);
        if (!rights.MayChange(item.CreatorId, callerId))
            return new FailedItem(ExpressionError.FORBIDDEN);

        if (!await TakeMutationAsync(rights.Bot))
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
        await ForgetItemsAsync([itemId]);

        var after = await RefreshAsync();

        await FireAsync(after, before, new ItemDeleted(pack.Id, itemId));
        return new SuccessItem(item.ToDto(s3.GetFileDownloadUrl));
    }

    public async Task<IReorderResult> ReorderItems(Guid packId, List<Guid> ordered)
    {
        var rights = await RightsAsync(this.GetUserId());

        if (rights.Bot || !rights.Manage)
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

        if (!await TakeMutationAsync(rights.Bot))
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
           .Select(i => new { i.Id, i.FileId, i.ThumbFileId })
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
        await ForgetItemsAsync(files.Select(f => f.Id));

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

    /// <summary>A file taken for an item: its record, and its bytes unless it is of the wrong purpose.</summary>
    private sealed record TakenFile(FileInfoResponse File, byte[]? Data);

    /// <summary>A bot's file for <see cref="ImportItem"/>: inline bytes, or where its upload lies.</summary>
    private sealed record ImportFile(ExpressionError? Error, byte[]? Data = null, BotStoredFile? Upload = null);

    /// <summary>Finalizes the client's upload (and the thumbnail of an animated one), then <see cref="ProcessUploadAsync"/>.</summary>
    private Task<StoredUpload> StoreUploadAsync(Guid callerId, ExpressionKind kind, Guid blobId, Guid? thumbBlobId, byte[]? clientOutline)
    {
        var files   = GrainFactory.GetGrain<IFileStorageGrain>(callerId);
        var purpose = ExpressionUploads.PurposeFor(kind);

        return ProcessUploadAsync(callerId, kind, thumbBlobId is not null, clientOutline,
            () => FinalizeAndReadAsync(files, blobId, purpose),
            () => FinalizeAndReadAsync(files, thumbBlobId!.Value, purpose));
    }

    /// <summary>Stores a copy of the bot's bytes for the item, then <see cref="ProcessUploadAsync"/>. Bots send no thumbnail.</summary>
    private async Task<StoredUpload> StoreBytesAsync(Guid callerId, ExpressionKind kind, ImportFile main)
    {
        var data = await BytesOfAsync(main);
        if (data is null)
            return new StoredUpload(ExpressionError.NOT_FOUND);

        // Checked before anything is stored: what fails here would only be refused after the copy.
        if (ExpressionUploads.Sniff(data) is not { } format || !ThumbFits(format, false))
            return new StoredUpload(ExpressionError.INVALID_FORMAT);
        if (data.Length > ExpressionUploads.MaxUploadBytes(kind, format))
            return new StoredUpload(ExpressionError.TOO_LARGE);

        var files = GrainFactory.GetGrain<IFileStorageGrain>(callerId);

        return await ProcessUploadAsync(callerId, kind, false, null,
            () => CopyAsync(files, kind, data),
            () => Task.FromResult<TakenFile?>(null));
    }

    /// <summary>The format of a bot's file, from its bytes or from the type its upload was stored under.</summary>
    private static ExpressionFormat? FormatOf(ImportFile file)
        => file.Data is { } data ? ExpressionUploads.Sniff(data) : ExpressionUploads.FormatOf(file.Upload?.ContentType);

    /// <summary>A static item takes no thumbnail; an animated one needs the client's unless the server draws its first frame.</summary>
    private bool ThumbFits(ExpressionFormat format, bool hasThumb)
        => format == ExpressionFormat.Static ? !hasThumb : hasThumb || renderer.IsAvailable(format);

    /// <summary>
    /// Checks the bytes, stores the re-encoded form (and the thumbnail of an animated item) and runs moderation.
    /// Every file <paramref name="main"/> and <paramref name="thumb"/> hand over holds a reference, released on a refusal.
    /// </summary>
    private async Task<StoredUpload> ProcessUploadAsync(Guid callerId, ExpressionKind kind, bool hasThumb, byte[]? clientOutline,
        Func<Task<TakenFile?>> main, Func<Task<TakenFile?>> thumb)
    {
        var purpose = ExpressionUploads.PurposeFor(kind);
        var held    = new List<Guid>();

        try
        {
            if (await main() is not { } taken)
                return new StoredUpload(ExpressionError.NOT_FOUND);

            held.Add(taken.File.FileId);

            if (taken.Data is not { } data)
                return await RefuseAsync(ExpressionError.INVALID_FORMAT);

            if (ExpressionUploads.Sniff(data) is not { } format || !ThumbFits(format, hasThumb))
                return await RefuseAsync(ExpressionError.INVALID_FORMAT);

            var file = await validator.ValidateAsync(new MemoryStream(data, writable: false), kind, format, CancellationToken.None);
            if (!file.Ok)
                return await RefuseAsync(file.Error);

            // PNG is stored as WEBP and JSON as TGS; everything is stored under the type it really is. Unchanged
            // bytes are written back too: the client's PUT does not set the Cache-Control.
            await StoreAsync(taken.File, file.Reencoded ?? data, file.StoredContentType,
                file.Reencoded is not null || ExpressionUploads.MediaType(taken.File.ContentType) != file.StoredContentType);

            FileInfoResponse? thumbFile = null;
            var               outline   = format == ExpressionFormat.Static ? file.Outline : ExpressionUploads.AcceptOutline(clientOutline);

            if (format != ExpressionFormat.Static)
            {
                // The server's frame is the thumbnail stored and judged; the client's stands in only when there is none.
                using var frame    = await renderer.RenderAsync(data, format, file.Width, file.Height, CancellationToken.None);
                var       rendered = frame is null ? null : FirstFrameRenderer.EncodeThumb(frame);

                if (hasThumb)
                {
                    if (await thumb() is not { } takenThumb)
                        return await RefuseAsync(ExpressionError.NOT_FOUND);

                    thumbFile = takenThumb.File;
                    held.Add(thumbFile.FileId);

                    if (takenThumb.Data is not { } thumbData)
                        return await RefuseAsync(ExpressionError.INVALID_FORMAT);

                    var checkedThumb = await validator.ValidateThumbAsync(new MemoryStream(thumbData, writable: false), file.Width,
                        file.Height, CancellationToken.None);
                    if (!checkedThumb.Ok)
                        return await RefuseAsync(ExpressionError.INVALID_FORMAT);

                    await StoreAsync(thumbFile, rendered ?? thumbData, checkedThumb.StoredContentType,
                        rendered is not null || ExpressionUploads.MediaType(thumbFile.ContentType) != checkedThumb.StoredContentType);
                }
                else if (rendered is null)
                {
                    return await RefuseAsync(ExpressionError.INVALID_FORMAT);
                }
                else
                {
                    thumbFile = await GrainFactory.GetGrain<IFileStorageGrain>(callerId).StoreAsync(
                        new FileUploadRequest(purpose, ExpressionContentTypes.Webp, rendered.Length, SpaceId), rendered,
                        ExpressionUploads.CacheControl, null);
                    held.Add(thumbFile.FileId);
                }

                if (outline is null && frame is not null && kind == ExpressionKind.Sticker)
                    outline = OutlineTracer.FromAlpha(frame);
            }

            // Animated items are judged by their first frame.
            var judged    = thumbFile ?? taken.File;
            var moderated = await GrainFactory.GetGrain<IContentModerationGrain>(Guid.Empty).EvaluateAsync(judged.S3Key, purpose);

            if (moderated.Action == ContentAction.Deny)
            {
                await RecordViolationAsync(callerId, judged.FileId, purpose, moderated);

                logger.LogWarning("Expression rejected in space {SpaceId} for user {UserId}, file {FileId}, stages={Stages}",
                    SpaceId, callerId, judged.FileId, moderated.StagesUsed);

                return await RefuseAsync(ExpressionError.CONTENT_REJECTED);
            }

            return new StoredUpload(null, format, taken.File.FileId, thumbFile?.FileId, file.Width, file.Height, file.FileSize, outline,
                held);
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

    private async Task<TakenFile?> FinalizeAndReadAsync(IFileStorageGrain files, Guid blobId, FilePurpose purpose)
    {
        if (await FinalizeAsync(files, blobId) is not { } file)
            return null;

        return new TakenFile(file, file.Purpose == purpose ? await ReadAsync(file.S3Key) : null);
    }

    private async Task<TakenFile?> CopyAsync(IFileStorageGrain files, ExpressionKind kind, byte[] data)
    {
        var type = ExpressionUploads.ContentTypeOf(data) ?? ExpressionUploads.OctetStream;
        var file = await files.StoreAsync(new FileUploadRequest(ExpressionUploads.PurposeFor(kind), type, data.Length, SpaceId), data,
            ExpressionUploads.CacheControl, null);

        return new TakenFile(file, data);
    }

    private async Task<ImportFile> ResolveInputAsync(Guid callerId, ExpressionFileInput input)
    {
        if (input.Data is { Length: > 0 } data)
            return new ImportFile(null, data);

        if (input.FileId is { } fileId)
            return await GrainFactory.GetGrain<IBotFilesGrain>(callerId).ResolveUploadAsync(fileId) is { } upload
                ? new ImportFile(null, Upload: upload)
                : new ImportFile(ExpressionError.NOT_FOUND);

        return new ImportFile(ExpressionError.INVALID_FORMAT);
    }

    private async Task<byte[]?> BytesOfAsync(ImportFile file)
        => file.Data ?? (file.Upload is { } upload ? await ReadAsync(upload.S3Key) : null);

    /// <summary>The upload an item was made from stops counting against the bot's pending ones.</summary>
    private async Task ClaimUploadAsync(Guid callerId, ExpressionFileInput file)
    {
        if (file.FileId is not { } id)
            return;

        try
        {
            await GrainFactory.GetGrain<IBotFilesGrain>(callerId).ClaimAsync(id);
        }
        catch (Exception e)
        {
            // It only frees a pending slot; the upload expires on its own.
            logger.LogWarning(e, "could not claim bot upload {FileId} in space {SpaceId}", id, SpaceId);
        }
    }

    private async Task<byte[]?> ReadAsync(string s3Key)
    {
        var stream = await s3.GetObjectStreamAsync(s3Key);
        if (stream is null)
            return null;

        await using (stream)
        {
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            return copy.ToArray();
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

    private async Task StoreAsync(FileInfoResponse file, byte[] data, string contentType, bool changed)
    {
        using (var content = new MemoryStream(data, writable: false))
            if (!await s3.PutObjectAsync(file.S3Key, content, contentType, ExpressionUploads.CacheControl))
                throw new InvalidOperationException($"could not store expression file {file.FileId}");

        if (!changed)
            return;

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
