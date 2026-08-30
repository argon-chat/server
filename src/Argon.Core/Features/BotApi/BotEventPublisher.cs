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
    UserLocaleRegistry          localeRegistry,
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
                    var user = await userCache.FromArgonUser(e.dto);
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

        try
        {
            await js.PublishAsync(subject, evt, serializer: serializer, opts: PublishRetry);
        }
        catch (NatsJSPublishNoResponseException)
        {
            // ONE EXCEPTION, TWO CAUSES, AND THEY NEED OPPOSITE REPAIRS.
            //
            // "No response received from the server" is raised both when nothing is bound to the
            // subject — so no stream can acknowledge — and when a stream is there but the reply did
            // not arrive in time. The message is the same either way, which is why a CI run could
            // fail eighty-one publishes without saying which it was.
            //
            // Two facts separate them: whether the stream answers now, and how long the publish
            // waited. Absent, near-instantly, means the stream is not there whatever
            // CreateOrUpdateStream reported. Present, after the client's request timeout, means it
            // is there and the acknowledgement did not come back — a different problem entirely,
            // and not one to be fixed by declaring the stream harder.
            //
            // Both are read on a path that has already failed, so this costs nothing on a healthy
            // publish; the original exception is rethrown untouched for the caller to handle.
            var present = await StreamRespondsAsync(subject);

            logger.LogWarning(
                "JetStream did not acknowledge a publish to {Subject} after {ElapsedMs}ms. The stream "
              + "{Presence} when asked immediately afterwards, so this is {Diagnosis}",
                subject, sw.ElapsedMilliseconds,
                present ? "answered" : "did not answer",
                present ? "an acknowledgement that did not arrive in time, not a missing stream"
                        : "a subject with no stream bound to it");

            throw;
        }

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
                        new CallIncomingEvent(e.callId, e.fromId, await localeRegistry.Get(e.fromId)));
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

        await js.PublishAsync(subject, evt, serializer: serializer, opts: PublishRetry);
    }

    /// <summary>
    /// Whether JetStream will own up to a stream on this subject, asked only after a publish to it
    /// has already failed.
    /// </summary>
    /// <remarks>
    /// Deliberately swallows everything and answers false. It is a diagnostic on a path that is
    /// already broken, and an exception raised while explaining a failure would replace the
    /// explanation with a second, less useful one.
    /// </remarks>
    private async ValueTask<bool> StreamRespondsAsync(string streamName)
    {
        try
        {
            await js.GetStreamAsync(streamName);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Publish options with enough retry budget to outlast a stream that exists but is not yet
    /// reachable on its subject.
    /// </summary>
    /// <remarks>
    /// <para><b>Measured, not guessed.</b> A CI run failed 101 publishes, and the diagnostic on that
    /// path reported the same thing every time: the stream ANSWERED when asked immediately
    /// afterwards, and the publish had given up after 250ms — median 252, tightest spread from 250
    /// to 341. That is not a network timeout, which would be seconds and scattered. It is a
    /// schedule: the client's own retry.</para>
    ///
    /// <para>The library retries only on <c>NoResponders</c> — the server saying nothing is bound to
    /// that subject — and by default makes two attempts 250ms apart. So the sequence was: publish,
    /// no responders, wait 250ms, publish, no responders, give up. Meanwhile the stream was already
    /// there. What had not happened yet was the subject interest reaching the publishing connection,
    /// which is a gap that opens right after <c>CreateOrUpdateStream</c> and closes on its own.</para>
    ///
    /// <para>So the budget is widened rather than the stream declared harder. Five attempts 200ms
    /// apart is about eight hundred milliseconds of patience, against a gap measured in hundreds.
    /// It costs a healthy publish nothing at all: these retries fire only on <c>NoResponders</c>,
    /// and a stream that is reachable never produces one.</para>
    ///
    /// <para>The events this drops are not free. A bot's <c>messageCreate</c> that never arrives is
    /// a message its author saw sent, and the only test that noticed was the one that waited forty
    /// seconds for a single event and called it a timeout.</para>
    /// </remarks>
    private static readonly NatsJSPubOpts PublishRetry = new()
    {
        RetryAttempts            = 5,
        RetryWaitBetweenAttempts = TimeSpan.FromMilliseconds(200)
    };

    private ValueTask EnsureDirectStreamAsync(Guid botUserId)
        => EnsureStreamAsync(botUserId, NatsStreamExtensions.ToBotDirectSubject(botUserId), maxMsgs: 1000);

    private ValueTask EnsureStreamAsync(Guid spaceId)
        => EnsureStreamAsync(spaceId, NatsStreamExtensions.ToBotEventSubject(spaceId), maxMsgs: 5000);

    /// <summary>
    /// Declares the stream a subject needs, once per key for the life of this publisher.
    /// </summary>
    /// <remarks>
    /// <para><b>The failure is logged now.</b> It used to be swallowed by a bare <c>catch</c> whose
    /// comment read "stream may already exist" — which is not a thing that happens:
    /// <c>CreateOrUpdateStreamAsync</c> is idempotent, that being the point of the name. So the
    /// excuse covered every real reason instead: JetStream not enabled, no storage left, the account
    /// out of streams, the server unreachable.</para>
    ///
    /// <para>What made it worse is where the consequence surfaced. The caller publishes regardless,
    /// and a publish to a subject no stream is bound to gets no acknowledgement — so the operator
    /// saw <c>NatsJSPublishNoResponseException: No response received from the server</c> against
    /// their event, one layer away from the thing that actually failed, with the reason discarded.
    /// That is what this log is for; the publish that follows will still fail, and now it can be
    /// explained.</para>
    ///
    /// <para>Removing the key on failure is kept: it is what lets the next event try again rather
    /// than remembering a stream that was never created.</para>
    /// </remarks>
    private async ValueTask EnsureStreamAsync(Guid key, string streamName, int maxMsgs)
    {
        if (!_ensuredStreams.TryAdd(key, true))
            return;

        try
        {
            await js.CreateOrUpdateStreamAsync(new StreamConfig(streamName, [streamName])
            {
                DuplicateWindow = TimeSpan.Zero,
                MaxAge          = TimeSpan.FromMinutes(5),
                AllowDirect     = true,
                MaxBytes        = -1,
                MaxMsgs         = maxMsgs,
                Retention       = StreamConfigRetention.Limits,
                Storage         = StreamConfigStorage.Memory,
                Discard         = StreamConfigDiscard.Old
            });
        }
        catch (Exception ex)
        {
            _ensuredStreams.TryRemove(key, out _);

            logger.LogWarning(ex,
                "Failed to declare NATS stream {StreamName}; the publish that follows will not be " +
                "acknowledged, because nothing is bound to that subject", streamName);
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
