namespace Argon.Features.BotApi;

using Argon.Features.NatsStreaming;
using Argon.Services.Ion;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;

/// <summary>
/// Maps incoming <see cref="IArgonEvent"/> instances to typed Bot API payloads
/// and publishes them as <see cref="BotSseEvent"/> to per-space NATS JetStream subjects.
/// </summary>
public sealed class BotEventPublisher(
    INatsJSContext              js,
    BotSseEventSerializer      serializer,
    BotUserCache                userCache,
    InteractionContextStore     interactionStore,
    IGrainFactory               grainFactory,
    ILogger<BotEventPublisher>  logger)
{
    public InteractionContextStore InteractionStore => interactionStore;

    private readonly ConcurrentDictionary<Guid, bool> _ensuredStreams = new();

    /// <summary>
    /// Maps an <see cref="IArgonEvent"/> to a Bot API event and publishes to NATS.
    /// Events that don't map to a bot event type are silently ignored.
    /// Never throws — bot events must not break the main SignalR pipeline.
    /// </summary>
    public async ValueTask PublishIfMappedAsync<T>(T @event, Guid spaceId) where T : IArgonEvent
    {
        try
        {
            switch (@event)
            {
                case MessageSent e:
                {
                    var msg = await BotEventMapper.FromArgonMessageAsync(e.message, userCache);
                    await PublishAsync(spaceId, BotEventType.MessageCreate,
                        new MessageCreateEvent(e.spaceId, e.message.channelId, msg), e.message.channelId);
                    break;
                }

                case MessageEdited e:
                {
                    await PublishAsync(spaceId, BotEventType.MessageEdit,
                        new MessageEditEvent(e.spaceId, e.channelId, e.messageId, e.text, e.updatedAt), e.channelId);
                    break;
                }

                case JoinToServerUser e:
                {
                    var user = await userCache.GetOrResolveAsync(e.userId);
                    await PublishAsync(spaceId, BotEventType.MemberJoin,
                        new MemberJoinEvent(e.spaceId, user));
                    break;
                }

                case UserUpdated e:
                {
                    userCache.Invalidate(e.dto.userId);
                    var user = userCache.FromArgonUser(e.dto);
                    await PublishAsync(spaceId, BotEventType.MemberUpdate,
                        new MemberUpdateEvent(e.spaceId, user));
                    break;
                }

                case ChannelCreated e:
                {
                    var channel = BotEventMapper.FromArgonChannel(e.data);
                    await PublishAsync(spaceId, BotEventType.ChannelCreate,
                        new ChannelCreateEvent(e.spaceId, channel));
                    break;
                }

                case ChannelRemoved e:
                {
                    await PublishAsync(spaceId, BotEventType.ChannelDelete,
                        new ChannelDeleteEvent(e.spaceId, e.channelId));
                    break;
                }

                case UserChangedStatus e:
                {
                    var user = await userCache.GetOrResolveAsync(e.userId);
                    var presence = new BotPresenceV1(BotEventMapper.FromUserStatus(e.status), null);
                    await PublishAsync(spaceId, BotEventType.PresenceUpdate,
                        new PresenceUpdateEvent(e.spaceId, user, presence));
                    break;
                }

                case OnUserPresenceActivityChanged e:
                {
                    var user = await userCache.GetOrResolveAsync(e.userId);
                    var activity = BotEventMapper.FromActivityPresence(e.presence);
                    var presence = new BotPresenceV1(BotUserStatus.Online, activity);
                    await PublishAsync(spaceId, BotEventType.PresenceUpdate,
                        new PresenceUpdateEvent(e.spaceId, user, presence));
                    break;
                }

                case OnUserPresenceActivityRemoved e:
                {
                    var user = await userCache.GetOrResolveAsync(e.userId);
                    var presence = new BotPresenceV1(BotUserStatus.Online, null);
                    await PublishAsync(spaceId, BotEventType.PresenceUpdate,
                        new PresenceUpdateEvent(e.spaceId, user, presence));
                    break;
                }

                case JoinedToChannelUser e:
                {
                    var user = await userCache.GetOrResolveAsync(e.userId);
                    await PublishAsync(spaceId, BotEventType.VoiceJoin,
                        new VoiceJoinEvent(e.spaceId, e.channelId, user), e.channelId);
                    break;
                }

                case LeavedFromChannelUser e:
                {
                    var user = await userCache.GetOrResolveAsync(e.userId);
                    await PublishAsync(spaceId, BotEventType.VoiceLeave,
                        new VoiceLeaveEvent(e.spaceId, e.channelId, user), e.channelId);
                    break;
                }

                case LeavedFromServerUser e:
                {
                    await PublishAsync(spaceId, BotEventType.MemberLeave,
                        new MemberLeaveEvent(e.spaceId, e.userId));
                    break;
                }

                case UserTypingEvent e:
                {
                    await PublishAsync(spaceId, BotEventType.TypingStart,
                        new TypingStartEvent(e.spaceId, e.channelId, e.userId, e.kind?.ToString()),
                        e.channelId);
                    break;
                }

                case UserStopTypingEvent e:
                {
                    await PublishAsync(spaceId, BotEventType.TypingStop,
                        new TypingStopEvent(e.spaceId, e.channelId, e.userId),
                        e.channelId);
                    break;
                }

                case ArchetypeCreated e:
                {
                    var dto = BotEventMapper.FromArchetype(e.data);
                    await PublishAsync(spaceId, BotEventType.ArchetypeCreate,
                        new ArchetypeCreateEvent(e.spaceId, dto));
                    break;
                }

                case ArchetypeChanged e:
                {
                    var dto = BotEventMapper.FromArchetype(e.data);
                    await PublishAsync(spaceId, BotEventType.ArchetypeUpdate,
                        new ArchetypeUpdateEvent(e.spaceId, dto));
                    break;
                }

                case ReactionAdded e:
                {
                    await PublishAsync(spaceId, BotEventType.ReactionAdd,
                        new ReactionAddEvent(e.spaceId, e.channelId, e.messageId, e.userId, e.emoji),
                        e.channelId);
                    break;
                }

                case ReactionRemoved e:
                {
                    await PublishAsync(spaceId, BotEventType.ReactionRemove,
                        new ReactionRemoveEvent(e.spaceId, e.channelId, e.messageId, e.userId, e.emoji),
                        e.channelId);
                    break;
                }

                // CommandInteraction — dispatched via InvokeSlashCommand, see PublishCommandInteractionAsync
            }
        }
        catch (Exception ex)
        {
            BotApiInstrument.EventPublishErrors.Add(1,
                new KeyValuePair<string, object?>("event_type", @event.GetType().Name));
            logger.LogWarning(ex, "Failed to publish bot event for {EventType} in space {SpaceId}",
                @event.GetType().Name, spaceId);
        }
    }

    /// <summary>
    /// Publishes a CommandInteraction event to the bot's space NATS subject.
    /// Called from ChannelGrain when a user invokes a slash command.
    /// </summary>
    public async ValueTask PublishCommandInteractionAsync(
        Guid interactionId, Guid spaceId, Guid channelId, Guid commandId, string commandName,
        BotUserV1 user, List<BotCommandOptionValueV1> options,
        Guid invokingUserId, Guid botAppId)
    {
        try
        {
            var voiceState = await ResolveVoiceStateAsync(spaceId, user.UserId);
            var payload = new CommandInteractionEvent(interactionId, spaceId, channelId, commandId, commandName, user, options, voiceState);
            await PublishAsync(spaceId, BotEventType.CommandInteraction, payload, channelId);
            interactionStore.Register(interactionId, invokingUserId, channelId, spaceId, botAppId);
        }
        catch (Exception ex)
        {
            BotApiInstrument.EventPublishErrors.Add(1,
                new KeyValuePair<string, object?>("event_type", nameof(BotEventType.CommandInteraction)));
            logger.LogWarning(ex, "Failed to publish CommandInteraction in space {SpaceId}", spaceId);
        }
    }

    /// <summary>
    /// Publishes a ControlInteraction event when a user clicks a button.
    /// Called from ChannelGrain when a user interacts with a button on a message.
    /// </summary>
    public async ValueTask PublishControlInteractionAsync(
        Guid interactionId, ControlType controlType, long messageId,
        Guid channelId, Guid spaceId, BotUserV1 user, string controlId,
        Guid invokingUserId, Guid botAppId)
    {
        try
        {
            var voiceState = await ResolveVoiceStateAsync(spaceId, user.UserId);
            var payload = new ControlInteractionEvent(interactionId, controlType, messageId, channelId, spaceId, user, controlId, voiceState);
            await PublishAsync(spaceId, BotEventType.ControlInteraction, payload, channelId);
            interactionStore.Register(interactionId, invokingUserId, channelId, spaceId, botAppId);
        }
        catch (Exception ex)
        {
            BotApiInstrument.EventPublishErrors.Add(1,
                new KeyValuePair<string, object?>("event_type", nameof(BotEventType.ControlInteraction)));
            logger.LogWarning(ex, "Failed to publish ControlInteraction in space {SpaceId}", spaceId);
        }
    }

    /// <summary>
    /// Publishes a SelectInteraction event when a user submits a select menu.
    /// </summary>
    public async ValueTask PublishSelectInteractionAsync(
        Guid interactionId, ControlType controlType, string customId, long messageId,
        Guid channelId, Guid spaceId, BotUserV1 user, List<string> values,
        Guid invokingUserId, Guid botAppId)
    {
        try
        {
            var voiceState = await ResolveVoiceStateAsync(spaceId, user.UserId);
            var payload = new SelectInteractionEvent(interactionId, controlType, customId, messageId, channelId, spaceId, user, values, voiceState);
            await PublishAsync(spaceId, BotEventType.SelectInteraction, payload, channelId);
            interactionStore.Register(interactionId, invokingUserId, channelId, spaceId, botAppId);
        }
        catch (Exception ex)
        {
            BotApiInstrument.EventPublishErrors.Add(1,
                new KeyValuePair<string, object?>("event_type", nameof(BotEventType.SelectInteraction)));
            logger.LogWarning(ex, "Failed to publish SelectInteraction in space {SpaceId}", spaceId);
        }
    }

    /// <summary>
    /// Publishes a ModalSubmit event when a user submits a modal form.
    /// </summary>
    public async ValueTask PublishModalSubmitAsync(
        Guid interactionId, string customId, Guid channelId, Guid spaceId,
        BotUserV1 user, List<ModalSubmitValueV1> values)
    {
        try
        {
            var voiceState = await ResolveVoiceStateAsync(spaceId, user.UserId);
            var payload = new ModalSubmitEvent(interactionId, customId, channelId, spaceId, user, values, voiceState);
            await PublishAsync(spaceId, BotEventType.ModalSubmit, payload, channelId);
        }
        catch (Exception ex)
        {
            BotApiInstrument.EventPublishErrors.Add(1,
                new KeyValuePair<string, object?>("event_type", nameof(BotEventType.ModalSubmit)));
            logger.LogWarning(ex, "Failed to publish ModalSubmit in space {SpaceId}", spaceId);
        }
    }

    private async ValueTask<BotVoiceStateV1?> ResolveVoiceStateAsync(Guid spaceId, Guid userId)
    {
        try
        {
            var slot = await grainFactory.GetGrain<ISpaceGrain>(spaceId).GetUserVoiceSlotAsync(userId);
            return slot is null ? null : new BotVoiceStateV1(slot.ChannelId, slot.JoinedAt, ChannelMemberState.NONE);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to resolve voice state for user {UserId} in space {SpaceId}", userId, spaceId);
            return null;
        }
    }

    private async ValueTask PublishAsync(Guid spaceId, BotEventType type, object payload, Guid? channelId = null)
    {
        var sw = Stopwatch.StartNew();

        await EnsureStreamAsync(spaceId);

        var subject = NatsStreamExtensions.ToBotEventSubject(spaceId);
        var evt = new BotSseEvent
        {
            Id        = "pending",
            Type      = type,
            SpaceId   = spaceId,
            ChannelId = channelId,
            Data      = payload
        };

        await js.PublishAsync(subject, evt, serializer: serializer);

        sw.Stop();
        var tag = new KeyValuePair<string, object?>("event_type", type.ToString());
        BotApiInstrument.EventsPublished.Add(1, tag);
        BotApiInstrument.EventPublishDuration.Record(sw.Elapsed.TotalMilliseconds, tag);
    }

    /// <summary>
    /// Publishes a per-user bot event (calls, DMs) to the bot's direct NATS subject.
    /// Called from <see cref="AppHubServer.ForUser{T}"/> for events like CallIncoming/CallFinished.
    /// </summary>
    public async ValueTask PublishForUserAsync<T>(T @event, Guid userId) where T : IArgonEvent
    {
        try
        {
            switch (@event)
            {
                case CallIncoming e:
                {
                    await PublishDirectAsync(userId, BotEventType.CallIncoming,
                        new CallIncomingEvent(e.callId, e.fromId));
                    break;
                }

                case CallFinished e:
                {
                    await PublishDirectAsync(userId, BotEventType.CallEnded,
                        new CallEndedEvent(e.callId));
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish direct bot event for {EventType} to user {UserId}",
                @event.GetType().Name, userId);
        }
    }

    /// <summary>
    /// Publishes a bot lifecycle event (install/uninstall) directly to the bot's NATS subject.
    /// Called from SpaceGrain after a bot is installed or uninstalled.
    /// </summary>
    public async ValueTask PublishBotLifecycleAsync(Guid botUserId, BotEventType type, object payload)
    {
        try
        {
            await PublishDirectAsync(botUserId, type, payload);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish bot lifecycle event {EventType} to bot {BotUserId}",
                type, botUserId);
        }
    }

    private async ValueTask PublishDirectAsync(Guid botUserId, BotEventType type, object payload)
    {
        await EnsureDirectStreamAsync(botUserId);

        var subject = NatsStreamExtensions.ToBotDirectSubject(botUserId);
        var evt = new BotSseEvent
        {
            Id        = "pending",
            Type      = type,
            Data      = payload
        };

        await js.PublishAsync(subject, evt, serializer: serializer);
    }

    private async ValueTask EnsureDirectStreamAsync(Guid botUserId)
    {
        if (!_ensuredStreams.TryAdd(botUserId, true))
            return;

        var streamName = NatsStreamExtensions.ToBotDirectSubject(botUserId);
        try
        {
            await js.CreateOrUpdateStreamAsync(new StreamConfig(streamName, [streamName])
            {
                DuplicateWindow = TimeSpan.Zero,
                MaxAge          = TimeSpan.FromMinutes(5),
                AllowDirect     = true,
                MaxBytes        = -1,
                MaxMsgs         = 1000,
                Retention       = StreamConfigRetention.Limits,
                Storage         = StreamConfigStorage.Memory,
                Discard         = StreamConfigDiscard.Old
            });
        }
        catch
        {
            _ensuredStreams.TryRemove(botUserId, out _);
        }
    }

    private async ValueTask EnsureStreamAsync(Guid spaceId)
    {
        if (!_ensuredStreams.TryAdd(spaceId, true))
            return;

        var streamName = NatsStreamExtensions.ToBotEventSubject(spaceId);
        try
        {
            await js.CreateOrUpdateStreamAsync(new StreamConfig(streamName, [streamName])
            {
                DuplicateWindow = TimeSpan.Zero,
                MaxAge          = TimeSpan.FromMinutes(5),
                AllowDirect     = true,
                MaxBytes        = -1,
                MaxMsgs         = 5000,
                Retention       = StreamConfigRetention.Limits,
                Storage         = StreamConfigStorage.Memory,
                Discard         = StreamConfigDiscard.Old
            });
        }
        catch
        {
            // Stream may already exist; remove from dict so next attempt retries
            _ensuredStreams.TryRemove(spaceId, out _);
        }
    }
}

/// <summary>
/// NATS serializer for <see cref="BotSseEvent"/>.
/// Uses Newtonsoft.Json with camelCase and the Bot SSE contract resolver.
/// </summary>
public sealed class BotSseEventSerializer : INatsSerializer<BotSseEvent>
{
    private static readonly JsonSerializerSettings Settings = new()
    {
        Formatting       = Formatting.None,
        ContractResolver = new BotSseContractResolver(),
        Converters       = { new IonArrayConverter(), new IonMaybeConverter() }
    };

    public void Serialize(IBufferWriter<byte> bufferWriter, BotSseEvent value)
    {
        var json      = JsonConvert.SerializeObject(value, Settings);
        var byteCount = Encoding.UTF8.GetByteCount(json);
        var span      = bufferWriter.GetSpan(byteCount);
        Encoding.UTF8.GetBytes(json, span);
        bufferWriter.Advance(byteCount);
    }

    public BotSseEvent? Deserialize(in ReadOnlySequence<byte> buffer)
    {
        if (buffer.IsSingleSegment)
        {
            var json = Encoding.UTF8.GetString(buffer.FirstSpan);
            return JsonConvert.DeserializeObject<BotSseEvent>(json, Settings);
        }

        using var ms = new MemoryStream((int)buffer.Length);
        foreach (var segment in buffer)
            ms.Write(segment.Span);

        ms.Position = 0;
        using var reader     = new StreamReader(ms, Encoding.UTF8);
        using var jsonReader = new JsonTextReader(reader);
        return JsonSerializer.CreateDefault(Settings).Deserialize<BotSseEvent>(jsonReader);
    }

    public INatsSerializer<BotSseEvent> CombineWith(INatsSerializer<BotSseEvent> next) => this;
}
