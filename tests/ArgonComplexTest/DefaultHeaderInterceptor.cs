namespace ArgonComplexTest;

using ion.runtime;

/// <summary>
/// Stamps the session/device headers every Ion call needs, plus the bearer token of whoever the
/// owning <see cref="TestBase"/> is currently acting as.
/// <para>
/// One instance per caller, never a shared singleton: the token is mutable, so a single instance
/// behind concurrently running fixtures would have them authenticating as each other. The session
/// and machine ids are per instance too, so parallel fixtures look like genuinely distinct clients
/// to the device-history and session-tracking code paths.
/// </para>
/// </summary>
public class DefaultHeaderInterceptor : IIonInterceptor
{
    private readonly Guid    _sessionId = Guid.CreateVersion7();
    private readonly Guid    _machineId;

    public DefaultHeaderInterceptor() : this(Guid.CreateVersion7())
    {
    }

    /// <summary>
    /// A client on a machine the test names. Two sessions built with the same id look like two
    /// accounts on one device, which is the shape the report system's independence rule exists to
    /// see through.
    /// </summary>
    public DefaultHeaderInterceptor(Guid machineId)
        => _machineId = machineId;

    /// <summary>
    /// The session id this client claims, verbatim as it goes out in <c>Sec-Ref</c>.
    /// </summary>
    /// <remarks>
    /// This is the <c>sid</c> the whole presence system is keyed on. The Ion side reads it back as
    /// <c>this.GetSessionId()</c>, <c>EventBus.PickTicket</c> stamps it into the hub ticket as the
    /// <c>sid</c> claim, and <c>AppHub</c> turns it into the session grain key
    /// <c>"{userId}:{sid}"</c>. A presence test that wants to read the Redis keys behind a session —
    /// <c>presence:user:{u}:session:{sid}</c> and friends — has no other way of learning which sid
    /// its own client is, and guessing is not an option because the value is minted here.
    /// </remarks>
    public Guid SessionId => _sessionId;

    /// <summary>
    /// The device id this client claims, verbatim as it goes out in <c>Sec-Carry</c>.
    /// </summary>
    /// <remarks>
    /// Exposed for tests that mint a token by hand: a refresh token carries a hash of the machine
    /// id, and the refresh path refuses one that does not match the caller's. A test that minted
    /// with some other value would be asserting that mismatch rather than whatever it meant to.
    /// </remarks>
    public string MachineId => _machineId.ToString();
    private volatile string? _authToken;

    public async Task InvokeAsync(IIonCallContext context, Func<IIonCallContext, CancellationToken, Task> next, CancellationToken ct)
    {
        context.RequestItems.Add("Sec-Ref", _sessionId.ToString());
        // X-Ctt carries the same value, and without it the session id this client claims is thrown
        // away. HttpContextExtensions.GetSessionId checks the ArgonSecure cookie, then — on a
        // Development host, which is what WebApplicationFactory boots — returns Guid.AllBitsSet for
        // any caller that did not send X-Ctt, never reaching the Sec-Ref fallback below it. Every
        // client in the test process would therefore be one session: one session grain per user, one
        // row on the devices screen, and no way to express two devices of one account at all. The
        // header changes nothing anywhere else — it is read in that one branch and nowhere in
        // production — so sending it makes the suite behave the way a deployed server does.
        context.RequestItems.Add("X-Ctt", _sessionId.ToString());
        context.RequestItems.Add("Sec-Ner", "1");
        context.RequestItems.Add("Sec-Carry", _machineId.ToString());

        if (!string.IsNullOrEmpty(_authToken))
            context.RequestItems.Add("Authorization", $"Bearer {_authToken}");

        await next(context, ct);
    }

    public void SetToken(string? t) => _authToken = t;
}
