namespace ArgonComplexTest.Tests;

using System.Net;
using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;
using ion.runtime;
using Newtonsoft.Json.Linq;
using static ChannelTestKit;

/// <summary>
/// Everything a member does to a bot through a channel — slash commands, buttons, selects and modals —
/// and what a bot does back: editing its own messages and showing that it is typing.
/// </summary>
/// <remarks>
/// <para>The bot is seeded into the database and joined as an ordinary member, the way
/// <c>BotApiTests</c> does it, and its side is driven over the real bot HTTP API and its event
/// stream. The member's side is the Ion surface the desktop client calls.</para>
///
/// <para>Each refusal here guards a door someone would otherwise walk through: a click on a control the
/// member was never meant to have, a submission of a modal that was shown to somebody else, a bot
/// editing a message that is not its own.</para>
/// </remarks>
[TestFixture]
public class ChannelBotInteractionTests : TestBase
{
    private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(20);

    private TestUserSession owner   = null!;
    private TestUserSession guest   = null!;
    private Guid            spaceId;
    private Guid            channelId;
    private ChannelTestBot  bot     = null!;

    [OneTimeSetUp]
    public async Task SetUpSpaceWithBot()
    {
        owner     = await CreateSessionAsync();
        guest     = await CreateSessionAsync();
        spaceId   = await CreateSpaceAsync(owner, default);
        channelId = await CreateChannelAsync(owner, spaceId, "bot-room", ChannelType.Text, default);
        await JoinAsync(owner, guest, spaceId, default);

        bot = await ChannelTestBot.SeedAsync(owner.UserId);
        await bot.JoinAsync(spaceId);
    }

    private static object Button(string id, string label = "Go", bool? disabled = null, Guid? requiredArchetypeId = null)
        => new { type = (int)ControlType.Button, variant = (int)ButtonVariant.Callback, label, id, disabled, requiredArchetypeId };

    private static object StringSelect(string customId, int? minValues, int? maxValues, bool? disabled = null, Guid? requiredArchetypeId = null,
        params string[] values)
        => new
        {
            type    = (int)ControlType.StringSelect,
            customId,
            minValues,
            maxValues,
            disabled,
            requiredArchetypeId,
            options = values.Select(v => new { label = v.ToUpperInvariant(), value = v }).ToArray()
        };

    private static object UserSelect(string customId)
        => new { type = (int)ControlType.UserSelect, customId, maxValues = 2 };

    private static object[] Rows(params object[][] rows) => rows.Select(r => (object)new { controls = r }).ToArray();

    // ── Slash commands ──────────────────────────────────────────────────────────────────────────

    private async Task<Guid> RegisterCommandAsync(ChannelTestBot owningBot, string name, object[]? options = null, bool global = false)
    {
        var registered = await owningBot.CallOkAsync(HttpMethod.Post, "/ICommands/v1/Register",
            new { name, description = "a test command", spaceId = global ? (Guid?)null : spaceId, options });
        return Guid.Parse(registered.GetProperty("commandId").GetString()!);
    }

    [Test, CancelAfter(120_000)]
    public async Task A_slash_command_reaches_the_bot_with_its_options_typed_by_the_schema(CancellationToken ct = default)
    {
        var commandId = await RegisterCommandAsync(bot, "roll", [
            new { name = "count", description = "how many", type = 1 },
            new { name = "ratio", description = "a number", type = 6 },
            new { name = "loud", description = "a flag", type = 2 },
            new { name = "label", description = "a string", type = 0 },
            new { name = "lenient", description = "unparsable int", type = 1 }
        ]);

        await using var events = await bot.OpenEventsAsync(ct);

        var result = await guest.Channels.InvokeSlashCommand(spaceId, channelId, commandId, new IonArray<SlashCommandOption>([
            new SlashCommandOption("count", "3"),
            new SlashCommandOption("ratio", "0.25"),
            new SlashCommandOption("loud", "true"),
            new SlashCommandOption("label", "dice"),
            new SlashCommandOption("lenient", "many"),
            new SlashCommandOption("not-in-the-schema", "dropped")
        ]), ct);

        Assert.That(result, Is.InstanceOf<SuccessInvokeSlashCommand>(), $"refused: {(result as FailedInvokeSlashCommand)?.error}");

        var frame   = await events.WaitForAsync("commandInteraction", d => (string?)d["commandId"] == commandId.ToString(), EventWait, ct);
        var options = ((JArray)frame["options"]!).ToDictionary(o => (string)o["name"]!, o => o["value"]!);

        Assert.Multiple(() =>
        {
            Assert.That((string?)frame["commandName"], Is.EqualTo("roll"));
            Assert.That(options["count"].Type, Is.EqualTo(JTokenType.Integer));
            Assert.That((long)options["count"], Is.EqualTo(3));
            Assert.That((double)options["ratio"], Is.EqualTo(0.25));
            Assert.That(options["loud"].Type, Is.EqualTo(JTokenType.Boolean));
            Assert.That((string?)options["label"], Is.EqualTo("dice"));
            Assert.That((string?)options["lenient"], Is.EqualTo("many"), "a value that does not parse goes through as written");
            Assert.That(options.ContainsKey("not-in-the-schema"), Is.False, "an option the command never declared reached the bot");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_command_that_is_unknown_or_whose_bot_is_not_in_the_space_is_not_found(CancellationToken ct = default)
    {
        // Global, so it matches every space by scope and only the membership check can keep it out.
        var absentBot = await ChannelTestBot.SeedAsync(owner.UserId, ct);
        var foreign   = await RegisterCommandAsync(absentBot, "elsewhere", global: true);

        var unknown  = await guest.Channels.InvokeSlashCommand(spaceId, channelId, Guid.NewGuid(), new IonArray<SlashCommandOption>([]), ct);
        var notInHere = await guest.Channels.InvokeSlashCommand(spaceId, channelId, foreign, new IonArray<SlashCommandOption>([]), ct);

        Assert.Multiple(() =>
        {
            Assert.That((unknown as FailedInvokeSlashCommand)?.error, Is.EqualTo(InvokeSlashCommandError.COMMAND_NOT_FOUND));
            Assert.That((notInHere as FailedInvokeSlashCommand)?.error, Is.EqualTo(InvokeSlashCommandError.COMMAND_NOT_FOUND),
                "a command reached a bot that is not a member of the space");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Without_UseCommands_a_member_cannot_invoke_a_command(CancellationToken ct = default)
    {
        var room      = await CreateChannelAsync(owner, spaceId, "no-commands", ChannelType.Text, ct);
        var commandId = await RegisterCommandAsync(bot, "gated");

        await DenyOnChannelAsync(owner, spaceId, room, ArgonEntitlement.UseCommands, ct);

        var result = await guest.Channels.InvokeSlashCommand(spaceId, room, commandId, new IonArray<SlashCommandOption>([]), ct);

        Assert.That((result as FailedInvokeSlashCommand)?.error, Is.EqualTo(InvokeSlashCommandError.INSUFFICIENT_PERMISSIONS));
    }

    // ── Buttons ─────────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task A_button_click_reaches_the_bot_under_the_interaction_id_the_member_is_given(CancellationToken ct = default)
    {
        var messageId = await bot.SendAsync(spaceId, channelId, "press it", Rows([Button("go")]), ct);

        await using var events = await bot.OpenEventsAsync(ct);

        var result = await guest.Channels.InteractWithControl(spaceId, channelId, messageId, "go", ct);

        Assert.That(result, Is.InstanceOf<SuccessInteractWithControl>(), $"refused: {(result as FailedInteractWithControl)?.error}");

        var interactionId = ((SuccessInteractWithControl)result).interactionId;
        var frame = await events.WaitForAsync("controlInteraction", d => (string?)d["interactionId"] == interactionId.ToString(), EventWait, ct);

        Assert.Multiple(() =>
        {
            Assert.That((string?)frame["controlId"], Is.EqualTo("go"));
            Assert.That((long?)frame["messageId"], Is.EqualTo(messageId));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_click_on_a_missing_message_an_unknown_control_or_a_disabled_one_is_refused(CancellationToken ct = default)
    {
        var plain   = await owner.Channels.SendMessage(spaceId, channelId, "no controls", new IonArray<IMessageEntity>([]),
            Random.Shared.NextInt64(1, long.MaxValue), null, ct);
        var withOne = await bot.SendAsync(spaceId, channelId, "one live, one off", Rows([Button("live"), Button("off", "Off", disabled: true)]), ct);

        var missing  = await guest.Channels.InteractWithControl(spaceId, channelId, long.MaxValue - 7, "live", ct);
        var none     = await guest.Channels.InteractWithControl(spaceId, channelId, plain, "live", ct);
        var unknown  = await guest.Channels.InteractWithControl(spaceId, channelId, withOne, "nope", ct);
        var disabled = await guest.Channels.InteractWithControl(spaceId, channelId, withOne, "off", ct);

        Assert.Multiple(() =>
        {
            Assert.That((missing as FailedInteractWithControl)?.error, Is.EqualTo(InteractWithControlError.MESSAGE_NOT_FOUND));
            Assert.That((none as FailedInteractWithControl)?.error, Is.EqualTo(InteractWithControlError.CONTROL_NOT_FOUND));
            Assert.That((unknown as FailedInteractWithControl)?.error, Is.EqualTo(InteractWithControlError.CONTROL_NOT_FOUND));
            Assert.That((disabled as FailedInteractWithControl)?.error, Is.EqualTo(InteractWithControlError.CONTROL_DISABLED));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_role_gated_control_opens_to_the_role_and_to_an_administrator_only(CancellationToken ct = default)
    {
        var member     = await CreateSessionAsync(ct);
        await JoinAsync(owner, member, spaceId, ct);

        var archetypes = ArchetypesOf(owner);
        var staff      = await archetypes.CreateArchetype(spaceId, $"staff-{Guid.NewGuid():N}"[..20], ct);

        var messageId = await bot.SendAsync(spaceId, channelId, "staff only", Rows(
            [Button("staff-button", requiredArchetypeId: staff.id)],
            [StringSelect("staff-select", 1, 1, requiredArchetypeId: staff.id, values: ["a", "b"])]), ct);

        var buttonBefore = await member.Channels.InteractWithControl(spaceId, channelId, messageId, "staff-button", ct);
        var selectBefore = await member.Channels.InteractWithSelect(spaceId, channelId, messageId, "staff-select", new IonArray<string>(["a"]), ct);

        Assert.Multiple(() =>
        {
            Assert.That((buttonBefore as FailedInteractWithControl)?.error, Is.EqualTo(InteractWithControlError.ARCHETYPE_REQUIRED));
            Assert.That((selectBefore as FailedInteractWithSelect)?.error, Is.EqualTo(InteractWithSelectError.ARCHETYPE_REQUIRED));
        });

        // The owner holds no "staff" but administers the space, which is the documented bypass.
        Assert.That(await owner.Channels.InteractWithControl(spaceId, channelId, messageId, "staff-button", ct),
            Is.InstanceOf<SuccessInteractWithControl>(), "an administrator was held back by a role gate");
        Assert.That(await owner.Channels.InteractWithSelect(spaceId, channelId, messageId, "staff-select", new IonArray<string>(["b"]), ct),
            Is.InstanceOf<SuccessInteractWithSelect>());

        Assert.That(await archetypes.SetArchetypeToMember(spaceId, await MemberIdOfAsync(owner, spaceId, member.UserId, ct), staff.id, true, ct),
            Is.True);

        Assert.That(await member.Channels.InteractWithControl(spaceId, channelId, messageId, "staff-button", ct),
            Is.InstanceOf<SuccessInteractWithControl>(), "holding the role did not open the control");
        Assert.That(await member.Channels.InteractWithSelect(spaceId, channelId, messageId, "staff-select", new IonArray<string>(["a"]), ct),
            Is.InstanceOf<SuccessInteractWithSelect>());
    }

    [Test, CancelAfter(120_000)]
    public async Task A_control_on_the_message_of_a_bot_that_has_left_the_space_is_refused(CancellationToken ct = default)
    {
        var leaving = await ChannelTestBot.SeedAsync(owner.UserId, ct);
        await leaving.JoinAsync(spaceId);

        var messageId = await leaving.SendAsync(spaceId, channelId, "last words",
            Rows([Button("bye")], [StringSelect("bye-select", null, null, values: ["x"])]), ct);

        await AsUserAsync(owner.UserId, () => Grains.GetGrain<Argon.Grains.Interfaces.ISpaceGrain>(spaceId).RemoveMemberAsync(leaving.UserId));

        var click  = await guest.Channels.InteractWithControl(spaceId, channelId, messageId, "bye", ct);
        var select = await guest.Channels.InteractWithSelect(spaceId, channelId, messageId, "bye-select", new IonArray<string>(["x"]), ct);

        Assert.Multiple(() =>
        {
            Assert.That((click as FailedInteractWithControl)?.error, Is.EqualTo(InteractWithControlError.BOT_NOT_CONNECTED));
            Assert.That((select as FailedInteractWithSelect)?.error, Is.EqualTo(InteractWithSelectError.BOT_NOT_CONNECTED));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task The_controls_of_a_deleted_message_take_no_more_input(CancellationToken ct = default)
    {
        var messageId = await bot.SendAsync(spaceId, channelId, "about to go",
            Rows([Button("ghost")], [StringSelect("ghost-select", null, null, values: ["x"])]), ct);

        Assert.That(await owner.Channels.DeleteMessage(spaceId, channelId, messageId, ct), Is.InstanceOf<SuccessDeleteMessage>());

        var click  = await guest.Channels.InteractWithControl(spaceId, channelId, messageId, "ghost", ct);
        var select = await guest.Channels.InteractWithSelect(spaceId, channelId, messageId, "ghost-select", new IonArray<string>(["x"]), ct);

        Assert.Multiple(() =>
        {
            Assert.That((click as FailedInteractWithControl)?.error, Is.EqualTo(InteractWithControlError.MESSAGE_NOT_FOUND),
                "a button on a deleted message still reached the bot");
            Assert.That((select as FailedInteractWithSelect)?.error, Is.EqualTo(InteractWithSelectError.MESSAGE_NOT_FOUND),
                "a select on a deleted message still reached the bot");
        });
    }

    // ── Selects ─────────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task A_string_select_takes_only_its_own_options_within_its_bounds(CancellationToken ct = default)
    {
        var messageId = await bot.SendAsync(spaceId, channelId, "pick one or two", Rows([StringSelect("pick", 1, 2, values: ["a", "b", "c"])]), ct);

        await using var events = await bot.OpenEventsAsync(ct);

        async Task<InteractWithSelectError?> TryAsync(params string[] values)
            => (await guest.Channels.InteractWithSelect(spaceId, channelId, messageId, "pick", new IonArray<string>(values), ct)
                   as FailedInteractWithSelect)?.error;

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await TryAsync(), Is.EqualTo(InteractWithSelectError.INVALID_VALUES), "fewer than the minimum");
            Assert.That(await TryAsync("a", "b", "c"), Is.EqualTo(InteractWithSelectError.INVALID_VALUES), "more than the maximum");
            Assert.That(await TryAsync("z"), Is.EqualTo(InteractWithSelectError.INVALID_VALUES), "a value the select never offered");
        });

        var picked = await guest.Channels.InteractWithSelect(spaceId, channelId, messageId, "pick", new IonArray<string>(["a", "c"]), ct);
        Assert.That(picked, Is.InstanceOf<SuccessInteractWithSelect>(), $"refused: {(picked as FailedInteractWithSelect)?.error}");

        var interactionId = ((SuccessInteractWithSelect)picked).interactionId;
        var frame = await events.WaitForAsync("selectInteraction", d => (string?)d["interactionId"] == interactionId.ToString(), EventWait, ct);

        Assert.That(((JArray)frame["values"]!).Select(v => (string)v!), Is.EqualTo(new[] { "a", "c" }));
    }

    [Test, CancelAfter(120_000)]
    public async Task A_select_is_refused_when_missing_unknown_or_disabled_and_a_user_select_takes_any_ids(CancellationToken ct = default)
    {
        var plain     = await owner.Channels.SendMessage(spaceId, channelId, "nothing to pick", new IonArray<IMessageEntity>([]),
            Random.Shared.NextInt64(1, long.MaxValue), null, ct);
        var messageId = await bot.SendAsync(spaceId, channelId, "selects", Rows(
            [StringSelect("closed", null, null, disabled: true, values: ["a"])],
            [UserSelect("who")]), ct);

        var missing  = await guest.Channels.InteractWithSelect(spaceId, channelId, long.MaxValue - 11, "who", new IonArray<string>(["a"]), ct);
        var none     = await guest.Channels.InteractWithSelect(spaceId, channelId, plain, "who", new IonArray<string>(["a"]), ct);
        var unknown  = await guest.Channels.InteractWithSelect(spaceId, channelId, messageId, "nope", new IonArray<string>(["a"]), ct);
        var disabled = await guest.Channels.InteractWithSelect(spaceId, channelId, messageId, "closed", new IonArray<string>(["a"]), ct);
        var users    = await guest.Channels.InteractWithSelect(spaceId, channelId, messageId, "who",
            new IonArray<string>([owner.UserId.ToString(), guest.UserId.ToString()]), ct);

        Assert.Multiple(() =>
        {
            Assert.That((missing as FailedInteractWithSelect)?.error, Is.EqualTo(InteractWithSelectError.MESSAGE_NOT_FOUND));
            Assert.That((none as FailedInteractWithSelect)?.error, Is.EqualTo(InteractWithSelectError.CONTROL_NOT_FOUND));
            Assert.That((unknown as FailedInteractWithSelect)?.error, Is.EqualTo(InteractWithSelectError.CONTROL_NOT_FOUND));
            Assert.That((disabled as FailedInteractWithSelect)?.error, Is.EqualTo(InteractWithSelectError.CONTROL_DISABLED));
            Assert.That(users, Is.InstanceOf<SuccessInteractWithSelect>(), "a user select has no fixed options to check against");
        });
    }

    // ── Modals ──────────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task A_modal_is_submitted_once_by_the_member_it_was_shown_to_and_in_its_own_channel(CancellationToken ct = default)
    {
        var elsewhere = await CreateChannelAsync(owner, spaceId, "modal-elsewhere", ChannelType.Text, ct);
        var messageId = await bot.SendAsync(spaceId, channelId, "feedback?", Rows([Button("open-form")]), ct);

        var click = await guest.Channels.InteractWithControl(spaceId, channelId, messageId, "open-form", ct);
        Assert.That(click, Is.InstanceOf<SuccessInteractWithControl>());

        var shown = await bot.CallOkAsync(HttpMethod.Post, "/IInteractions/v1/Modal", new
        {
            interactionId = ((SuccessInteractWithControl)click).interactionId,
            modal = new
            {
                customId   = "feedback-form",
                title      = "Feedback",
                components = new object[] { new { type = 0, customId = "reason", label = "Reason", style = 0 } }
            }
        }, ct);

        var modalId = Guid.Parse(shown.GetProperty("modalInteractionId").GetString()!);
        var values  = new IonArray<ModalSubmitValue>([new ModalSubmitValue("reason", "because")]);

        await using var events = await bot.OpenEventsAsync(ct);

        var byOwner     = await owner.Channels.SubmitModal(spaceId, channelId, modalId, values, ct);
        var inOtherRoom = await guest.Channels.SubmitModal(spaceId, elsewhere, modalId, values, ct);
        var byGuest     = await guest.Channels.SubmitModal(spaceId, channelId, modalId, values, ct);
        var again       = await guest.Channels.SubmitModal(spaceId, channelId, modalId, values, ct);

        Assert.Multiple(() =>
        {
            Assert.That((byOwner as FailedSubmitModal)?.error, Is.EqualTo(SubmitModalError.INTERACTION_NOT_FOUND),
                "a modal shown to one member was accepted from another");
            Assert.That((inOtherRoom as FailedSubmitModal)?.error, Is.EqualTo(SubmitModalError.INTERACTION_NOT_FOUND),
                "a modal was accepted through a channel it was not opened in");
            Assert.That(byGuest, Is.InstanceOf<SuccessSubmitModal>(),
                $"the member the modal was shown to could not submit it: {(byGuest as FailedSubmitModal)?.error}");
            Assert.That((again as FailedSubmitModal)?.error, Is.EqualTo(SubmitModalError.INTERACTION_EXPIRED), "a modal was submitted twice");
        });

        var frame = await events.WaitForAsync("modalSubmit", d => (string?)d["customId"] == modalId.ToString(), EventWait, ct);
        var field = ((JArray)frame["values"]!).Single();

        Assert.Multiple(() =>
        {
            Assert.That((string?)field["customId"], Is.EqualTo("reason"));
            Assert.That(((JArray)field["values"]!).Select(v => (string)v!), Is.EqualTo(new[] { "because" }));
        });
    }

    // ── Bot edits ───────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task A_bot_edits_its_own_message_and_the_channel_is_told(CancellationToken ct = default)
    {
        var messageId = await bot.SendAsync(spaceId, channelId, "v1", Rows([Button("keep")]), ct);

        await using var observer = await RealtimeClient.ConnectAsync(owner, ct);
        await observer.SubscribeToChannel(channelId, ct);

        await bot.CallOkAsync(HttpMethod.Patch, "/IInteractions/v1/EditMessage", new { channelId, messageId, text = "v2" }, ct);

        await observer.WaitForAsync<MessageEdited>(e => e.messageId == messageId && e.text == "v2", EventWait, ct: ct);

        var afterText = await StoredMessageAsync(spaceId, channelId, messageId, ct);
        Assert.That(afterText?.Controls?.Single().Controls.Single().Id, Is.EqualTo("keep"), "editing the text alone dropped the controls");

        // An empty list clears the controls; leaving the text out keeps it.
        await bot.CallOkAsync(HttpMethod.Patch, "/IInteractions/v1/EditMessage", new { channelId, messageId, controls = Array.Empty<object>() }, ct);

        var cleared = await StoredMessageAsync(spaceId, channelId, messageId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(cleared?.Text, Is.EqualTo("v2"));
            Assert.That(cleared?.Controls, Is.Null);
        });

        await bot.CallOkAsync(HttpMethod.Patch, "/IInteractions/v1/EditMessage",
            new { channelId, messageId, controls = Rows([Button("fresh")]) }, ct);

        Assert.That((await StoredMessageAsync(spaceId, channelId, messageId, ct))?.Controls?.Single().Controls.Single().Id, Is.EqualTo("fresh"));
    }

    [Test, CancelAfter(120_000)]
    public async Task A_bot_cannot_edit_a_message_it_did_not_send_or_one_that_was_deleted(CancellationToken ct = default)
    {
        var ownersMessage = await owner.Channels.SendMessage(spaceId, channelId, "the owner's words", new IonArray<IMessageEntity>([]),
            Random.Shared.NextInt64(1, long.MaxValue), null, ct);
        var deleted = await bot.SendAsync(spaceId, channelId, "gone soon", null, ct);

        Assert.That(await owner.Channels.DeleteMessage(spaceId, channelId, deleted, ct), Is.InstanceOf<SuccessDeleteMessage>());

        using var notOwn  = await bot.CallAsync(HttpMethod.Patch, "/IInteractions/v1/EditMessage",
            new { channelId, messageId = ownersMessage, text = "hijacked" }, ct);
        using var notLive = await bot.CallAsync(HttpMethod.Patch, "/IInteractions/v1/EditMessage",
            new { channelId, messageId = deleted, text = "back from the dead" }, ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(notOwn.IsSuccessStatusCode, Is.False, "a bot edited somebody else's message");
            Assert.That((await StoredMessageAsync(spaceId, channelId, ownersMessage, ct))?.Text, Is.EqualTo("the owner's words"));

            Assert.That(notLive.IsSuccessStatusCode, Is.False, "a bot edited a deleted message");
            Assert.That((await StoredMessageAsync(spaceId, channelId, deleted, ct))?.Text, Is.EqualTo("gone soon"));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Malformed_controls_are_refused_on_send_and_on_edit(CancellationToken ct = default)
    {
        // A callback button with no label, and a select that sneaks a button field in.
        var badRows = Rows([new { type = (int)ControlType.Button, variant = (int)ButtonVariant.Callback, id = "x" }]);

        using var send = await bot.CallAsync(HttpMethod.Post, "/IMessages/v1/Send", new
        {
            spaceId, channelId, text = "broken", randomId = Random.Shared.NextInt64(1, long.MaxValue), controls = badRows
        }, ct);

        var messageId = await bot.SendAsync(spaceId, channelId, "fine", Rows([Button("ok")]), ct);

        using var edit = await bot.CallAsync(HttpMethod.Patch, "/IInteractions/v1/EditMessage",
            new { channelId, messageId, controls = Rows([new { type = (int)ControlType.StringSelect, customId = "s", label = "no" }]) }, ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(send.IsSuccessStatusCode, Is.False, "a message with a malformed control was stored");
            Assert.That(edit.IsSuccessStatusCode, Is.False, "an edit put a malformed control on a message");
            Assert.That((await StoredMessageAsync(spaceId, channelId, messageId, ct))?.Controls?.Single().Controls.Single().Id,
                Is.EqualTo("ok"));
        });

        var history = await owner.Channels.QueryMessages(spaceId, channelId, null, 50, ct);
        Assert.That(history.Values.Any(m => m.text == "broken"), Is.False);
    }

    // ── Typing ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A bot has no client to send the stop for it when it crashes mid-reply, so the indicator has
    /// to end by itself.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task A_bot_typing_indicator_ends_by_itself_and_restarting_it_keeps_one_timer(CancellationToken ct = default)
    {
        var room = await CreateChannelAsync(owner, spaceId, "bot-typing", ChannelType.Text, ct);

        await using var observer = await RealtimeClient.ConnectAsync(owner, ct);
        await observer.SubscribeToChannel(room, ct);

        using var bad = await bot.CallAsync(HttpMethod.Post, "/ITyping/v1/Start", new { channelId = room, kind = "dancing" }, ct);
        Assert.That(bad.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        await bot.CallOkAsync(HttpMethod.Post, "/ITyping/v1/Start", new { channelId = room, kind = "thinking" }, ct);
        await bot.CallOkAsync(HttpMethod.Post, "/ITyping/v1/Start", new { channelId = room, kind = "searching" }, ct);

        await observer.WaitForAsync<UserTypingEvent>(e => e.userId == bot.UserId && e.kind == TypingKind.SEARCHING, EventWait, ct: ct);

        var started = DateTimeOffset.UtcNow;
        var stop    = await observer.WaitForRecordAsync<UserStopTypingEvent>(e => e.userId == bot.UserId, TimeSpan.FromSeconds(30), ct: ct);

        Assert.That(stop.ReceivedAt - started, Is.GreaterThan(TimeSpan.FromSeconds(5)),
            "the indicator stopped long before its timeout, so the restart did not replace the first timer");

        // Replaced, not doubled: the first Start's timer must not fire a second stop.
        await observer.AssertNoneWithinAsync<UserStopTypingEvent>(e => e.userId == bot.UserId, TimeSpan.FromSeconds(2),
            "a restarted indicator stopped twice", observer.Records().ToList().IndexOf(stop) + 1, ct);
    }

    /// <summary>
    /// A bot's typing indicator claims it is about to post; a channel id alone must not be enough to
    /// make that claim in a space the bot is not in. (A person's indicator is deliberately unchecked.)
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task A_bot_outside_the_space_does_not_show_as_typing(CancellationToken ct = default)
    {
        var room      = await CreateChannelAsync(owner, spaceId, "members-typing", ChannelType.Text, ct);
        var absentBot = await ChannelTestBot.SeedAsync(owner.UserId, ct);

        await using var observer = await RealtimeClient.ConnectAsync(owner, ct);
        await observer.SubscribeToChannel(room, ct);

        using (await absentBot.CallAsync(HttpMethod.Post, "/ITyping/v1/Start", new { channelId = room }, ct)) { }

        // A member typing afterwards is the barrier: once theirs has arrived, the bot has had its turn.
        await guest.Bus.Dispatch(new IAmTypingEvent(room), ct);
        await observer.WaitForAsync<UserTypingEvent>(e => e.userId == guest.UserId && e.channelId == room, EventWait, ct: ct);

        await observer.AssertNoneWithinAsync<UserTypingEvent>(e => e.userId == absentBot.UserId,
            TimeSpan.FromSeconds(1), "a bot outside the space showed as typing in it", 0, ct);
    }

    [Test, CancelAfter(120_000)]
    public async Task An_explicit_stop_is_announced_for_a_bot_and_for_a_member(CancellationToken ct = default)
    {
        var room = await CreateChannelAsync(owner, spaceId, "stop-typing", ChannelType.Text, ct);

        await using var observer = await RealtimeClient.ConnectAsync(owner, ct);
        await observer.SubscribeToChannel(room, ct);

        await bot.CallOkAsync(HttpMethod.Post, "/ITyping/v1/Start", new { channelId = room }, ct);
        await bot.CallOkAsync(HttpMethod.Post, "/ITyping/v1/Stop", new { channelId = room }, ct);

        await guest.Bus.Dispatch(new IAmStopTypingEvent(room), ct);

        await observer.WaitForAsync<UserTypingEvent>(e => e.userId == bot.UserId && e.kind == TypingKind.TYPING, EventWait, ct: ct);
        await observer.WaitForAsync<UserStopTypingEvent>(e => e.userId == bot.UserId, TimeSpan.FromSeconds(5), ct: ct);
        await observer.WaitForAsync<UserStopTypingEvent>(e => e.userId == guest.UserId && e.channelId == room, EventWait, ct: ct);
    }
}
