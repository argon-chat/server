namespace Argon.Api.Features.Rpc;

using ActualLab.Reflection;
using ActualLab.Rpc.Infrastructure;
using Grains;
using Grains.Persistence.States;
using Microsoft.AspNetCore.Authorization;

public class FusionAuthorizationMiddleware(IServiceProvider Services, IGrainFactory GrainFactory)
    : RpcInboundMiddleware(services: Services)
{
    public AsyncLocal<string> Token = new();

    public async override Task OnBeforeCall(RpcInboundCall call)
    {
        var existAttribute =
            call.MethodDef.Method.GetAttributes<AuthorizeAttribute>(inheritFromInterfaces: true, inheritFromBaseTypes: true).Count != 0;

        if (!existAttribute)
        {
            await base.OnBeforeCall(call: call);
            return;
        }

        var grain = GrainFactory.GetGrain<IFusionSession>(primaryKey: call.Context.Peer.Id);

        var state = await grain.GetState();
        if (state.IsAuthorized)
        {
            await base.OnBeforeCall(call: call);
            return;
        }

        call.Cancel();
    }
}

public class FusionServiceContext(IGrainFactory GrainFactory) : IFusionServiceContext
{
    public ValueTask<FusionSession> GetSessionState()
    {
        var current = RpcInboundContext.GetCurrent();
        var peerId  = current.Peer.Id;

        var grain = GrainFactory.GetGrain<IFusionSession>(primaryKey: peerId);

        return grain.GetState();
    }
}

public interface IFusionServiceContext
{
    ValueTask<FusionSession> GetSessionState();
}