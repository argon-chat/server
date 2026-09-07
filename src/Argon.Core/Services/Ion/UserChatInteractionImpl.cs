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

    public async Task<long> SendDirectMessage(Guid receiverId, string text, IonArray<IMessageEntity> entities, long randomId, long? replyTo, CancellationToken ct = default)
        => await this.GetGrain<IUserChatGrain>(Guid.CreateVersion7()).SendDirectMessageAsync(receiverId, text, entities.Values.ToList(), randomId, replyTo, ct);

    public async Task<IonArray<DirectMessage>> QueryDirectMessages(Guid peerId, long? from, int limit, CancellationToken ct = default)
        => (await this.GetGrain<IUserChatGrain>(Guid.CreateVersion7()).QueryDirectMessagesAsync(peerId, from, limit, ct));
}