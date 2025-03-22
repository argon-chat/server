namespace Argon.Extensions;

public static class ActorExtensions
{
    public static void When<T>(this T t, Func<T, bool> filter, Action on)
    {
        if (filter(t)) on();
    }

    public static T When<T>(this T t, Func<T, bool> filter, Action<T> on)
    {
        if (filter(t)) on(t);
        return t;
    }

    public async static Task WhenAsync<T>(this T t, Func<T, bool> filter, Func<ValueTask> on)
    {
        if (filter(t)) await on();
    }

    public static E As<T, E>(this T t)
    {
        if (t is E e)
            return e;
        throw new InvalidCastException();
    }
}