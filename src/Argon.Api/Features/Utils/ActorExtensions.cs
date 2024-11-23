namespace Argon.Api.Extensions;

public static class ActorExtensions
{
    public static void When<T>(this T t, Func<T, bool> filter, Action on)
    {
        if (filter(t)) on();
    }

    public async static Task WhenAsync<T>(this T t, Func<T, bool> filter, Func<ValueTask> on)
    {
        if (filter(t)) await on();
    }
}