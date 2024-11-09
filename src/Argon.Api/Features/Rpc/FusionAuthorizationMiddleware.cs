namespace Argon.Api.Features.Rpc;

using ActualLab.Reflection;
using ActualLab.Rpc.Infrastructure;
using Grains;
using Grains.States;
using Microsoft.AspNetCore.Authorization;

public class FusionAuthorizationMiddleware(IServiceProvider Services, IGrainFactory GrainFactory) : RpcInboundMiddleware(Services)
{
    public AsyncLocal<string> Token = new();

    public async override Task OnBeforeCall(RpcInboundCall call)
    {
        var existAttribute = call.MethodDef.Method.GetAttributes<AuthorizeAttribute>(true, true).Count != 0;

        if (!existAttribute)
        {
            await base.OnBeforeCall(call);
            return;
        }

        var grain = GrainFactory.GetGrain<IFusionSession>(call.Context.Peer.Id);

        var state = await grain.GetState();
        if (state.IsAuthorized)
        {
            await base.OnBeforeCall(call);
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

        var grain = GrainFactory.GetGrain<IFusionSession>(peerId);

        return grain.GetState();
    }
}

public interface IFusionServiceContext
{
    ValueTask<FusionSession> GetSessionState();
}