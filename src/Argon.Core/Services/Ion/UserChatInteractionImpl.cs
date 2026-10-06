namespace Argon.Services.Ion;

using Argon.Api.Features.Utils;
using Core.Grains.Interfaces;
using ion.runtime;

public class UserChatInteractionImpl : IUserChatInteractions
{
    public async Task<IonArray<UserChat>> GetRecentChats(int limit, int offset, CancellationToken ct = default)
        => await this.GetGrain<IUserChatGrain>(Guid.CreateVersion7()).GetRecentChatsAsync(limit, offset, ct);

    public async Task PinChat(Guid peerId, CancellationToken ct = default)
        => await this.GetGrain<IUserChatGrain>(Guid.CreateVersion7()).PinChatAsync(peerId, ct);

    public async Task UnpinChat(Guid peerId, CancellationToken ct = default)
        => await this.GetGrain<IUserChatGrain>(Guid.CreateVersion7()).UnpinChatAsync(peerId, ct);

    public async Task MarkChatRead(Guid peerId, CancellationToken ct = default)
        => await this.GetGrain<IUserChatGrain>(Guid.CreateVersion7()).MarkChatReadAsync(peerId, ct);

    public async Task DeleteChat(Guid peerId, CancellationToken ct = default)
        => await this.GetGrain<IUserChatGrain>(Guid.CreateVersion7()).DeleteChatAsync(peerId, ct);

    public async Task<IUploadFileResult> BeginUploadAttachment(Guid peerId, CancellationToken ct = default)
    {
        var result = await this.GetGrain<IUserChatGrain>(Guid.CreateVersion7()).BeginUploadAttachmentAsync(peerId, ct);

        if (result.IsSuccess)
        {
            var t = result.Value;
            return new SuccessUploadFile(t.BlobId, t.Url, UploadHelpers.ToFormFields(t.Fields), t.TtlSeconds);
        }
        return new FailedUploadFile(result.Error);
    }

    public async Task<AttachmentInfo> CompleteUploadAttachment(Guid peerId, Guid blobId, CancellationToken ct = default)
        => await this.GetGrain<IUserChatGrain>(Guid.CreateVersion7()).CompleteUploadAttachmentAsync(blobId, ct);

    public async Task<IAttachExistingFileResult> AttachExistingFile(Guid peerId, Guid sourceFileId, string? fileName, CancellationToken ct = default)
    {
        var result = await this.GetGrain<IUserChatGrain>(Guid.CreateVersion7()).AttachExistingFileAsync(peerId, sourceFileId, fileName, ct);

        return result.IsSuccess
            ? new SuccessAttachExistingFile(result.Value)
            : new FailedAttachExistingFile(result.Error);
    }

    public async Task<IPrepareUploadResult> PrepareUploadAttachment(Guid peerId, IonBytes sha256, long size, string contentType, string fileName,
        CancellationToken ct = default)
    {
        var result = await this.GetGrain<IUserChatGrain>(Guid.CreateVersion7())
           .PrepareUploadAttachmentAsync(peerId, sha256.ToArray(), size, contentType, fileName, ct);

        return PrepareUploads.ToResult(result);
    }

    public Task<IVideoUploadResult> PrepareVideoUpload(Guid peerId, VideoUploadDeclaration declaration, CancellationToken ct = default)
        => this.GetGrain<IUserChatGrain>(Guid.CreateVersion7()).PrepareVideoUploadAsync(this.GetUserId(), peerId, declaration, ct);

    public Task<IVideoUploadResult> CompleteVideoUpload(Guid peerId, Guid ticketId, IonArray<UploadedPart> parts, CancellationToken ct = default)
        => this.GetGrain<IUserChatGrain>(Guid.CreateVersion7())
           .CompleteVideoUploadAsync(this.GetUserId(), peerId, ticketId, parts.Values?.ToArray() ?? [], ct);

    public Task AbortVideoUpload(Guid peerId, Guid ticketId, CancellationToken ct = default)
        => this.GetGrain<IUserChatGrain>(Guid.CreateVersion7()).AbortVideoUploadAsync(this.GetUserId(), peerId, ticketId, ct);

    public Task<UploadLimits> GetUploadLimits(Guid peerId, CancellationToken ct = default)
        => this.GetGrain<IUserChatGrain>(Guid.CreateVersion7()).GetUploadLimitsAsync(this.GetUserId(), peerId, ct);

    public async Task<long> SendDirectMessage(Guid receiverId, string text, IonArray<IMessageEntity> entities, long randomId, long? replyTo, CancellationToken ct = default)
        => await this.GetGrain<IUserChatGrain>(Guid.CreateVersion7()).SendDirectMessageAsync(receiverId, text, entities.Values.ToList(), randomId, replyTo, ct);

    public async Task<IonArray<DirectMessage>> QueryDirectMessages(Guid peerId, long? from, int limit, CancellationToken ct = default)
        => (await this.GetGrain<IUserChatGrain>(Guid.CreateVersion7()).QueryDirectMessagesAsync(peerId, from, limit, ct));
}