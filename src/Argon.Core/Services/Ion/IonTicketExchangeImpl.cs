namespace Argon.Services.Ion;

using System.Formats.Cbor;
using Argon.Features.Auth;
using Argon.Features.Logic;
using ion.runtime;

public class IonTicketExchangeImpl(
    IArgonCacheDatabase cache,
    IServiceProvider provider,
    IUserPresenceService presence,
    IOptions<ClientAppsOptions> clientApps,
    ILogger<IonTicketExchangeImpl> logger) : IIonTicketExchange
{
    public async Task<ReadOnlyMemory<byte>> OnExchangeCreateAsync(IIonCallContext callContext)
    {
        var req = ArgonRequestContext.Current;

        if (string.IsNullOrEmpty(req.AppId))
            throw new InvalidOperationException($"AppId is null");
        if (string.IsNullOrEmpty(req.MachineId))
            throw new InvalidOperationException($"MachineId is null");
        if (req.SessionId is null)
            throw new InvalidOperationException($"SessionId is null");

        // The ids and the mint time a realtime stream is judged by for revocation, as PickTicket
        // stamps them into the hub ticket.
        var credentials = await EventBusImpl.CredentialIdentitiesAsync(
            cache, logger, req.UserId!.Value, req.SessionId.Value, CancellationToken.None);

        var ticket = new ArgonIonTicket(req.UserId!.Value, req.Ip, req.Ray, req.ClientName, "", req.AppId, req.SessionId.Value, req.MachineId,
            req.Region,
            credentials.Select(x => Guid.TryParse(x, out var id) ? id : Guid.Empty).Where(x => x != Guid.Empty).ToArray(),
            DateTimeOffset.UtcNow);
        var writer = new CborWriter();
        IonFormatterStorage<ArgonIonTicket>.Write(writer, ticket);

        var ticketId = ArgonId.New();

        // Exactly the encoded bytes: a rented buffer is larger, and its tail is whatever it held last.
        await cache.StringSetAsync($"ion_exchange_{ticketId}", Convert.ToBase64String(writer.Encode()), TimeSpan.FromMinutes(1));

        // The ticket is where a session becomes describable: it is issued once per connection and
        // already carries the client string and the country that the devices screen needs to name the
        // row. Recording it here rather than per request keeps a Redis write off the hot path, and
        // rather than in UserSessionGrain because the grain is reached through Orleans' request
        // context, which carries the ids but not the client name.
        await presence.TouchSessionMetaAsync(ticket.userId, ticket.sessionId.ToString(),
            UserSessionMeta.Describe(req, clientApps.Value.Find(req.AppId, req.Client)));

        return ticketId.ToByteArray();
    }

    public async Task<(IonProtocolError?, object? ticket)> OnExchangeTransactionAsync(ReadOnlyMemory<byte> exchangeToken)
    {
        if (exchangeToken.Length != 16)
            return (new IonProtocolError("BAD_TICKET", $"Invalid token length: expected 16 bytes, got {exchangeToken.Length}"), null);

        var ticketId = new Guid(exchangeToken.Span);
        var key      = await cache.StringGetAsync($"ion_exchange_{ticketId}");

        if (key is null)
            return (new IonProtocolError("BAD_TICKET", "Ticket not found or expired"), null);

        try
        {
            var ticketBytes = Convert.FromBase64String(key);
            var reader      = new CborReader(ticketBytes);

            var ticket = IonFormatterStorage<ArgonIonTicket>.Read(reader);

            return (null, ticket);
        }
        catch (Exception e)
        {
            return (IonProtocolError.INTERNAL_ERROR(e.Message), null);
        }
    }

    public void OnTicketApply(object ticketObject)
    {
        var t = ticketObject as ArgonIonTicket;
        ArgonRequestContext.Set(new ArgonRequestContextData()
        {
            UserId     = t!.userId,
            Scope      = provider,
            AppId      = t.appId,
            ClientName = t.clientName,
            Ip         = t.ip,
            SessionId  = t.sessionId,
            MachineId  = t.machineId,
            Ray        = t.ray,
            Region     = t.region,
            Client     = ClientDescriptor.FromUserAgent(t.clientName)
        });

        var reentrancy = RequestContext.AllowCallChainReentrancy();

        reentrancy.SetUserId(t.userId);
        reentrancy.SetUserCountry(t.region);
        reentrancy.SetUserIp(t.ip);
        reentrancy.SetUserMachineId(t.machineId);
        reentrancy.SetUserSessionId(t.sessionId);
        reentrancy.SetUserAppId(t.appId);
        reentrancy.SetUserClient(ClientDescriptor.FromUserAgent(t.clientName));
    }
}
