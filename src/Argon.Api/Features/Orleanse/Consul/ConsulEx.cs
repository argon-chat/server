namespace Argon.Api.Features.Orleans.Consul;

using global::Consul;

internal static class ConsulEx
{
    public static void Assert(this WriteResult result)
    {
        if (result.StatusCode != HttpStatusCode.OK)
            throw new Exception();
    }
}