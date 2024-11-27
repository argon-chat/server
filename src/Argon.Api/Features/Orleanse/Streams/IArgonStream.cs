namespace Argon.Api.Features.Rpc;

using ActualLab.Rpc;
using Contracts;
using Orleans.Streams;

public interface IArgonStream<T> : 
    IAsyncObserver<T>, IAsyncEnumerable<T>, IAsyncDisposable where T : IArgonEvent
{
    public RpcStream<T> AsRpcStream() => new(this);
    ValueTask           Fire(T ev);
}