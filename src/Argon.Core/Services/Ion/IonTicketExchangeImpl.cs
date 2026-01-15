namespace Argon.Services.Ion;

using System.Buffers;
using System.Formats.Cbor;
using ion.runtime;

public class IonTicketExchangeImpl(IArgonCacheDatabase cache, IServiceProvider provider) : IIonTicketExchange
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

        var ticket = new ArgonIonTicket(req.UserId!.Value, req.Ip, req.Ray, req.ClientName, "", req.AppId, req.SessionId.Value, req.MachineId,
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
            UserId     = t.userId,
            Scope      = provider,
            AppId      = t.appId,
            ClientName = t.clientName,
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