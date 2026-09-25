namespace ArgonComplexTest.Tests;

using System.Text;
using System.Text.Json;
using Argon.Core.Grains.Interfaces;
using ArgonContracts;

/// <summary>
/// One-to-one calls: ringing an online user, who may answer and how often, who may hang up, and
/// the room tokens each side is handed.
/// </summary>
/// <remarks>
/// The tokens are LiveKit JWTs signed in process, so no SFU is needed. <see cref="SystemMessageTests"/>
/// covers the chat messages a call leaves behind.
/// </remarks>
[TestFixture]
public class DirectCallTests : TestBase
{
    private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(10);

    private static ICallInteraction Calls(TestUserSession session) => session.Client.ForService<ICallInteraction>(SocialHarness.Services);

    private static ICallGrain Call(Guid callId) => SocialHarness.Grains.GetGrain<ICallGrain>(callId);

    /// <summary>The claims of a JWT, unverified — the signature is LiveKit's business, the grants are ours.</summary>
    private static JsonElement Claims(string jwt)
    {
        var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        return JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload))).RootElement;
    }

    private static void AssertRoomToken(string jwt, Guid callId, Guid identity)
    {
        var claims = Claims(jwt);

        Assert.Multiple(() =>
        {
            Assert.That(claims.GetProperty("sub").GetString(), Is.EqualTo(identity.ToString()));
            Assert.That(claims.GetProperty("video").GetProperty("room").GetString(), Is.EqualTo($"call_{callId:N}"));
            Assert.That(claims.GetProperty("video").GetProperty("roomJoin").GetBoolean(), Is.True);
        });
    }

    private async Task<(TestUserSession Caller, TestUserSession Callee, Guid CallId, string CallerToken)> RingAsync(
        bool calleeOnline, CancellationToken ct)
    {
        var caller = await CreateSessionAsync(ct);
        var callee = await CreateSessionAsync(ct);

        await using var _ = calleeOnline ? await SocialHarness.OnlineAsync(callee, ct) : null;

        var started = await Calls(caller).DingDongCreep(callee.UserId, ct);
        Assert.That(started, Is.InstanceOf<SuccessDingDong>(), $"the call did not start: {(started as FailedDingDong)?.error}");

        var ring = (SuccessDingDong)started;
        return (caller, callee, ring.callId, ring.token);
    }

    [Test, CancelAfter(120_000)]
    public async Task Ringing_an_online_user_and_answering_connects_both_sides(CancellationToken ct = default)
    {
        var caller = await CreateSessionAsync(ct);
        var callee = await CreateSessionAsync(ct);

        await using var callerStream = await SocialHarness.OnlineAsync(caller, ct);
        await using var calleeStream = await SocialHarness.OnlineAsync(callee, ct);

        var ring   = (SuccessDingDong)await Calls(caller).DingDongCreep(callee.UserId, ct);
        var callId = ring.callId;

        AssertRoomToken(ring.token, callId, caller.UserId);
        await calleeStream.WaitForRecordAsync<CallIncoming>(e => e.callId == callId && e.fromId == caller.UserId, EventWait, ct: ct);

        var ringing = await Call(callId).GetStateAsync(ct);
        Assert.Multiple(() =>
        {
            Assert.That(ringing.Status, Is.EqualTo(CallStatus.Ringing));
            Assert.That((ringing.CallerId, ringing.CalleeId), Is.EqualTo((caller.UserId, callee.UserId)));
        });

        var answer = await Calls(callee).PickUpCall(callId, ct);
        Assert.That(answer, Is.InstanceOf<SuccessPickUp>(), $"refused: {(answer as FailedPickUp)?.error}");

        AssertRoomToken(((SuccessPickUp)answer).token, callId, callee.UserId);
        await callerStream.WaitForRecordAsync<CallAccepted>(e => e.callId == callId && e.fromId == callee.UserId, EventWait, ct: ct);

        Assert.That((await Call(callId).GetStateAsync(ct)).Status, Is.EqualTo(CallStatus.Accepted));

        await Calls(caller).HangupCall(callId, ct);

        await callerStream.WaitForRecordAsync<CallFinished>(e => e.callId == callId, EventWait, ct: ct);
        await calleeStream.WaitForRecordAsync<CallFinished>(e => e.callId == callId, EventWait, ct: ct);

        Assert.That((await Call(callId).GetStateAsync(ct)).Status, Is.EqualTo(CallStatus.Ended));

        // A second hang-up — the other side's button, pressed a moment late — finds nothing to end.
        await Calls(callee).HangupCall(callId, ct);
        await Task.Delay(500, ct);

        Assert.That(calleeStream.Records().Count(r => r.Event is CallFinished f && f.callId == callId), Is.EqualTo(1),
            "ending an ended call announced it again");
    }

    [Test, CancelAfter(120_000)]
    public async Task Only_the_callee_can_answer_and_only_once(CancellationToken ct = default)
    {
        var (caller, callee, callId, _) = await RingAsync(calleeOnline: true, ct);
        var stranger = await CreateSessionAsync(ct);

        var byStranger = await Calls(stranger).PickUpCall(callId, ct);
        var byCaller   = await Calls(caller).PickUpCall(callId, ct);
        var byCallee   = await Calls(callee).PickUpCall(callId, ct);
        var again      = await Calls(callee).PickUpCall(callId, ct);

        Assert.Multiple(() =>
        {
            Assert.That((byStranger as FailedPickUp)?.error, Is.EqualTo("not_callee"));
            Assert.That((byCaller as FailedPickUp)?.error, Is.EqualTo("not_callee"));
            Assert.That(byCallee, Is.InstanceOf<SuccessPickUp>());
            Assert.That((again as FailedPickUp)?.error, Is.EqualTo("not_ringing"));
        });

        await Calls(callee).HangupCall(callId, ct);
    }

    /// <summary>
    /// Only the two people in a call can end it.
    /// </summary>
    /// <remarks>
    /// <c>HangupAsync</c> took the caller's id and never looked at it, so anyone holding a call id
    /// — every <c>CallIncoming</c> carries one — could end somebody else's call.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_stranger_cannot_hang_up_someone_elses_call(CancellationToken ct = default)
    {
        var (_, callee, callId, _) = await RingAsync(calleeOnline: true, ct);
        var stranger = await CreateSessionAsync(ct);

        await Calls(stranger).HangupCall(callId, ct);
        await Calls(stranger).RejectCall(callId, ct);

        Assert.That((await Call(callId).GetStateAsync(ct)).Status, Is.EqualTo(CallStatus.Ringing),
            "a third party ended the call");

        Assert.That(await Calls(callee).PickUpCall(callId, ct), Is.InstanceOf<SuccessPickUp>());
        await Calls(callee).HangupCall(callId, ct);
    }

    [Test, CancelAfter(120_000)]
    public async Task Rejecting_a_ringing_call_ends_it_for_the_caller(CancellationToken ct = default)
    {
        var caller = await CreateSessionAsync(ct);
        var callee = await CreateSessionAsync(ct);

        await using var callerStream = await SocialHarness.OnlineAsync(caller, ct);
        await using var calleeStream = await SocialHarness.OnlineAsync(callee, ct);

        var callId = ((SuccessDingDong)await Calls(caller).DingDongCreep(callee.UserId, ct)).callId;

        await Calls(callee).RejectCall(callId, ct);

        await callerStream.WaitForRecordAsync<CallFinished>(e => e.callId == callId, EventWait, ct: ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That((await Call(callId).GetStateAsync(ct)).Status, Is.EqualTo(CallStatus.Ended));
            Assert.That(await Calls(callee).PickUpCall(callId, ct), Is.InstanceOf<FailedPickUp>(), "a rejected call was answered");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Ringing_someone_offline_rings_into_the_void(CancellationToken ct = default)
    {
        var (caller, callee, callId, token) = await RingAsync(calleeOnline: false, ct);

        var state = await Call(callId).GetStateAsync(ct);

        Assert.Multiple(() =>
        {
            AssertRoomToken(token, callId, caller.UserId);
            Assert.That(state.Status, Is.EqualTo(CallStatus.Ringing), "the caller still hears ringing until the timeout");
            Assert.That(state.CalleeId, Is.EqualTo(callee.UserId));
            Assert.That(state.CalleeToken, Is.Null, "nobody was rung, so no token was minted for them");
        });

        await Calls(caller).HangupCall(callId, ct);
    }

    /// <summary>A caller the callee has blocked rings into the void: the callee never hears about it.</summary>
    [Test, CancelAfter(120_000)]
    public async Task A_blocked_caller_rings_into_the_void(CancellationToken ct = default)
    {
        var caller = await CreateSessionAsync(ct);
        var callee = await CreateSessionAsync(ct);
        await callee.Friends.BlockUser(caller.UserId, ct);

        await using var calleeStream = await SocialHarness.OnlineAsync(callee, ct);
        var mark = calleeStream.Mark();

        var started = await Calls(caller).DingDongCreep(callee.UserId, ct);
        Assert.That(started, Is.InstanceOf<SuccessDingDong>(), "the caller is not told they are blocked");
        var callId = ((SuccessDingDong)started).callId;

        var rang = await calleeStream.FirstWithinAsync<CallIncoming>(e => e.callId == callId, EventWait, mark, ct);
        var answer = await Calls(callee).PickUpCall(callId, ct);

        await Calls(caller).HangupCall(callId, ct);
        var finished = await calleeStream.FirstWithinAsync<CallFinished>(e => e.callId == callId, TimeSpan.FromSeconds(2), mark, ct);

        Assert.Multiple(() =>
        {
            Assert.That(rang, Is.Null, "the blocker's client rang");
            Assert.That((answer as FailedPickUp)?.error, Is.EqualTo("not_ringing"),
                "a call the blocker never saw cannot be answered by its id");
            Assert.That(finished, Is.Null, "the blocker was told a call they never saw had ended");
        });
    }
}
