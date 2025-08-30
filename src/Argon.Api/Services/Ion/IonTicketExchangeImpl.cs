namespace Argon.Services.Ion;

using System.Buffers;
using System.Formats.Cbor;
using ion.runtime;

public class IonTicketExchangeImpl(IArgonCacheDatabase cache, IServiceProvider provider) : IIonTicketExchange
{
    public async Task<ReadOnlyMemory<byte>> OnExchangeCreateAsync(IIonCallContext callContext)
    {
        var req = ArgonRequestContext.Current;

        var ticket = new ArgonIonTicket(req.UserId!.Value, req.Ip, req.Ray, req.ClientName, req.HostName, req.AppId, req.SessionId, req.MachineId,
            req.Region);
        var writer = new CborWriter();
        IonFormatterStorage<ArgonIonTicket>.Write(writer, ticket);

        var ticketId = Guid.CreateVersion7();

        using var mem = MemoryPool<byte>.Shared.Rent(writer.BytesWritten);

        writer.Encode(mem.Memory.Span);

        await cache.StringSetAsync($"ion_exchange_{ticketId}", Convert.ToBase64String(mem.Memory.Span), TimeSpan.FromMinutes(1));

        return ticketId.ToByteArray();
    }

    public async Task<(IonProtocolError?, object? ticket)> OnExchangeTransactionAsync(ReadOnlyMemory<byte> exchangeToken)
    {
        var ticketId = new Guid(exchangeToken.Span);
        var key      = await cache.StringGetAsync($"ion_exchange_{ticketId}");

        if (key is null)
            return (new IonProtocolError("BAD_TICKET", ""), null);

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
            UserId     = t.userId,
            Scope      = new AsyncServiceScope(provider.CreateScope()),
            AppId      = t.appId,
            ClientName = t.clientName,
            HostName   = t.hostName,
            Ip         = t.ip,
            SessionId  = t.sessionId,
            MachineId  = t.machineId,
            Ray        = t.ray,
            Region     = t.region
        });

        var reentrancy = RequestContext.AllowCallChainReentrancy();

        reentrancy.SetUserId(t.userId);
        reentrancy.SetUserCountry(t.region);
        reentrancy.SetUserIp(t.ip);
        reentrancy.SetUserMachineId(t.machineId);
        reentrancy.SetUserSessionId(t.sessionId);
    }
}