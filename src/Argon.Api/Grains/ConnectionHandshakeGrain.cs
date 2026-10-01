namespace Argon.Grains;

using Argon.Features.Integrations.Connections;
using Argon.Grains.Interfaces;
using Orleans.Concurrency;

/// <inheritdoc cref="IConnectionHandshakeGrain"/>
[StatelessWorker]
public sealed class ConnectionHandshakeGrain(
    ConnectionHandshakeStore store,
    IConnectionProviderRegistry providers,
    IOptions<ConnectionsOptions> options,
    ILogger<ConnectionHandshakeGrain> logger) : Grain, IConnectionHandshakeGrain
{
    private ConnectionsOptions Options => options.Value;

    public Task<List<ConnectionProviderInfo>> GetProvidersAsync(CancellationToken ct = default)
        => Task.FromResult(Options.Enabled
            ? providers.Configured().Select(p => new ConnectionProviderInfo(p.Kind, p.Capabilities)).ToList()
            : []);

    public async Task<BeginConnectOutcome> BeginAsync(Guid userId, ConnectionProvider provider, ConnectReturnKind returnTo, bool replace,
        CancellationToken ct = default)
    {
        if (!Options.Enabled || providers.Get(provider) is not { } adapter)
            return BeginConnectOutcome.Refused(BeginConnectError.PROVIDER_DISABLED);

        if (!await store.TryAcquireBeginAsync(userId, ct))
            return BeginConnectOutcome.Refused(BeginConnectError.RATE_LIMITED);

        // Re-authenticating a connection that lost its grant needs no "replace"; swapping the
        // account behind a live one does. Which of the two this is only becomes known when the
        // provider answers, so the same rule is applied again at completion.
        var existing = await GrainFactory.GetGrain<IUserConnectionsGrain>(userId).GetExistingAsync(provider, ct);

        if (existing is { Status: ConnectionStatus.SUSPENDED })
            return BeginConnectOutcome.Refused(BeginConnectError.CONNECTION_SUSPENDED);

        if (existing is { Status: not ConnectionStatus.NEEDS_REAUTH } && !replace)
            return BeginConnectOutcome.Refused(BeginConnectError.PROVIDER_ALREADY_LINKED);

        var now         = DateTimeOffset.UtcNow;
        var state       = OAuthCodeFlow.NewState();
        var verifier    = adapter.UsesPkce ? OAuthCodeFlow.NewPkceVerifier() : null;
        var redirectUri = Options.CallbackUrl(provider);
        var handshakeId = ArgonId.New();

        await store.PutStartedAsync(new ConnectionHandshakeStore.Started(
            handshakeId, userId, provider, state, verifier, redirectUri, returnTo, replace, now), ct);

        var url = adapter.BuildAuthorizationUrl(new HandshakeContext(state, redirectUri, verifier));

        return new BeginConnectOutcome(BeginConnectError.NONE, url, handshakeId, now + Options.HandshakeTtl);
    }

    public async Task<HandshakePage> CompleteAsync(ConnectionProvider provider, Dictionary<string, string> query, Guid? browserUserId,
        CancellationToken ct = default)
    {
        if (!Options.Enabled || providers.Get(provider) is not { } adapter)
            return Page(HandshakePageKind.Disabled, provider, Guid.Empty, null, null, null, ConnectReturnKind.DESKTOP);

        var started = await store.ConsumeStartedAsync(query.GetValueOrDefault("state"), ct);

        if (started is null || started.Provider != provider)
            return Page(HandshakePageKind.Expired, provider, Guid.Empty, null, null, null, ConnectReturnKind.DESKTOP);

        HandshakeResult result;

        try
        {
            result = await adapter.CompleteAsync(new HandshakeContext(started.State, started.RedirectUri, started.PkceVerifier), query, ct);
        }
        catch (Exception e) when (e is ProviderCallException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(e, "{Provider} handshake {HandshakeId} failed at the provider", provider, started.HandshakeId);
            return Page(HandshakePageKind.ProviderError, provider, started.HandshakeId, null, null, null, started.ReturnTo);
        }

        switch (result.Outcome)
        {
            case HandshakeOutcome.Denied:
                return Page(HandshakePageKind.Denied, provider, started.HandshakeId, null, null, null, started.ReturnTo);
            case HandshakeOutcome.Failed:
                logger.LogWarning("{Provider} handshake {HandshakeId} failed: {Error}", provider, started.HandshakeId, result.Error);
                return Page(HandshakePageKind.ProviderError, provider, started.HandshakeId, null, null, null, started.ReturnTo);
        }

        // Parked before anything is asked of the browser: the code has been spent and cannot be spent
        // again, so whatever the browser turns out to be, the exchange is kept for it to finish.
        await store.ParkAsync(started.HandshakeId, started.UserId, provider, result, started.ReturnTo, started.Replace, ct);

        return await SettleAsync(started.HandshakeId, browserUserId, fromCallback: true, ct);
    }

    public Task<HandshakePage> ResumeAsync(Guid handshakeId, Guid? browserUserId, CancellationToken ct = default)
        => SettleAsync(handshakeId, browserUserId, fromCallback: false, ct);

    /// <summary>
    /// The gate: the parked result becomes a row only when the browser's user is the one that started
    /// the handshake. Anything else leaves it parked (no browser user) or throws it away (the wrong one).
    /// </summary>
    private async Task<HandshakePage> SettleAsync(Guid handshakeId, Guid? browserUserId, bool fromCallback, CancellationToken ct)
    {
        if (browserUserId is null)
        {
            var parked = await store.PeekParkedAsync(handshakeId, ct);

            if (parked is null)
                return Page(HandshakePageKind.Expired, default, handshakeId, null, null, null, ConnectReturnKind.DESKTOP);

            return Page(fromCallback ? HandshakePageKind.Continue : HandshakePageKind.NeedsSignIn,
                parked.Provider, handshakeId, parked.Identity.Name, await UsernameAsync(parked.UserId, ct), null, parked.ReturnTo);
        }

        var pending = await store.ConsumeParkedAsync(handshakeId, ct);

        if (pending is null)
            return Page(HandshakePageKind.Expired, default, handshakeId, null, null, null, ConnectReturnKind.DESKTOP);

        if (pending.UserId != browserUserId.Value)
        {
            logger.LogWarning("Handshake {HandshakeId} for {Provider} was started by {Expected} and finished in a browser signed in as {Actual}; refused",
                handshakeId, pending.Provider, pending.UserId, browserUserId);

            return Page(HandshakePageKind.WrongUser, pending.Provider, handshakeId, pending.Identity.Name,
                await UsernameAsync(pending.UserId, ct), await UsernameAsync(browserUserId.Value, ct), pending.ReturnTo);
        }

        var outcome = await GrainFactory.GetGrain<IUserConnectionsGrain>(pending.UserId).LinkAsync(
            new LinkRequest(pending.Provider, pending.Identity, store.OpenParkedToken(pending), pending.Details, pending.Scopes, pending.Replace), ct);

        var kind = outcome switch
        {
            LinkOutcome.Linked                 => HandshakePageKind.Linked,
            LinkOutcome.AlreadyLinkedElsewhere => HandshakePageKind.AlreadyLinkedElsewhere,
            LinkOutcome.ProviderAlreadyLinked  => HandshakePageKind.ProviderAlreadyLinked,
            LinkOutcome.Suspended              => HandshakePageKind.Suspended,
            _                                  => HandshakePageKind.ProviderError
        };

        return Page(kind, pending.Provider, handshakeId, pending.Identity.Name, await UsernameAsync(pending.UserId, ct), null, pending.ReturnTo);
    }

    private async Task<string?> UsernameAsync(Guid userId, CancellationToken ct)
    {
        try
        {
            return (await GrainFactory.GetGrain<IIdentityDirectoryGrain>(Guid.Empty).GetUserBasicInfoAsync(userId, ct))?.Username;
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "No username for {UserId} on the handshake page", userId);
            return null;
        }
    }

    private HandshakePage Page(HandshakePageKind kind, ConnectionProvider provider, Guid handshakeId, string? externalName,
        string? expectedUsername, string? signedInUsername, ConnectReturnKind returnTo)
        => new(kind, provider, handshakeId, externalName, expectedUsername, signedInUsername, returnTo,
            string.IsNullOrWhiteSpace(Options.WebAppUrl) ? null : Options.WebAppUrl);
}
