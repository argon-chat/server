namespace Argon.Load.Client;

using ArgonContracts;
using ion.runtime;
using Microsoft.AspNetCore.SignalR.Client;
using System.Diagnostics;
using System.Formats.Cbor;

/// <summary>
/// One virtual user's live event stream — the SignalR side of the client, next to its RPC side.
/// </summary>
/// <remarks>
/// This is the half of the desktop client that matters once it is up: state lives in IndexedDB and
/// is fed from here, so steady state is almost entirely fan-out and almost nothing else. The
/// connection is built the same way the client builds it in <c>realtimeWorker.ts</c> — hub at
/// <c>/w</c>, bearer token from <c>EventBus.PickTicket</c>.
/// <para>
/// Automatic reconnect is deliberately off. A reconnect during a run would quietly repair whatever
/// the run was trying to expose and the numbers would come out flattering.
/// </para>
/// </remarks>
public sealed class HubListener : IAsyncDisposable
{
    private readonly HubConnection connection;

    private HubListener(HubConnection connection) => this.connection = connection;

    /// <summary>Raised for every event delivered to this user, decoded, with its arrival time.</summary>
    public event Action<IArgonEvent, long>? Received;

    public static async Task<HubListener> ConnectAsync(Uri target, LoadClient client, CancellationToken ct)
    {
        var ticket = await client.Service<IEventBus>().PickTicket(ct);

        var connection = new HubConnectionBuilder()
           .WithUrl(new Uri(target, "/w"), options => options.AccessTokenProvider = () => Task.FromResult<string?>(ticket))
           .Build();

        var listener = new HubListener(connection);

        connection.On<byte[], Guid, string?>("broadcastSpace", (payload, _, _) => listener.Dispatch(payload));
        connection.On<byte[], Guid>("broadcastChannel", (payload, _) => listener.Dispatch(payload));
        connection.On<byte[], string?>("forSelf", (payload, _) => listener.Dispatch(payload));

        await connection.StartAsync(ct);
        return listener;
    }

    /// <summary>
    /// Decoding happens here rather than in the scenario so the arrival timestamp is taken before the
    /// work, not after it: the number wanted is how long the server took, not how long this process
    /// took to make sense of the answer.
    /// </summary>
    private void Dispatch(byte[] payload)
    {
        var arrived = Stopwatch.GetTimestamp();

        try
        {
            var reader = new CborReader(payload);
            var @event = IonFormatterStorage.GetFormatter<IArgonEvent>().Read(reader);

            Received?.Invoke(@event, arrived);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"  undecodable event: {e.Message}");
        }
    }

    public async ValueTask DisposeAsync()
        => await connection.DisposeAsync();
}
