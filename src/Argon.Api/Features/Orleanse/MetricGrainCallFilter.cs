namespace Argon.Api.Features;

using Metrics;

public class MetricGrainCallFilter(IMetricsCollector metrics) : IIncomingGrainCallFilter
{
    public async Task Invoke(IIncomingGrainCallContext context)
    {
        var grainName  = context.Grain.GetType().Name;
        var methodName = context.ImplementationMethod.Name;

        await metrics.TimeAsync(MeasurementId.GrainCallTiming, 
            grainName, methodName,
            async () => await context.Invoke());
    }
}
