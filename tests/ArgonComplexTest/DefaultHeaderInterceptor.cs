namespace ArgonComplexTest;

using ion.runtime;

public class DefaultHeaderInterceptor : IIonInterceptor
{
    private static readonly Guid    SessionId = Guid.CreateVersion7();
    private static readonly Guid    MachineId = Guid.CreateVersion7();
    private                 string? AuthToken = null;

    public async Task InvokeAsync(IIonCallContext context, Func<IIonCallContext, CancellationToken, Task> next, CancellationToken ct)
    {
        context.RequestItems.Add("Sec-Ref", SessionId.ToString());
        context.RequestItems.Add("Sec-Ner", "1");
        context.RequestItems.Add("Sec-Carry", MachineId.ToString());

        if (!string.IsNullOrEmpty(AuthToken))
        {
            context.RequestItems.Add("Authorization", $"Bearer {AuthToken}");
        }

        await next(context, ct);
    }

    public void SetToken(string t) => AuthToken = t;
}