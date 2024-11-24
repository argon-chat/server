namespace Argon.Api.Features.Sentry;

public class SentryGrainCallFilter : IIncomingGrainCallFilter
{
    public async Task Invoke(IIncomingGrainCallContext context)
    {
        try
        {
            await context.Invoke();
        }
        catch (Exception ex)
        {
            SentrySdk.ConfigureScope(scope =>
            {
                var grainId    = context.TargetId.ToString();
                var calleGrain = context.SourceId?.ToString();
                var grainType  = context.Grain.GetType().FullName;

                var methodName = context.ImplementationMethod.Name;

                if (!string.IsNullOrEmpty(calleGrain))
                    scope.SetTag("ActivatorGrainId", calleGrain);
                scope.SetTag("GrainId", grainId);
                scope.SetTag("GrainType", grainType ?? "unk");
                scope.SetTag("MethodName", methodName);
            });

            SentrySdk.CaptureException(ex);
            throw;
        }
    }
}