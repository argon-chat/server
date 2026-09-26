namespace ArgonComplexTest;

using AccountContracts;
using ArgonContracts;
using ion.runtime;

/// <summary>
/// Unwraps the result union of a call the test expects to succeed, and fails with the refusal when it
/// does not. Refusals themselves are asserted on the union: <c>Is.EqualTo(new FailedX(error))</c>.
/// </summary>
internal static class IonResults
{
    public static async Task<long> Ok(this Task<ISendMessageResult> call)
        => (await call.Readback()).messageId;

    public static async Task<SendMessageReadback> Readback(this Task<ISendMessageResult> call)
        => await call switch { SuccessSendMessage ok => ok.readback, var other => throw Refused(other) };

    public static async Task Ok(this Task<IChannelLayoutResult> call)
        => _ = await call switch { SuccessChannelLayout ok => ok, var other => throw Refused(other) };

    public static async Task<Archetype> Ok(this Task<ICreateArchetypeResult> call)
        => await call switch { SuccessCreateArchetype ok => ok.archetype, var other => throw Refused(other) };

    public static async Task<Archetype> Ok(this Task<IUpdateArchetypeResult> call)
        => await call switch { SuccessUpdateArchetype ok => ok.archetype, var other => throw Refused(other) };

    public static async Task<InviteCode> Ok(this Task<ICreateInviteCodeResult> call)
        => await call switch { SuccessCreateInviteCode ok => ok.code, var other => throw Refused(other) };

    public static async Task<ServerInvites> Ok(this Task<IGetInviteCodesResult> call)
        => await call switch { SuccessGetInviteCodes ok => ok.invites, var other => throw Refused(other) };

    public static async Task Ok(this Task<ISpaceManageResult> call)
        => _ = await call switch { SuccessSpaceManage ok => ok, var other => throw Refused(other) };

    public static async Task<TeamDetails> Ok(this Task<IGetTeamDetailsResult> call)
        => await call switch { SuccessGetTeamDetails ok => ok.team, var other => throw Refused(other) };

    public static async Task<IonArray<TeamInviteInfo>> Ok(this Task<IGetTeamInvitesResult> call)
        => await call switch { SuccessGetTeamInvites ok => ok.invites, var other => throw Refused(other) };

    public static async Task<AppDetails> Ok(this Task<IAppDetailsResult> call)
        => await call switch { SuccessAppDetails ok => ok.app, var other => throw Refused(other) };

    public static async Task<string> Ok(this Task<IRegenerateBotTokenResult> call)
        => await call switch { SuccessRegenerateBotToken ok => ok.token, var other => throw Refused(other) };

    public static async Task Ok(this Task<IAppManagementResult> call)
        => _ = await call switch { SuccessAppManagement ok => ok, var other => throw Refused(other) };

    private static AssertionException Refused(object result)
        => new($"expected the call to succeed, it was refused: {result}");
}
