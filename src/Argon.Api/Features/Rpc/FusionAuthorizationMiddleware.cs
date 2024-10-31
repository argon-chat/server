﻿namespace Argon.Api.Features.Rpc;

using ActualLab.Rpc.Infrastructure;
using ActualLab.Rpc;
using MemoryPack;
using Microsoft.Extensions.Caching.Distributed;
using ActualLab;
using ActualLab.Reflection;
using Grains;
using Grains.Persistence.States;
using Microsoft.AspNetCore.Authorization;
using Orleans;

public class FusionAuthorizationMiddleware(IServiceProvider Services, IGrainFactory GrainFactory) : RpcInboundMiddleware(Services)
{
    public AsyncLocal<string> Token = new AsyncLocal<string>();
    public override async Task OnBeforeCall(RpcInboundCall call)
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
        return;
    }
}

public class FusionServiceContext(IGrainFactory GrainFactory) : IFusionServiceContext
{
    public ValueTask<FusionSession> GetSessionState()
    {
        var current = RpcInboundContext.GetCurrent();
        var peerId = current.Peer.Id;

        var grain = GrainFactory.GetGrain<IFusionSession>(peerId);

        return grain.GetState();
    }
}

public interface IFusionServiceContext
{
    ValueTask<FusionSession> GetSessionState();
}