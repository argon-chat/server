namespace Argon.Services;

using Api.Features.Orleans.Client;
using Features.Middlewares;
using Google.Protobuf;
using Grpc.Core;
using Transport;

public class ArgonTransport(IServiceProvider provider, ArgonDescriptorStorage storage, ILogger<ArgonTransport> logger)
    : Transport.ArgonTransport.ArgonTransportBase
{
    public async override Task<RpcResponse> Unary(RpcRequest request, ServerCallContext context)
    {
        await using var scope = provider.CreateAsyncScope();

        var registry = scope.ServiceProvider.GetRequiredService<IArgonDcRegistry>();


        var nearestDc = registry.GetNearestClusterClient();

        if (nearestDc is null)
        {
            context.Status = new Status(StatusCode.FailedPrecondition, "no dc online found");
            return new RpcResponse
            {
                Payload      = ByteString.Empty,
                StatusCode   = ArgonRpcStatusCode.Ok,
                ErrorMessage = "no dc online found"
            };
        }


        using var _ = scope.ServiceProvider.GetRequiredService<IServerTimingRecorder>()
           .BeginRecord($"Unary/{request.Interface}::{request.Method}");
        using var ctx     = ArgonTransportContext.CreateGrpc(context, provider, registry);
        var       service = storage.GetService(request.Interface);  

        try
        {
            var method = service.GetType().GetMethod(request.Method);
            if (method == null)
                throw new InvalidOperationException($"Method '{request.Method}' not found in service '{service.GetType().Name}'.");

            if (method.GetCustomAttribute<AllowAnonymousAttribute>() is null && !ctx.IsAuthorized)
                return new RpcResponse
                {
                    Payload    = ByteString.Empty,
                    StatusCode = ArgonRpcStatusCode.NotAuthorized,
                };

            var result = await InvokeServiceMethod(service, method, request.Payload);

            return new RpcResponse()
            {
                Payload    = ByteString.CopyFrom(result),
                StatusCode = ArgonRpcStatusCode.Ok,
            };
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "failed execute unary endpoint");
            return new RpcResponse
            {
                ErrorMessage  = e.Message,
                ExceptionType = e.GetType().FullName,
                Payload       = ByteString.Empty,
                StatusCode    = ArgonRpcStatusCode.InternalException
            };
        }
    }

    private async Task<byte[]> InvokeServiceMethod(IArgonService service, MethodInfo method, ByteString payload)
    {
        var parameters     = method.GetParameters();
        var parameterTypes = parameters.Select(p => p.ParameterType).ToArray();

        var arguments = MessagePackSerializer.Deserialize<object[]>(payload.Memory);

        if (arguments.Length != parameterTypes.Length)
            throw new InvalidOperationException(
                $"Method '{method.Name}' expects {parameterTypes.Length} arguments, but received {arguments.Length}."
            );

        var typedArguments = arguments
           .Zip(parameterTypes, (arg, type) => MessagePackSerializer.Deserialize(type, MessagePackSerializer.Serialize(arg))).ToArray();

        if (method.Invoke(service, typedArguments) is not Task task)
            throw new InvalidOperationException($"Method '{method.Name}' does not return Task.");

        await task.ConfigureAwait(false);

        if (method.ReturnType == typeof(Task))
            return [];

        var resultProperty = task.GetType().GetProperty("Result");
        if (resultProperty == null)
            throw new InvalidOperationException($"Task for method '{method.Name}' does not have a result.");

        var result = resultProperty.GetValue(task);

        return MessagePackSerializer.Serialize(result);
    }
}