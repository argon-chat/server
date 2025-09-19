namespace Argon.Features.Sentry;

using global::Sentry;
using R3;
using Serilog.Context;

public class SentryGrainCallFilter : IIncomingGrainCallFilter
{
    public async Task Invoke(IIncomingGrainCallContext context)
    {
        var grainType = context.Grain.GetType().FullName;

        if (grainType?.StartsWith("Orleans") ?? false)
        {
            try
            {
                await context.Invoke();
            }
            catch (Exception ex)
            {
                SentrySdk.CaptureException(ex);
                throw;
            }
            return;
        }

        var userId       = context.GetUserId();
        var reentrancyId = context.GetReentrancyId();
        var grainId      = $"{Guid.AllBitsSet}";
        var callerGrain  = context.SourceId?.ToString();
        var methodName   = context.ImplementationMethod.Name;

        if (context.Grain is IGrainWithGuidKey guidKey)
            grainId = guidKey.GetPrimaryKey().ToString();
        if (context.Grain is IGrainWithStringKey strKey)
            grainId = strKey.GetPrimaryKeyString();
        if (context.Grain is IGrainWithIntegerKey intKey)
            grainId = intKey.GetPrimaryKeyLong().ToString();

        SentrySdk.ConfigureScope(scope =>
        {
            if (!string.IsNullOrEmpty(callerGrain))
                scope.SetTag("ActivatorGrainId", callerGrain);
            scope.SetTag("GrainId", grainId);
            scope.SetTag("GrainType", grainType ?? "unk");
            scope.SetTag("MethodName", methodName);
            if (userId is not null)
                scope.User = new SentryUser()
                {
                    Id = userId.Value.ToString()
                };
            if (reentrancyId is not null)
                scope.SetTag("ReentrancyId", reentrancyId.Value.ToString());
        });


        List<KeyValuePair<string, object>> properties =
        [
            new("userId", userId?.ToString() ?? "<anonymous>"),
            new("grainId", grainId),
            new("grainCall", $"{context.Grain.GetType().Name}:{methodName}")
        ];

        if (!string.IsNullOrEmpty(callerGrain))
            properties.Add(new("activatorGrainId", callerGrain));
        if (reentrancyId is not null)
            properties.Add(new("reentrancyId", reentrancyId.Value.ToString()));

        try
        {
            var disposables = properties
               .Select(p => LogContext.PushProperty(p.Key, p.Value))
               .ToArray();

            using (new CompositeDisposable(disposables))
            {
                await context.Invoke();
            }
        }
        catch (Exception ex)
        {
            SentrySdk.CaptureException(ex);
            throw;
        }
    }
}