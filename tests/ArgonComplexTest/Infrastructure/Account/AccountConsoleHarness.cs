namespace ArgonComplexTest.Infrastructure.Account;

using AccountContracts;
using Argon.Grains.Interfaces;
using Argon.Services.Ion;
using ArgonContracts;
using ArgonComplexTest.Infrastructure.Presence;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The account console, driven the way the console's own Ion executor drives it.
/// </summary>
/// <remarks>
/// <para><b>Why not over HTTP.</b> <c>IAccountConsole</c> answers on a port of its own behind
/// <c>AccountConsoleAuthInterceptor</c>, which validates an OIDC access token from Aegis against a
/// live JWKS endpoint. The integration host configures no OIDC provider for that port, so there is no
/// token to present; standing one up would be a test of the interceptor rather than of the console.
/// This is exactly the trade <c>AdminConsoleTests.Admin()</c> makes for <c>IAdminConsole</c>, and for
/// the same reason.</para>
///
/// <para><b>Why setting the context is equivalent.</b> The interceptor's entire output is the ambient
/// <see cref="ArgonRequestContext"/>: it resolves the bearer token to a user id and copies the token's
/// display claims into <c>Props</c>, and then <c>Ion_AccountConsole_ServiceExecutor</c> resolves
/// <c>IAccountConsole</c> out of a scope and calls the method. Nothing else about the interceptor is
/// visible to the service — it holds no state, decorates no call, and the service never asks whether
/// one ran. So the pair below <em>is</em> the state every console method runs under, and a test built
/// on it exercises the console rather than the transport in front of it.</para>
///
/// <para><b>What that deliberately does not cover</b>, and what a reader must not mistake this for:
/// the console's authentication. Nothing here proves that a request without a token is refused, that
/// a token for user A cannot act on user B, or that an expired one is rejected — this harness
/// <em>is</em> the thing that would have to be bypassed for any of those to be tested, and it grants
/// whatever identity it is handed. Authorisation inside the console (a method that reads the caller's
/// own id and refuses to touch another's) is testable here and worth testing; the interceptor in
/// front of it is not.</para>
/// </remarks>
public static class AccountConsoleHarness
{
    /// <summary>
    /// Resolves the console with <paramref name="session"/>'s identity in scope.
    /// </summary>
    /// <remarks>
    /// <para>Synchronous on purpose. <see cref="ArgonRequestContext"/> is an
    /// <see cref="System.Threading.AsyncLocal{T}"/>, and a value written inside an <c>async</c> helper
    /// is written into that helper's own execution context — it would be gone by the time the caller
    /// resumed. Written from a synchronous call it mutates the caller's context and survives every
    /// <c>await</c> that follows, which is what the fixtures need and what <c>AdminConsoleTests</c>
    /// relies on too.</para>
    ///
    /// <para>The scope is returned rather than disposed here because the console resolves scoped
    /// services out of it for the duration of the call; the caller owns it with
    /// <c>await using</c>.</para>
    /// </remarks>
    public static (AsyncServiceScope Scope, IAccountConsole Console) Console(TestUserSession session)
        => Console(session.UserId, session.Credentials.displayName, session.SessionId);

    /// <summary>
    /// The same, for a user a test knows only by id — a seeded account, or one whose session has
    /// already been revoked.
    /// </summary>
    /// <param name="userId">Who the console is acting as.</param>
    /// <param name="displayName">
    /// What the token's display claim would have said. The console renders this straight out of
    /// <c>Props</c> and never loads the user row for it, so a test asserting on
    /// <c>MeDetails.displayName</c> is asserting on what is passed here.
    /// </param>
    /// <param name="sessionId">The <c>sid</c> claim, when the test has one worth carrying.</param>
    /// <param name="avatarId">The avatar claim, likewise rendered straight out of <c>Props</c>.</param>
    public static (AsyncServiceScope Scope, IAccountConsole Console) Console(
        Guid userId, string displayName = "Console User", Guid? sessionId = null, string avatarId = "")
    {
        var scope = ArgonTestEnvironment.Instance.Host.Services.CreateAsyncScope();

        ArgonRequestContext.Set(new ArgonRequestContextData
        {
            Ip         = "127.0.0.1",
            Region     = "us",
            Ray        = Guid.NewGuid().ToString("N"),
            ClientName = "integration-tests",
            AppId      = null,
            SessionId  = sessionId ?? Guid.NewGuid(),
            MachineId  = "integration-tests",
            UserId     = userId,
            Scope      = scope.ServiceProvider,
            Props =
            {
                ["displayName"] = displayName,
                ["avatarId"]    = avatarId
            }
        });

        return (scope, scope.ServiceProvider.GetRequiredService<IAccountConsole>());
    }

    /// <summary>
    /// Polls the caller's own export over Ion until it reaches <paramref name="status"/>.
    /// </summary>
    /// <remarks>
    /// Over <c>ISecurityInteraction</c> rather than against the grain, because that is the surface the
    /// desktop client actually watches and the one whose faithfulness is worth pinning. Returns the
    /// last status read either way, so a caller that timed out asserts on what it saw — "was
    /// COLLECTING" says something; "the poll timed out" says nothing.
    /// </remarks>
    public static Task<DataExportStatus> WaitForExportAsync(
        TestUserSession session,
        DataExportStatusKind status,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
        => Poll.ForValueAsync(
            async () => await session.Security.GetDataExportStatus(ct),
            reported => reported.status == status,
            timeout ?? AccountTimings.ExportBudget,
            AccountTimings.ExportTick / 4,
            ct);

    /// <summary>
    /// Polls the deletion grain until it reports <paramref name="status"/>, driving the poll by hand.
    /// </summary>
    /// <remarks>
    /// <para><c>CheckAndExecuteAsync</c> is called on every attempt rather than waiting for the
    /// grain's own timer. The timer is running too — the host's <c>CheckInterval</c> is two seconds —
    /// but which of the two gets there first is a race, and a fixture that depends on the timer is
    /// asserting on Orleans' scheduling rather than on the deletion. Calling it directly is what the
    /// grain's own interface exposes it for.</para>
    ///
    /// <para>Idempotence is the reason this is safe: the body returns immediately unless the status is
    /// <c>Scheduled</c>, remembers each reminder it sent, and executes once.</para>
    /// </remarks>
    public static Task<AccountDeletionStatusKind> DriveDeletionUntilAsync(
        Guid userId,
        AccountDeletionStatusKind status,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var grain = ArgonTestEnvironment.Instance.Host.Services
           .GetRequiredService<IGrainFactory>()
           .GetGrain<IAccountDeletionGrain>(userId);

        return Poll.ForValueAsync(
            async () =>
            {
                await grain.CheckAndExecuteAsync();

                return (await grain.GetDeletionStatusAsync()).Status;
            },
            reported => reported == status,
            timeout ?? AccountTimings.ExecutionBudget,
            AccountTimings.Slack / 4,
            ct);
    }
}
