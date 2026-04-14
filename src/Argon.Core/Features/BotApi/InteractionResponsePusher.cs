namespace Argon.Features.BotApi;

using Argon.Core.Features.Transport;

/// <summary>
/// Pushes interaction responses to users via SignalR using CBOR (consistent with all other events).
/// Uses <see cref="AppHubServer.ForUser{T}"/> which serializes via IonFormatterStorage.
/// </summary>
public sealed class InteractionResponsePusher(AppHubServer appHubServer)
{
    public Task PushAckAsync(Guid interactionId, Guid userId)
        => appHubServer.ForUser(new InteractionAcked(interactionId), userId);

    public Task PushDeferredAsync(Guid interactionId, Guid userId)
        => appHubServer.ForUser(new InteractionDeferred(interactionId), userId);

    public Task PushShowModalAsync(Guid interactionId, Guid userId, ModalDefinitionV1 modal)
        => appHubServer.ForUser(new ShowModal(interactionId, MapModal(modal)), userId);

    private static IonModalDefinition MapModal(ModalDefinitionV1 src) => new(
        src.CustomId,
        src.Title,
        src.Components.Select(MapComponent).ToList());

    private static IonModalComponent MapComponent(ModalComponentV1 c) => new(
        (IonModalComponentType)(int)c.Type,
        c.CustomId,
        c.Label,
        c.Style is not null ? (IonTextInputStyle)(int)c.Style : null,
        c.Placeholder,
        c.MinLength,
        c.MaxLength,
        c.Required,
        c.Value,
        c.Options?.Select(o => new IonModalSelectOption(o.Label, o.Value, o.Description, null)).ToArray(),
        c.MinValues,
        c.MaxValues,
        c.Default,
        c.Description);
}
